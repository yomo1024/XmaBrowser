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

    /// <summary>这个 profile 是**插件自己的**（用户在插件里登录过）。
    /// 它就地使用：不复制 cookie、退出时不删（登录态要留着）。</summary>
    public bool Owned { get; set; }

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

    /// <summary>诊断出口（插件把它接到「关于」页日志与日志文件上）。目的是"卡在哪一段"能直接看到。</summary>
    public Action<string>? Diag { get; set; }

    private void Note(string msg)
    {
        try { Diag?.Invoke("[通道] " + msg); } catch { /* 忽略 */ }
    }

    /// <summary>
    /// **预热**：提前把 headless 浏览器拉起来、连上 CDP，但**不访问站点**
    /// （打开 about:blank，不给站点多打一个请求）。
    ///
    /// 为什么要：通道取一页的固定成本里，会话冷启动实测约 1.1 秒
    /// （检测浏览器 60 ms → 拉起进程 → 等 CDP 端点 → 连 WebSocket）。
    /// NSFW 的详情、预览图都要走通道，所以「搜完 NSFW 就顺手预热」能让
    /// 后面每一次点开详情都省掉这 1 秒 —— 而且不需要额外请求。
    /// </summary>
    public async Task<bool> WarmUpAsync(CancellationToken ct = default)
    {
        if (_disposed || !Enabled) return false;
        if (_session is { Usable: true }) return true;      // 已经热着，什么都不用做

        try
        {
            await _opLock.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return false; }

        try
        {
            if (_session is { Usable: true }) return true;   // 等在锁上时别人已经热好了

            var candidate = PickCandidate();
            if (candidate is null) return false;

            if (_session is { } dead) DropSession(dead);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            _session = await Session.StartAsync(candidate, _cfg, _log, ct).ConfigureAwait(false);
            LastUsedBrowser = candidate;
            _lastOpUtc = DateTime.UtcNow;
            _log.Info($"浏览器通道预热完成：{sw.ElapsedMilliseconds} ms（{candidate.Label}，未访问站点）");
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception e)
        {
            _log.Debug($"浏览器通道预热失败（不影响后面的正式调用）：{e.GetType().Name} {e.Message}");
            return false;
        }
        finally
        {
            _opLock.Release();
        }
    }

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

    // ==================================================================== 插件自带的登录

    /// <summary>登录等待上限（分钟）：要到 Discord 那边输账号/过 2FA，给宽一点。</summary>
    public const int LoginWaitMinutes = 5;

    /// <summary>
    /// 挑战页重试前的等待（毫秒）。原来写死 2500 —— 站点挑战时那是纯等待，
    /// 一轮就是 2.5 秒 + 一次完整导航。实测挑战多半是瞬时的，1200 毫秒足够给它缓一下。
    /// </summary>
    internal const int ChallengeRetryDelayMs = 1200;

    /// <summary>
    /// 通道里屏蔽的广告/统计**域名**。
    /// 我们只用 DOM（卡片/标题/下载链接），而这些脚本会把页面「加载完成」拖到好几秒。
    /// ⚠ 刻意**不含** xivmodarchive.com（那是我们要取的页面与预览图），
    /// 也不含任何 consent/验证类域名（动它们可能把整个页面搞坏）。
    /// </summary>
    internal static readonly string[] BlockedHosts =
    [
        "googletagmanager.com",
        "google-analytics.com",
        "googlesyndication.com",
        "doubleclick.net",
        "nitropay.com",
        "adnxs.com",
        "amazon-adsystem.com",
        "criteo.com",
        "criteo.net",
        "taboola.com",
        "outbrain.com",
        "scorecardresearch.com",
        "quantserve.com",
        "pubmatic.com",
        "rubiconproject.com",
        "openx.net",
        "casalemedia.com",
        "sharethrough.com",
        "smaato.net",
        "adform.net",
        "33across.com",
        "gumgum.com",
        "smartadserver.com",
        "yieldmo.com",
        "triplelift.com",
        "adsrvr.org",
        "moatads.com",
    ];

    /// <summary>CDP 用的屏蔽模式（第二道防线，主要拦子资源）。</summary>
    internal static string[] AdPatterns => BlockedHosts.Select(h => $"*://*.{h}/*").ToArray();

    /// <summary>
    /// 把广告域名解析到 127.0.0.1（连不上的话立刻失败，比等超时强）。
    ///
    /// ⭐ 为什么用启动参数而不是只靠 CDP：实测 CDP 的 `Network.setBlockedURLs`
    /// **拦不住顶层导航** —— 直接导航到被屏蔽的域名照样能拿到内容（我的 A/B 测试当场抓到这个）。
    /// host-resolver 规则是解析层的，对子资源和顶层导航一视同仁。
    /// </summary>
    internal static string HostResolverRules =>
        string.Join(",", BlockedHosts.SelectMany(h => new[] { $"MAP *.{h} 127.0.0.1", $"MAP {h} 127.0.0.1" }));

    /// <summary>
    /// 让浏览器起得更快、更安静的开关。我们只读 DOM，所以这些都是安全的
    /// （不会改渲染、不会丢内容），纯粹少干无关的活。
    /// </summary>
    private static readonly string[] PerfArgs =
    [
        "--disable-background-networking",
        "--disable-component-update",
        "--disable-default-apps",
        "--disable-domain-reliability",
        "--disable-sync",
        "--metrics-recording-only",
        "--mute-audio",
        "--no-pings",
        "--no-service-autorun",
        "--disable-features=Translate,MediaRouter,OptimizationHints",
    ];

    /// <summary>插件自带 profile 的默认位置（用户没自定义时用它）。</summary>
    public static string DefaultOwnedProfileDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "XIVLauncher", "pluginConfigs", "XmaBrowser", "browser-profile");

    /// <summary>插件自带 profile 的目录（跟着登录态一起长期留着）。</summary>
    public string OwnedProfileDir =>
        !string.IsNullOrWhiteSpace(_cfg.ChannelProfileDir) ? _cfg.ChannelProfileDir : DefaultOwnedProfileDir;

    /// <summary>插件自带的 profile 里有没有 XIVModArchive 的 cookie（即「在插件里登录过」）。</summary>
    public bool OwnedProfileHasXmaCookie
        => BuildOwnedCandidate() is { } c && ProfileHasXmaCookie(c);

    /// <summary>
    /// 用本机任一可用浏览器的 exe + **插件自己的 profile 目录**拼一个候选。
    /// 这是「在插件里登录」的基础：登录态存这里，之后通道一直用它，不再碰用户的浏览器 profile。
    /// </summary>
    public BrowserCandidate? BuildOwnedCandidate()
    {
        var all = Detect();
        var exe = !string.IsNullOrWhiteSpace(_cfg.BrowserExe) && File.Exists(_cfg.BrowserExe)
            ? _cfg.BrowserExe
            : all.FirstOrDefault(c => File.Exists(c.Exe))?.Exe ?? string.Empty;
        if (exe.Length == 0) return null;

        var baseLabel = all.FirstOrDefault(c => string.Equals(c.Exe, exe, StringComparison.OrdinalIgnoreCase))?.Label
                        ?? "自定义";
        return new BrowserCandidate
        {
            Label = baseLabel + "·插件自带",
            Exe = exe,
            UserDataDir = OwnedProfileDir,
            Owned = true,
        };
    }

    /// <summary>
    /// 挑 profile 的顺序（纯逻辑，可离线自测）：
    ///   ① 插件自带的、且登录过 XIVModArchive 的 —— 最可靠，登录态是用户自己确认过的
    ///   ② 借来的、带 XMA cookie 的 —— 老路子，能用但说不清登录态是否还有效
    ///   ③ 其它
    /// 同级之间保持传入顺序（OrderBy 是稳定排序），所以探测顺序不会被打乱。
    /// </summary>
    internal static List<BrowserCandidate> RankCandidates(IEnumerable<BrowserCandidate> all,
                                                          Func<BrowserCandidate, bool> hasXmaCookie)
        => all.OrderByDescending(c => c.Owned && hasXmaCookie(c))
              .ThenByDescending(c => hasXmaCookie(c))
              .ToList();

    /// <summary>
    /// 登录探测用的 JS：把「当前在哪个站 + 有没有退出链接 + 有没有登录链接」一次带回来。
    /// 判定标志与 LoginStateDetector 一致（实测：已登录有 /logout；未登录有 Log In with Discord）。
    /// 用 innerHTML 做正则而不是选选器，因为站点那段作者标签的 HTML 是畸形的。
    /// </summary>
    internal const string LoginProbeJs =
        "(()=>{const h=location.host||'';const s=document.documentElement?document.documentElement.innerHTML:'';" +
        "return h+'|'+(/\\/logout/.test(s)?1:0)+'|'+(/Log In with Discord|href=\"\\/login\"/.test(s)?1:0);})()";

    /// <summary>探测结果说「确实登录上了」吗（纯函数，可离线自测）。</summary>
    internal static bool ProbeSaysLoggedIn(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var p = raw.Split('|');
        if (p.Length < 3) return false;
        if (!p[0].Contains("xivmodarchive", StringComparison.OrdinalIgnoreCase)) return false;   // 还在 Discord 那边就不算
        return p[1] == "1" && p[2] == "0";
    }

    /// <summary>当前是不是停在 Discord 的授权页（给用户一句提示用）。</summary>
    internal static bool ProbeIsOnDiscord(string? raw)
        => !string.IsNullOrWhiteSpace(raw) && raw.Contains("discord", StringComparison.OrdinalIgnoreCase);

    public sealed record LoginOutcome(bool Success, string Message, string BrowserLabel, string ProfileDir);

    /// <summary>
    /// **在插件里登录 XIVModArchive**。
    ///
    /// 为什么要有它：站点对 NSFW 的门控是「客户端身份」，而登录只有 Discord OAuth 一条路
    /// （没有密码表单、没有 API key）。原来的做法是复制用户浏览器 profile 里的 cookie ——
    /// 借得到就谢天谢地，借不到（没登录过 / cookie 锁着 / 过期了）插件也无从得知。
    ///
    /// 现在：开一个**看得见的**浏览器窗口，带着插件自己的 profile 打开站点的登录页，
    /// 用户在里面过一次 Discord 授权，插件轮询页面直到检测到登录成功，然后优雅关闭浏览器
    /// （必须是优雅关闭 —— 强杀进程可能让 Chrome 来不及把 cookie 落盘），登录态留在 profile 里长期复用。
    /// </summary>
    public async Task<LoginOutcome> LoginAsync(Action<string>? progress = null, CancellationToken ct = default,
                                               int waitMinutes = LoginWaitMinutes)
    {
        var say = progress ?? (_ => { });

        var browser = BuildOwnedCandidate();
        if (browser is null)
            return new LoginOutcome(false, "没找到可用的浏览器（本机需要有 Chrome / Edge / CentBrowser）", "", "");

        var dir = OwnedProfileDir;
        try { Directory.CreateDirectory(Path.Combine(dir, "Default", "Network")); }
        catch (Exception e) { return new LoginOutcome(false, $"建不了 profile 目录：{e.Message}", browser.Label, dir); }

        Session? session = null;
        // 探测到的最后一帧页面状态：取消/超时时也要带出来，否则出问题没法自查
        var lastRaw = string.Empty;
        try
        {
            say($"[1/3] 正在启动浏览器（{browser.Label}）…");
            session = await Session.StartAsync(browser, _cfg, _log, ct, headless: false,
                                               startUrl: XmaUrls.Login).ConfigureAwait(false);
            say("[2/3] 浏览器已打开 XIVModArchive 登录页：请在弹出的窗口里点「Log In with Discord」" +
                $"并完成授权（最多等 {waitMinutes} 分钟）。");

            var deadline = DateTime.UtcNow.AddMinutes(Math.Max(1, waitMinutes));
            var toldDiscord = false;

            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();

                var probe = await session.Cdp.SendAsync("Runtime.evaluate",
                    new { expression = LoginProbeJs, returnByValue = true }, ct, 15_000).ConfigureAwait(false);
                var raw = CdpClient.Value(probe) ?? string.Empty;
                if (raw.Length > 0) lastRaw = raw;

                if (ProbeSaysLoggedIn(raw))
                {
                    say("[3/3] 检测到已登录，正在把登录态写入插件 profile…");
                    // 给站点和浏览器一点时间把 session cookie 落盘，再**优雅**关闭
                    await Task.Delay(2000, ct).ConfigureAwait(false);
                    await session.CloseGracefullyAsync().ConfigureAwait(false);
                    session = null;
                    DropSessionIfAny();
                    _log.Info($"浏览器通道：插件登录成功，登录态已存到 {dir}");
                    return new LoginOutcome(true,
                        $"登录成功！登录态已存到插件自己的 profile，以后 NSFW 内容直接用这个身份。\n（{dir}）",
                        browser.Label, dir);
                }

                if (!toldDiscord && ProbeIsOnDiscord(raw))
                {
                    toldDiscord = true;
                    say("[2/3] 已经在 Discord 的授权页面了 —— 完成登录后这里会自动检测到，不用管我。");
                }

                await Task.Delay(1500, ct).ConfigureAwait(false);
            }

            return new LoginOutcome(false,
                $"等了 {LoginWaitMinutes} 分钟还没检测到登录成功（当前页面状态：{lastRaw}）。\n" +
                "可以再试一次，或在弹出的窗口里确认是不是把 Discord 授权走完了。",
                browser.Label, dir);
        }
        catch (OperationCanceledException)
        {
            return new LoginOutcome(false,
                $"登录已取消（最后一帧页面状态：{(lastRaw.Length > 0 ? lastRaw : "还没读到页面")}）",
                browser.Label, dir);
        }
        catch (Exception e)
        {
            _log.Warn($"浏览器通道：登录流程出错 {e.GetType().Name} {e.Message}");
            return new LoginOutcome(false, $"登录失败：{e.GetType().Name} {e.Message}", browser.Label, dir);
        }
        finally
        {
            if (session is not null)
            {
                try { await session.CloseGracefullyAsync().ConfigureAwait(false); } catch { /* 忽略 */ }
                session.Dispose();
            }
        }
    }

    /// <summary>
    /// 用**插件自带的 profile** 打开站点，看站点到底认不认这个身份。
    ///
    /// 为什么不能只看「cookie 库里有没有 xivmodarchive」：cookie 在 ≠ 会话还有效
    /// （过期了、被站点踢了、Discord 那边撤销授权了，cookie 都还在）。
    /// 真正的判据只有站点给的页面（有没有 /logout），这正是 LoginStateDetector 干的事。
    ///
    /// 它单起一个短命会话、用完就优雅关掉：共用主会话的话会跟别的通道操作抢 profile。
    /// </summary>
    public async Task<string> CheckLoginAsync(CancellationToken ct = default)
    {
        var owned = BuildOwnedCandidate();
        if (owned is null) return "没找到可用的浏览器";
        if (!File.Exists(owned.CookieFile)) return "未登录（插件 profile 里还没有站点 cookie）";

        // 让开主会话：同一个 profile 不能被两个 Chrome 实例同时用
        DropSessionIfAny();

        Session? s = null;
        try
        {
            s = await Session.StartAsync(owned, _cfg, _log, ct, headless: true).ConfigureAwait(false);
            var html = await FetchHtmlCoreAsync(s, XmaUrls.Root, ct).ConfigureAwait(false);
            if (html is null) return "取不到页面：" + _lastError;

            var facts = LoginStateDetector.Analyze(html);
            return facts.State switch
            {
                XmaLoginState.LoggedIn => "已登录（插件自带的身份有效）",
                XmaLoginState.LoggedOut => "未登录（cookie 在，但站点不认这个会话了）",
                XmaLoginState.CloudflareChallenge => "被 Cloudflare 挑战拦了 —— 这不是登录问题，稍后再试",
                _ => "认不出来（页面里既没有 /logout 也没有登录链接）",
            };
        }
        catch (OperationCanceledException) { return "检查被取消"; }
        catch (Exception e) { return $"检查失败：{e.GetType().Name} {e.Message}"; }
        finally
        {
            if (s is not null)
            {
                try { await s.CloseGracefullyAsync().ConfigureAwait(false); } catch { /* 忽略 */ }
                s.Dispose();
            }
        }
    }

    /// <summary>清除插件里的登录（删掉自带 profile）。浏览器还开着的话要先关掉。</summary>
    public string ClearLogin()
    {
        DropSessionIfAny();
        var dir = OwnedProfileDir;
        try
        {
            if (!Directory.Exists(dir)) return "插件里本来就没有登录态（profile 目录不存在）。";
            Directory.Delete(dir, true);
            _log.Info($"浏览器通道：已清除插件登录（删除 {dir}）");
            return "已清除插件里的登录态。";
        }
        catch (Exception e)
        {
            return $"清除失败：{e.Message}（如果那个浏览器窗口还开着，先关掉它再试）";
        }
    }

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

        // ⭐ 等锁必须**有上限**：锁是串行的，如果前面一个操作卡住了，
        // 后面所有请求就会无限排队 —— 用户看到的就是"点进去一直不动"。
        var acquired = false;
        var lockSw = System.Diagnostics.Stopwatch.StartNew();
        using var lockCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lockCts.CancelAfter(FetchLimits.OpLockWaitMs);
        try
        {
            try
            {
                await _opLock.WaitAsync(lockCts.Token).ConfigureAwait(false);
                acquired = true;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _lastError = $"通道忙：等操作锁超过 {FetchLimits.OpLockWaitMs} ms（有一个更早的通道操作卡住了）";
                _log.Warn("浏览器通道 " + _lastError);
                Note(_lastError);
                return null;
            }
            lockSw.Stop();
            if (lockSw.ElapsedMilliseconds > 1000) Note($"等通道锁 {lockSw.ElapsedMilliseconds} ms（被前面的操作占着）");

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
            if (acquired) _opLock.Release();
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

            if (attempt == 1) await Task.Delay(ChallengeRetryDelayMs, ct).ConfigureAwait(false);   // 给挑战页一点时间自己放行
        }
        return null;
    }

    /// <summary>取回来的页面能不能用：不空、也不是 Cloudflare 挑战页。</summary>
    internal static bool IsUsablePage(string? html)
        => html is { Length: >= 200 } && !LoginStateDetector.LooksLikeChallenge(html);

    private async Task<string?> NavigateAndGrabAsync(Session s, string url, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await s.EnsureAdBlockingAsync(ct).ConfigureAwait(false);
        await s.Cdp.SendAsync("Page.enable", null, ct, FetchLimits.PageEnableMs).ConfigureAwait(false);
        await s.Cdp.SendAsync("Page.navigate", new { url }, ct, FetchLimits.PageNavigateMs).ConfigureAwait(false);

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
                new { expression = readyExpr, returnByValue = true }, ct, FetchLimits.EvalProbeMs).ConfigureAwait(false);
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
            new { expression = "document.documentElement.outerHTML", returnByValue = true }, ct, FetchLimits.GrabHtmlMs)
            .ConfigureAwait(false);
        var outer = CdpClient.Value(html);
        // 之前这里用 "hardDeadline.AddSeconds(-5)" 反推耗时 —— 硬上限改过就失准，纯属瞎算。
        // 现在老老实实拿秒表量（从这个方法进门算起）。
        _log.Info($"浏览器通道取页面 {url[..Math.Min(60, url.Length)]}… → {outer?.Length ?? 0} 字节（{sw.ElapsedMilliseconds} ms）");
        Note($"取页面 {sw.ElapsedMilliseconds} ms → {outer?.Length ?? 0} 字节 ← {url[..Math.Min(70, url.Length)]}");
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
        await s.EnsureAdBlockingAsync(ct).ConfigureAwait(false);
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

        // 整体超时：不让任何一步把调用方永久挂住。
        // ⭐ 而且**要封顶**：原来直接用 BrowserTimeoutSec（默认 180 秒），
        // 起不来的话用户就得干等三分钟 —— 这是"卡一分钟"的主要来源之一。
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var budgetMs = Math.Min(Math.Clamp(_cfg.BrowserTimeoutSec, 30, 900) * 1000, FetchLimits.SessionStartBudgetMs);
        timeout.CancelAfter(budgetMs);

        var startSw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            _session = await Session.StartAsync(candidate, _cfg, _log, timeout.Token).ConfigureAwait(false);
            LastUsedBrowser = candidate;
            _lastError = string.Empty;
            startSw.Stop();
            Note($"起会话 {startSw.ElapsedMilliseconds} ms（{candidate.Label}）");
            return _session;
        }
        catch (OperationCanceledException)
        {
            startSw.Stop();
            _lastError = $"启动浏览器通道超时（{budgetMs} ms 预算用尽）";
            _log.Warn(_lastError);
            Note(_lastError);
            return null;
        }
        catch (Exception e)
        {
            startSw.Stop();
            _lastError = $"启动浏览器通道失败（{startSw.ElapsedMilliseconds} ms）：{e.GetType().Name} {e.Message}";
            _log.Warn(_lastError);
            Note(_lastError);
            return null;
        }
    }

    private BrowserCandidate? PickCandidate()
    {
        var all = Detect();

        // ① 用户显式指定的浏览器 + profile（尊重设置，不动它）
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

        // ② 候选池 = 插件自带的 profile + 本机探测到的浏览器，按「谁最可能有登录态」排序：
        //    插件自带的（用户亲手登录过）> 借来的带 XMA cookie 的 > 其它
        var pool = new List<BrowserCandidate>();
        if (BuildOwnedCandidate() is { } owned) pool.Add(owned);
        pool.AddRange(all);

        var ranked = RankCandidates(pool, ProfileHasXmaCookie);
        var fresh = ranked.FirstOrDefault(c => c.LooksUsable || (c.Owned && File.Exists(c.Exe)));
        if (fresh is not null)
        {
            if (fresh.Owned)
                _log.Info($"浏览器通道使用插件自带的登录态（{fresh.UserDataDir}）");
            else if (ProfileHasXmaCookie(fresh))
                _log.Info($"浏览器通道使用 {fresh.Label}（借它的登录态）");
            else
                _log.Info($"浏览器通道使用 {fresh.Label}（未见 XMA cookie，NSFW 可能取不到）");
            return fresh;
        }

        // ③ 实在没有能用的 profile：至少把 exe 找出来（只能取公开内容）
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
        private readonly bool _blockAds;
        private readonly IXmaLog _log;
        private bool _blockAdsSet;

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

        private Session(Process process, CdpClient cdp, string profileDir, string downloadDir, bool cleanup,
                        IXmaLog log, bool blockAds)
        {
            _process = process;
            Cdp = cdp;
            _profileDir = profileDir;
            DownloadDir = downloadDir;
            _cleanupProfile = cleanup;
            _log = log;
            _blockAds = blockAds;
        }

        /// <summary>
        /// 屏蔽广告/统计脚本（每个会话设一次）。
        /// 我们只用 DOM，而站点的广告脚本会把页面「加载完成」拖到好几秒 —— 屏蔽后 DOM 该有的还在，
        /// 页面却早得多稳定下来。设不上也不影响正确性，所以失败只记 Debug。
        /// </summary>
        public async Task EnsureAdBlockingAsync(CancellationToken ct)
        {
            if (!_blockAds || _blockAdsSet) return;
            _blockAdsSet = true;
            try
            {
                await Cdp.SendAsync("Network.enable", null, ct, 10_000).ConfigureAwait(false);
                await Cdp.SendAsync("Network.setBlockedURLs", new { urls = AdPatterns }, ct, 10_000)
                         .ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _log.Debug($"浏览器通道：广告屏蔽没设上（不影响正确性）：{e.Message}");
            }
        }

        public static async Task<Session> StartAsync(BrowserCandidate browser, IBrowserSettings cfg, IXmaLog log,
                                                     CancellationToken ct, bool headless = true,
                                                     string? startUrl = null)
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), "XmaBrowser");
            var downloadDir = Path.Combine(tempRoot, "downloads");
            SafeDelete(downloadDir);
            Directory.CreateDirectory(downloadDir);

            string profileDir;
            var cleanup = false;

            if (browser.Owned)
            {
                // 插件自带的 profile：**就地使用**。
                // 不复制 cookie（它自己就是登录态的存放处），退出时也不删 —— 删了就得重新登录。
                profileDir = browser.UserDataDir;
                Directory.CreateDirectory(Path.Combine(profileDir, "Default", "Network"));
                log.Info($"浏览器通道：使用插件自带 profile（{profileDir}）");
            }
            else
            {
                profileDir = Path.Combine(tempRoot, "profile");
                SafeDelete(profileDir);
                Directory.CreateDirectory(Path.Combine(profileDir, "Default", "Network"));

                // 借登录态：把 Cookies + Local State 复制过来（共享读，浏览器开着也能复制）
                if (!CopyShared(browser.LocalState, Path.Combine(profileDir, "Local State")))
                    log.Warn("浏览器通道：Local State 复制失败");
                if (!CopyShared(browser.CookieFile, Path.Combine(profileDir, "Default", "Network", "Cookies")))
                    log.Warn("浏览器通道：Cookies 复制失败（NSFW 可能取不到）");

                cleanup = cfg.CleanupBorrowedProfile;
            }

            var port = FreePort();
            var args = new List<string>();
            if (headless) args.Add("--headless=new");
            // 注意：登录窗口是**可见**的（headless 时下面这些参数照样无害）
            args.Add("--disable-gpu");
            args.Add("--no-first-run");
            args.Add("--no-default-browser-check");
            args.Add("--disable-extensions");
            args.AddRange(PerfArgs);                      // 起得更快、更安静（我们只读 DOM）
            if (cfg.ChannelBlockAds) args.Add("--host-resolver-rules=" + HostResolverRules);
            args.Add($"--remote-debugging-port={port}");
            args.Add($"--user-data-dir={profileDir}");
            args.Add(startUrl ?? "about:blank");

            var psi = new ProcessStartInfo(browser.Exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            var proc = Process.Start(psi) ?? throw new IOException("浏览器进程起不来");
            log.Info($"浏览器通道启动 {browser.Label}{(headless ? " headless" : "（可见窗口）")}（PID {proc.Id}，端口 {port}）");

            var wsUrl = await WaitForCdpAsync(port, ct).ConfigureAwait(false);
            if (wsUrl is null)
            {
                try { proc.Kill(true); } catch { /* 忽略 */ }
                throw new IOException("等不到 CDP 调试端点");
            }

            var cdp = await CdpClient.ConnectAsync(wsUrl, ct).ConfigureAwait(false);
            return new Session(proc, cdp, profileDir, downloadDir, cleanup, log, cfg.ChannelBlockAds);
        }

        /// <summary>
        /// 优雅关闭：先发 Browser.close 让 Chrome 正常退出。
        /// 为什么必须优雅 —— 登录流程结束后要保证 session cookie 真的落盘；
        /// 直接 Kill 进程可能让它来不及写，结果就是「明明登录成功了，下次还是没登录态」。
        /// </summary>
        public async Task CloseGracefullyAsync()
        {
            try { await Cdp.SendAsync("Browser.close", null, CancellationToken.None, 5000).ConfigureAwait(false); }
            catch { /* CDP 可能已经因为浏览器退出而断开，正常 */ }

            for (var i = 0; i < 50; i++)
            {
                try { if (_process.HasExited) break; } catch { break; }
                await Task.Delay(100).ConfigureAwait(false);
            }
        }

        private static async Task<string?> WaitForCdpAsync(int port, CancellationToken ct)
        {
            // 单次探测 1 秒（原来 3 秒）+ 总时长封顶（原来 80×3s 可以拖到几分钟）
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(FetchLimits.WaitForCdpProbeMs) };
            var deadline = DateTime.UtcNow.AddMilliseconds(FetchLimits.WaitForCdpTotalMs);
            while (DateTime.UtcNow < deadline)
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
                await Task.Delay(200, ct).ConfigureAwait(false);
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
