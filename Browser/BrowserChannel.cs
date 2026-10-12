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

    public string LastError => _lastError;

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

    public async Task<string?> GetHtmlAsync(string url, CancellationToken ct = default)
    {
        var s = await GetSessionAsync(ct).ConfigureAwait(false);
        if (s is null) return null;

        await s.Cdp.SendAsync("Page.enable", null, ct).ConfigureAwait(false);
        await s.Cdp.SendAsync("Page.navigate", new { url }, ct).ConfigureAwait(false);

        // 等导航真的落在目标地址上并且加载完成（避免读到上一页）
        for (var i = 0; i < 120; i++)
        {
            ct.ThrowIfCancellationRequested();
            var r = await s.Cdp.SendAsync("Runtime.evaluate",
                new { expression = "location.href + '|' + document.readyState", returnByValue = true }, ct, 15_000)
                .ConfigureAwait(false);
            var v = CdpClient.Value(r) ?? "";
            var parts = v.Split('|');
            if (parts.Length == 2 && parts[1] == "complete" && parts[0].StartsWith(url.Split('?')[0], StringComparison.OrdinalIgnoreCase))
                break;
            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        var html = await s.Cdp.SendAsync("Runtime.evaluate",
            new { expression = "document.documentElement.outerHTML", returnByValue = true }, ct, 60_000)
            .ConfigureAwait(false);
        var outer = CdpClient.Value(html);
        _log.Info($"浏览器通道取页面 {url[..Math.Min(60, url.Length)]}… → {(outer?.Length ?? 0)} 字节");
        return outer;
    }

    /// <summary>让浏览器以「真实下载」的方式取文件（NSFW 的包只有这条路能拿）。</summary>
    public async Task<bool> DownloadAsync(string url, string destFile, Action<long, long>? progress,
                                          CancellationToken ct = default)
    {
        var s = await GetSessionAsync(ct).ConfigureAwait(false);
        if (s is null) return false;

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
                    return false;
                }

                progress?.Invoke(size, size);
                _log.Info($"浏览器通道下载完成：{Path.GetFileName(destFile)} {size} 字节");
                return true;
            }
            await Task.Delay(400, ct).ConfigureAwait(false);
        }

        _lastError = $"浏览器通道下载超时（{_cfg.BrowserTimeoutSec} 秒）：{url}";
        _log.Warn(_lastError);
        return false;
    }

    /// <summary>
    /// 让浏览器把图片取回来并**转成 JPEG**（canvas → dataURL）。
    /// 这里刻意走「真实浏览器」：站点全尺寸图在 Cloudflare 后面，直连会 403；
    /// 顺带把 WebP 之类统一成 JPEG —— Dalamud 的贴图加载不认 WebP。
    /// </summary>
    public async Task<byte[]?> GetImageBytesAsync(string url, CancellationToken ct = default)
    {
        var s = await GetSessionAsync(ct).ConfigureAwait(false);
        if (s is null) return null;

        await s.Cdp.SendAsync("Page.enable", null, ct).ConfigureAwait(false);
        await s.Cdp.SendAsync("Page.navigate", new { url }, ct).ConfigureAwait(false);

        // 等图片文档加载完（顶层是图片时，Chrome 会包一个 <img>）
        for (var i = 0; i < 120; i++)
        {
            ct.ThrowIfCancellationRequested();
            var probe = await s.Cdp.SendAsync("Runtime.evaluate",
                new { expression = "(()=>{const i=document.querySelector('img');return i&&i.complete&&i.naturalWidth>0?i.naturalWidth+'x'+i.naturalHeight:'';})()", returnByValue = true },
                ct, 15_000).ConfigureAwait(false);
            var v = CdpClient.Value(probe);
            if (!string.IsNullOrEmpty(v)) break;
            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        var expr = "(function(){const i=document.querySelector('img');if(!i||!i.naturalWidth)return '';" +
                   "const c=document.createElement('canvas');c.width=i.naturalWidth;c.height=i.naturalHeight;" +
                   "c.getContext('2d').drawImage(i,0,0);return c.toDataURL('image/jpeg',0.92);})()";
        var res = await s.Cdp.SendAsync("Runtime.evaluate",
            new { expression = expr, returnByValue = true }, ct, 60_000).ConfigureAwait(false);
        var dataUrl = CdpClient.Value(res);

        if (dataUrl is null || !dataUrl.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
        {
            _lastError = "浏览器没能读出图片内容";
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
        if (_session is { Alive: true }) return _session;
        _session?.Dispose();
        _session = null;

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

    private static bool ProfileHasXmaCookie(BrowserCandidate c)
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
        _session?.Dispose();
        _session = null;
        _http.Dispose();
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
