using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XmaBrowser.Site;

namespace XmaBrowser.Browser;

public sealed class BrowserCandidate
{
    public string Label { get; set; } = string.Empty;
    public string Exe { get; set; } = string.Empty;
    public string UserDataDir { get; set; } = string.Empty;
    public string CookieFile => Path.Combine(UserDataDir, "Default", "Network", "Cookies");
    public string LocalState => Path.Combine(UserDataDir, "Local State");
    public bool LooksUsable => Exe.Length > 0 && File.Exists(Exe) && File.Exists(CookieFile) && File.Exists(LocalState);
}

/// <summary>
/// 浏览器通道：需要登录态 / 被 Cloudflare 拦的内容（NSFW）交给本机浏览器去取。
///
/// 为什么必须这么绕（本机实测结论）：
///   · 带逐字一致的 Cookie + 浏览器头走纯 HTTP → NSFW 详情页 403、包直链 404
///   · 同样内容在浏览器里 → 200，包能下（实测 30.8MB 的 .ttmp2）
///   · Chrome 154 的 cookie 是 v20（App-Bound 加密），外部 DPAPI 解必失败 → 我们不自己解密，
///     而是把 Cookies + Local State 复制到插件自己的临时 profile，用**同一个浏览器 exe** 起 headless，
///     让浏览器替我们解密，再用 CDP 把明文 cookie / 页面 / 下载拿到手。
///   · 实测：浏览器正在运行时也能复制出 Cookie 库（共享读），用户不需要关浏览器。
/// </summary>
public sealed class BrowserChannel : IDisposable
{
    private readonly IBrowserSettings _cfg;
    private readonly IXmaLog _log;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private Session? _session;
    private string _lastError = string.Empty;
    private readonly SemaphoreSlim _opLock = new(1, 1);   // 通道操作串行（共用一个标签页）
    private volatile bool _disposed;
    private DateTime _lastOpUtc = DateTime.MinValue;

    public string LastError => _lastError;

    /// <summary>最近一次会话实际使用的浏览器（自检报告里要显示选了谁、profile 在哪）。</summary>
    public BrowserCandidate? LastUsedBrowser { get; private set; }

    public BrowserChannel(IBrowserSettings cfg, IXmaLog log)
    {
        _cfg = cfg;
        _log = log;
    }

    public bool Enabled => _cfg.EnableBrowserChannel;

    /// <summary>探测本机能用的浏览器（Chrome / Edge / CentBrowser / Brave）。</summary>
    public static List<BrowserCandidate> Detect()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        var list = new List<BrowserCandidate>
        {
            new() { Label = "Chrome", Exe = Path.Combine(pf, "Google", "Chrome", "Application", "chrome.exe"),
                    UserDataDir = Path.Combine(local, "Google", "Chrome", "User Data") },
            new() { Label = "Chrome(x86)", Exe = Path.Combine(pf86, "Google", "Chrome", "Application", "chrome.exe"),
                    UserDataDir = Path.Combine(local, "Google", "Chrome", "User Data") },
            new() { Label = "Edge", Exe = Path.Combine(pf86, "Microsoft", "Edge", "Application", "msedge.exe"),
                    UserDataDir = Path.Combine(local, "Microsoft", "Edge", "User Data") },
            new() { Label = "Edge(64)", Exe = Path.Combine(pf, "Microsoft", "Edge", "Application", "msedge.exe"),
                    UserDataDir = Path.Combine(local, "Microsoft", "Edge", "User Data") },
            new() { Label = "CentBrowser", Exe = Path.Combine(local, "CentBrowser", "Application", "chrome.exe"),
                    UserDataDir = Path.Combine(local, "CentBrowser", "User Data") },
            new() { Label = "Brave", Exe = Path.Combine(pf, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
                    UserDataDir = Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data") },
        };
        return list;
    }

    /// <summary>哪些候选浏览器里真的有 xivmodarchive 的登录态（用来优先挑）。</summary>
    public static List<BrowserCandidate> DetectUsable() => Detect().Where(c => c.LooksUsable).ToList();

    /// <summary>
    /// 按 URL 猜「页面可用的标志元素」：搜索页看有没有卡片、详情页看下载链/标题、其它看 body。
    /// 用它判断「可以取 HTML 了」，比死等 readyState=complete 快得多（站点带广告脚本）。
    /// </summary>
    internal static string WaitSelectorFor(string url)
    {
        if (url.Contains("/search", StringComparison.OrdinalIgnoreCase)) return ".mod-card";
        if (url.Contains("/modid/", StringComparison.OrdinalIgnoreCase)) return "#mod-download-link, h1.display-5";
        return "body";
    }

    /// <summary>
    /// 在浏览器会话上跑一个操作。两层保护：
    ///   1) **串行**：通道里所有操作共用同一个标签页，并发导航会互相打断（还会让对方读到错的页面）；
    ///   2) **自愈**：遇到 <see cref="CdpClosedException"/>（连接被关 / 被 Dispose / 浏览器进程没了）
    ///      就丢弃会话重建一次再试 —— 这正是 "WebSocket is in an invalid state ('Aborted')" 的修复路径。
    /// </summary>
    private async Task<T?> WithSessionAsync<T>(string what, Func<Session, CancellationToken, Task<T?>> op,
                                               CancellationToken ct) where T : class
    {
        if (_disposed) return null;

        await _opLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 空闲太久先把浏览器关掉：headless Chrome 常驻占上百 MB，闲着不划算。
            // 没有后台定时器，就在下一次要用通道时判断一次（简单、无额外线程）。
            var idleMinutes = _cfg.ChannelIdleMinutes;
            if (idleMinutes > 0 && _session is not null &&
                (DateTime.UtcNow - _lastOpUtc).TotalMinutes >= idleMinutes)
            {
                _log.Info($"浏览器通道空闲 {idleMinutes} 分钟 → 关闭会话释放内存");
                DropSessionIfAny();
            }

            for (var attempt = 1; attempt <= 2; attempt++)
            {
                var s = await GetSessionAsync(ct).ConfigureAwait(false);
                if (s is null) return null;

                try
                {
                    var value = await op(s, ct).ConfigureAwait(false);
                    _lastOpUtc = DateTime.UtcNow;
                    if (value is not null) _lastError = string.Empty;   // 成功了就把上次的错误清掉
                    return value;
                }
                catch (CdpClosedException e)
                {
                    _lastError = $"{what}：CDP 连接中断（{e.Message}）";
                    _log.Warn($"浏览器通道 {what} → {_lastError}；" +
                              (attempt == 1 ? "丢弃会话重建后重试" : "重建后仍失败"));
                    DropSession(s);
                    if (attempt == 2) return null;
                }
            }
            return null;
        }
        finally
        {
            _opLock.Release();
        }
    }

    /// <summary>丢弃指定会话（只清掉还指向它的字段，然后释放它）。</summary>
    private void DropSession(Session session)
    {
        if (ReferenceEquals(_session, session)) _session = null;
        try { session.Dispose(); } catch { /* 忽略 */ }
    }

    /// <summary>取页面 HTML（自动串行 + 断线自愈）。</summary>
    public Task<string?> GetHtmlAsync(string url, CancellationToken ct = default)
        => WithSessionAsync<string>("取页面", (s, token) => FetchHtmlCoreAsync(s, url, token), ct);

    private async Task<string?> FetchHtmlCoreAsync(Session s, string url, CancellationToken ct)
    {
        // 取页面「不算成功」的两种情形（实测都会出现，别当成“没有结果”）：
        //   · 空文档（几十字节）—— 导航没渲染出来
        //   · Cloudflare 挑战页（Just a moment，实测约 29KB！）—— 站点在做人机校验/限流
        // 两种都重试一次：挑战页常常过几秒自己就放行了。
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var outer = await NavigateAndGrabAsync(s, url, ct).ConfigureAwait(false);
            if (IsUsablePage(outer))
                return outer;

            var why = LoginStateDetector.LooksLikeChallenge(outer)
                ? "站点在做 Cloudflare 人机校验（页面是 Just a moment）"
                : $"页面没取到内容（{outer?.Length ?? 0} 字节）";
            _lastError = $"{why} —— 这不等于“没有结果”；稍后再试";
            _log.Warn($"浏览器通道取页面第 {attempt} 次没成功：{_lastError}（{url[..Math.Min(60, url.Length)]}）");

            if (attempt == 1) await Task.Delay(2500, ct).ConfigureAwait(false);   // 给挑战页一点时间自己放行
        }
        return null;
    }

    /// <summary>取回来的页面能不能用：不空、也不是 Cloudflare 挑战页。</summary>
    internal static bool IsUsablePage(string? html)
        => html is { Length: >= 200 } && !LoginStateDetector.LooksLikeChallenge(html);

    private async Task<string?> NavigateAndGrabAsync(Session s, string url, CancellationToken ct)
    {
        await s.Cdp.SendAsync("Page.enable", null, ct).ConfigureAwait(false);
        await s.Cdp.SendAsync("Page.navigate", new { url }, ct).ConfigureAwait(false);

        // 等「够用」而不是「彻底加载完」：
        // 站点带广告脚本（s.nitropay 等），readyState 到 complete 常要拖好几秒；
        // 我们只解析 DOM，所以等到「URL 落到目标 + 不是 loading + 关键元素出现」就收工。
        // 关键元素没出现也给个软上限（2.5s），别把没有该元素的页面卡死；硬上限 5s。
        var key = url.Split('?')[0];
        var selector = WaitSelectorFor(url);
        // 搜索页要等**够 15 张卡片**（站点一页 15 条）再取，否则可能只拿到半页；
        // 其它页面有 1 个关键元素就算够。
        var needCount = selector == ".mod-card" ? 15 : 1;
        // 多带一个 document.title：挑战页的标题就是 "Just a moment..."，可以立刻认出来别再干等。
        var readyExpr = "location.href + '|' + document.readyState + '|' + " +
                        $"document.querySelectorAll('{selector}').length + '|' + document.title";
        var hardDeadline = DateTime.UtcNow.AddSeconds(6);
        var softDeadline = DateTime.UtcNow.AddMilliseconds(2500);
        var emptyDeadline = DateTime.UtcNow.AddMilliseconds(1200);

        while (DateTime.UtcNow < hardDeadline)
        {
            ct.ThrowIfCancellationRequested();
            var r = await s.Cdp.SendAsync("Runtime.evaluate",
                new { expression = readyExpr, returnByValue = true }, ct, 15_000).ConfigureAwait(false);
            var v = CdpClient.Value(r) ?? string.Empty;
            var parts = v.Split('|');
            var urlMatched = parts.Length >= 3 && parts[0].StartsWith(key, StringComparison.OrdinalIgnoreCase);

            // 一认出挑战页就立刻收手（别白等 6 秒）——让上层去重试/报因
            if (urlMatched && parts.Length >= 4 && LoginStateDetector.LooksLikeChallenge(parts[3]))
                break;

            if (urlMatched && parts[1] != "loading")
            {
                var count = int.TryParse(parts[2], out var n) ? n : 0;
                // 注意：**不能**拿 readyState==complete 当收工条件 —— Page.navigate 之后会短暂存在
                // 一个「已完成但内容为空」的文档（实测抓到过 28 字节），那样会解析出 0 条。
                if (count >= needCount) break;
                if (count > 0 && DateTime.UtcNow >= softDeadline) break;
                // 完事了却一张卡片都没有（且给过一小会儿）→ 别再干等，交给上层的重试
                if (count == 0 && parts[1] == "complete" && DateTime.UtcNow >= emptyDeadline) break;
                if (DateTime.UtcNow >= hardDeadline) break;
            }
            await Task.Delay(120, ct).ConfigureAwait(false);
        }

        var html = await s.Cdp.SendAsync("Runtime.evaluate",
            new { expression = "document.documentElement.outerHTML", returnByValue = true }, ct, 60_000)
            .ConfigureAwait(false);
        var outer = CdpClient.Value(html);
        var took = DateTime.UtcNow - hardDeadline.AddSeconds(-5);   // 从进入本方法算起的大致耗时
        _log.Info($"浏览器通道取页面 {url[..Math.Min(60, url.Length)]}… → {outer?.Length ?? 0} 字节（约 {took.TotalMilliseconds:0} ms）");
        return outer;
    }

    /// <summary>让浏览器以「真实下载」的方式取文件（NSFW 的包只有这条路能拿）。</summary>
    public async Task<bool> DownloadAsync(string url, string destFile, Action<long, long>? progress,
                                          CancellationToken ct = default)
        => await WithSessionAsync<string>("下载",
                (s, token) => DownloadCoreAsync(s, url, destFile, progress, token), ct).ConfigureAwait(false) is not null;

    private async Task<string?> DownloadCoreAsync(Session s, string url, string destFile,
                                                  Action<long, long>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(s.DownloadDir);
        await s.Cdp.SendAsync("Browser.setDownloadBehavior",
            new { behavior = "allow", downloadPath = s.DownloadDir, eventsEnabled = true }, ct).ConfigureAwait(false);
        await s.Cdp.SendAsync("Page.navigate", new { url }, ct, 30_000).ConfigureAwait(false);

        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(30, _cfg.BrowserTimeoutSec));
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var done = Directory.EnumerateFiles(s.DownloadDir)
                                .Where(f => !f.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase))
                                .ToList();
            if (done.Count > 0)
            {
                var file = done.OrderByDescending(f => new FileInfo(f).Length).First();
                var size = new FileInfo(file).Length;
                if (size == 0) { await Task.Delay(300, ct).ConfigureAwait(false); continue; }

                try
                {
                    if (File.Exists(destFile)) File.Delete(destFile);
                    File.Move(file, destFile);
                }
                catch (Exception e)
                {
                    _lastError = $"下载完成但搬运失败：{e.Message}";
                    return null;
                }

                progress?.Invoke(size, size);
                _log.Info($"浏览器通道下载完成：{Path.GetFileName(destFile)} {size} 字节");
                return "ok";
            }
            await Task.Delay(400, ct).ConfigureAwait(false);
        }

        _lastError = $"浏览器通道下载超时（{_cfg.BrowserTimeoutSec} 秒）：{url}";
        _log.Warn(_lastError);
        return null;
    }

    /// <summary>
    /// 让浏览器把图片取回来并**转成 JPEG**（canvas → dataURL）。
    /// 这里刻意走「真实浏览器」：站点全尺寸图在 Cloudflare 后面，直连会 403；
    /// 顺带把 WebP 之类统一成 JPEG —— Dalamud 的贴图加载不认 WebP。
    /// </summary>
    public Task<byte[]?> GetImageBytesAsync(string url, CancellationToken ct = default)
        => WithSessionAsync<byte[]>("取图", (s, token) => FetchImageCoreAsync(s, url, token), ct);

    /// <summary>图片文档最多等这么久（毫秒）。原来是 120×250ms = 30 秒，实测失败也要白等 32270 ms，
    /// 而且这段时间一直占着通道锁 —— 用户那边的表现就是「读详情/搜索被堵住」。</summary>
    private const int ImageWaitMs = 6000;

    private async Task<byte[]?> FetchImageCoreAsync(Session s, string url, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await s.Cdp.SendAsync("Page.enable", null, ct).ConfigureAwait(false);
        await s.Cdp.SendAsync("Page.navigate", new { url }, ct).ConfigureAwait(false);

        // 等图片文档就绪 —— 但**要同时看这是什么文档**。
        // 关键教训（实测）：403 / 人机校验 / 错误页同样是「加载完成」，此时页面里根本没有 <img>，
        // 只盯 img 会白等满 30 秒（实测 32270 ms）才失败。
        // 所以把 document.contentType 一起取回来：text/* 就说明拿到的是一张网页，立刻收手。
        const string probe =
            "(()=>{const t=document.contentType||'';const i=document.querySelector('img');" +
            "const ok=i&&i.complete&&i.naturalWidth>0?(i.naturalWidth+'x'+i.naturalHeight):'';" +
            "return t+'|'+ok;})()";

        var deadline = DateTime.UtcNow.AddMilliseconds(ImageWaitMs);
        var ready = false;
        string? badDoc = null;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var r = await s.Cdp.SendAsync("Runtime.evaluate",
                new { expression = probe, returnByValue = true }, ct, 10_000).ConfigureAwait(false);
            var v = CdpClient.Value(r) ?? string.Empty;
            var parts = v.Split('|');
            var contentType = parts.Length > 0 ? parts[0] : string.Empty;

            if (parts.Length > 1 && parts[1].Length > 0) { ready = true; break; }

            // 文档是网页（text/html、text/plain…）→ 不可能是图片，别等了
            if (contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
            {
                badDoc = contentType;
                break;
            }
            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        if (!ready)
        {
            _lastError = badDoc is null
                ? $"图片没在 {ImageWaitMs} ms 内就绪（站点可能很慢或在校验）"
                : $"图片地址返回的是网页（{badDoc}，多半是 403 / 人机校验）";
            _log.Warn($"浏览器通道取图失败：{_lastError}（{url[..Math.Min(60, url.Length)]}，{sw.ElapsedMilliseconds} ms）");
            return null;
        }

        var expr = "(function(){const i=document.querySelector('img');if(!i||!i.naturalWidth)return '';" +
                   "const c=document.createElement('canvas');c.width=i.naturalWidth;c.height=i.naturalHeight;" +
                   "c.getContext('2d').drawImage(i,0,0);return c.toDataURL('image/jpeg',0.92);})()";
        var res = await s.Cdp.SendAsync("Runtime.evaluate",
            new { expression = expr, returnByValue = true }, ct, 20_000).ConfigureAwait(false);
        var dataUrl = CdpClient.Value(res);

        if (dataUrl is null || !dataUrl.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
        {
            _lastError = "浏览器没能读出图片内容";
            _log.Warn($"浏览器通道取图失败：{_lastError}（{url[..Math.Min(60, url.Length)]}，{sw.ElapsedMilliseconds} ms）");
            return null;
        }

        var idx = dataUrl.IndexOf("base64,", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) { _lastError = "图片数据格式异常"; return null; }

        try
        {
            var bytes = Convert.FromBase64String(dataUrl[(idx + 7)..]);
            _log.Info($"浏览器通道取图成功：{bytes.Length} 字节（已转 JPEG）");
            return bytes;
        }
        catch (Exception e)
        {
            _lastError = "图片解码失败：" + e.Message;
            return null;
        }
    }

    private async Task<Session?> GetSessionAsync(CancellationToken ct)
    {
        // 关键：不只看「进程还在」，还要看 **CDP 连接还活着**。
        // 只看进程的话，连接一旦断掉（浏览器崩了 / 连接被关 / 中途被 Dispose），
        // 就会一直复用死会话 → 每次调用都抛
        // "WebSocketException: The WebSocket is in an invalid state ('Aborted')"（主人报过）。
        if (_session is { Usable: true } alive) return alive;
        if (_session is { } dead)
        {
            _log.Warn("浏览器通道：上一个会话已失效（进程结束或 CDP 连接断开），重建一个新会话");
            DropSession(dead);
        }

        if (_disposed)
        {
            _lastError = "浏览器通道已关闭";
            return null;
        }

        if (!Enabled)
        {
            _lastError = "浏览器通道未启用";
            return null;
        }

        var candidate = PickCandidate();
        if (candidate is null)
        {
            _lastError = "没有找到可用的浏览器（需要已安装 Chrome/Edge/CentBrowser，且里面登录过 XIVModArchive）";
            return null;
        }

        // 整体超时：不让任何一步把调用方永久挂住（进程起不来 / 调试端口没开 / CDP 拒连…）
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_cfg.BrowserTimeoutSec, 30, 900)));

        try
        {
            _session = await Session.StartAsync(candidate, _cfg, _log, timeout.Token).ConfigureAwait(false);
            LastUsedBrowser = candidate;
            _lastError = string.Empty;
            return _session;
        }
        catch (OperationCanceledException)
        {
            _lastError = $"启动浏览器通道超时（{_cfg.BrowserTimeoutSec} 秒）";
            _log.Warn(_lastError);
            return null;
        }
        catch (Exception e)
        {
            _lastError = $"启动浏览器通道失败：{e.GetType().Name} {e.Message}";
            _log.Warn(_lastError);
            return null;
        }
    }

    private BrowserCandidate? PickCandidate()
    {
        var all = Detect();
        if (!string.IsNullOrWhiteSpace(_cfg.BrowserExe))
        {
            var custom = new BrowserCandidate
            {
                Label = "自定义",
                Exe = _cfg.BrowserExe,
                UserDataDir = !string.IsNullOrWhiteSpace(_cfg.BrowserUserDataDir)
                    ? _cfg.BrowserUserDataDir
                    : Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(_cfg.BrowserExe)!)!, "User Data"),
            };
            if (custom.LooksUsable) return custom;
            if (File.Exists(custom.Exe)) return custom;   // exe 在就用它（没有 cookie 也能取公开内容）
        }

        var usable = all.Where(c => c.LooksUsable).ToList();
        if (usable.Count > 0)
        {
            // 优先从「有 xivmodarchive cookie」的 profile 里挑（登录态在里面）
            foreach (var c in usable)
                if (ProfileHasXmaCookie(c)) { _log.Info($"浏览器通道使用 {c.Label}"); return c; }
            _log.Info($"浏览器通道使用 {usable[0].Label}（未见 XMA cookie，NSFW 可能取不到）");
            return usable[0];
        }

        if (!_cfg.AutoPickProfile)
            return all.FirstOrDefault(c => File.Exists(c.Exe));

        return all.FirstOrDefault(c => File.Exists(c.Exe));
    }

    /// <summary>
    /// 这个 profile 里有没有 xivmodarchive 的 cookie（Cookie 库的 host_key 是明文，扫一下就知道）。
    /// 只能判「有没有」，判不了「有没有过期」—— 真正的登录态由 LoginStateDetector 看页面得出。
    /// </summary>
    public static bool ProfileHasXmaCookie(BrowserCandidate c)
    {
        try
        {
            using var fs = new FileStream(c.CookieFile, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
            var buf = new byte[Math.Min(1 << 20, (int)Math.Min(fs.Length, 1 << 20))];
            var n = fs.Read(buf, 0, buf.Length);
            var text = System.Text.Encoding.Latin1.GetString(buf, 0, n);
            return text.Contains("xivmodarchive", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public void Dispose()
    {
        _disposed = true;
        DropSessionIfAny();
        _http.Dispose();
        // 刻意不 Dispose _opLock：万一还有操作在持有它，释放时会抛 ObjectDisposedException。
        // 一个 SemaphoreSlim 随插件卸载一起回收，代价可以忽略。
    }

    private void DropSessionIfAny()
    {
        if (_session is { } s) DropSession(s);
        _session = null;
    }

    /// <summary>一次浏览器会话：临时 profile + headless 进程 + CDP 连接。</summary>
    private sealed class Session : IDisposable
    {
        private readonly Process _process;
        private readonly string _profileDir;
        public CdpClient Cdp { get; }
        public string DownloadDir { get; }
        private readonly bool _cleanupProfile;
        private readonly IXmaLog _log;

        public bool Alive
        {
            get
            {
                try { return !_process.HasExited; }
                catch { return false; }
            }
        }

        /// <summary>
        /// 这个会话还能不能用：进程活着 **并且** CDP WebSocket 还是 Open。
        /// 只看进程是不够的 —— 连接可能先断（浏览器崩 / 连接被关 / 被 Dispose），
        /// 那样继续复用就会一路抛 "invalid state ('Aborted')"。
        /// </summary>
        public bool Usable => Alive && Cdp.IsOpen;

        private Session(Process process, CdpClient cdp, string profileDir, string downloadDir, bool cleanup, IXmaLog log)
        {
            _process = process;
            Cdp = cdp;
            _profileDir = profileDir;
            DownloadDir = downloadDir;
            _cleanupProfile = cleanup;
            _log = log;
        }

        public static async Task<Session> StartAsync(BrowserCandidate browser, IBrowserSettings cfg, IXmaLog log,
                                                     CancellationToken ct)
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), "XmaBrowser");
            var profileDir = Path.Combine(tempRoot, "profile");
            var downloadDir = Path.Combine(tempRoot, "downloads");
            SafeDelete(profileDir);
            SafeDelete(downloadDir);
            Directory.CreateDirectory(Path.Combine(profileDir, "Default", "Network"));
            Directory.CreateDirectory(downloadDir);

            // 借登录态：把 Cookies + Local State 复制过来（共享读，浏览器开着也能复制）
            if (!CopyShared(browser.LocalState, Path.Combine(profileDir, "Local State")))
                log.Warn("浏览器通道：Local State 复制失败");
            if (!CopyShared(browser.CookieFile, Path.Combine(profileDir, "Default", "Network", "Cookies")))
                log.Warn("浏览器通道：Cookies 复制失败（NSFW 可能取不到）");

            var port = FreePort();
            var args = new List<string>
            {
                "--headless=new",
                "--disable-gpu",
                "--no-first-run",
                "--no-default-browser-check",
                "--disable-extensions",
                $"--remote-debugging-port={port}",
                $"--user-data-dir={profileDir}",
                "about:blank",
            };

            var psi = new ProcessStartInfo(browser.Exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            var proc = Process.Start(psi) ?? throw new IOException("浏览器进程起不来");
            log.Info($"浏览器通道启动 {browser.Label} headless（PID {proc.Id}，端口 {port}）");

            var wsUrl = await WaitForCdpAsync(port, ct).ConfigureAwait(false);
            if (wsUrl is null)
            {
                try { proc.Kill(true); } catch { /* 忽略 */ }
                throw new IOException("等不到 CDP 调试端点");
            }

            var cdp = await CdpClient.ConnectAsync(wsUrl, ct).ConfigureAwait(false);
            return new Session(proc, cdp, profileDir, downloadDir, cfg.CleanupBorrowedProfile, log);
        }

        private static async Task<string?> WaitForCdpAsync(int port, CancellationToken ct)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            for (var i = 0; i < 80; i++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var json = await http.GetStringAsync($"http://127.0.0.1:{port}/json/list", ct).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(json);
                    foreach (var t in doc.RootElement.EnumerateArray())
                        if (t.TryGetProperty("type", out var ty) && ty.GetString() == "page" &&
                            t.TryGetProperty("webSocketDebuggerUrl", out var u))
                            return u.GetString();
                }
                catch { /* 还没起来 */ }
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
            return null;
        }

        public void Dispose()
        {
            try { Cdp.Dispose(); } catch { /* 忽略 */ }
            try { if (!_process.HasExited) _process.Kill(true); } catch { /* 忽略 */ }
            try { _process.Dispose(); } catch { /* 忽略 */ }

            if (_cleanupProfile)
            {
                SafeDelete(_profileDir);
                SafeDelete(DownloadDir);
            }
            _log.Info("浏览器通道已关闭");
        }

        private static void SafeDelete(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* 有文件还被占就先留着 */ }
        }

        private static bool CopyShared(string src, string dst)
        {
            try
            {
                using var s = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var d = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None);
                s.CopyTo(d);
                return new FileInfo(dst).Length > 0;
            }
            catch { return false; }
        }

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }
    }
}
