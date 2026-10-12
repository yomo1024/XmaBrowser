using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;
using XmaBrowser.Browser;
using XmaBrowser.Site;

namespace XmaBrowser.Ui;

/// <summary>
/// 缩略图/预览图缓存：
///   · 磁盘按 URL 哈希存文件（上限可配，超了按最久未访问淘汰）
///   · 纹理缓存在内存（失败会记住，但**会过期**，不再永久判死）
///   · Get() 永不阻塞绘制线程：没下载好就先返回 null，后台下完下一帧自然出现
///
/// 两条取图路：
///   1) 直连 HttpClient —— 公开缩略图基本都能拿到（快）
///   2) 直连被拒（403 / 返回网页而不是图片）→ 浏览器通道（带登录态，NSFW 的图只有这条路拿得到）
/// 但第 2 条**要调用点授权**（allowChannel）—— 列表一页 15 张如果全走通道会把通道占满，
/// 反过来把搜索和详情堵死。所以只有「用户正在看的那张预览图」才授权。
///
/// ⚠ 三个曾经的坑（都会让用户看到「进度条一直转、图永远不出来」）：
///   ① 同一张图被列表（只直连）和详情面板（允许通道）先后请求时，后到的请求被静默丢弃
///      → 用 <see cref="ImageJobState"/> 让后到的请求给在跑的任务**升级权限**；
///   ② 直连继承了 HttpClient 的 30 分钟超时，站点卡住就真的卡住
///      → 直连单独 6 秒超时；
///   ③ 整条任务没有总上限 → 现在 25 秒硬上限，超了必须给出失败原因（绝不无限转）。
/// </summary>
public sealed class ThumbCache : IDisposable
{
    /// <summary>失败后多久才允许自动重试同一个 URL。
    /// 原来失败是永久记住的 —— NSFW 的图第一次直连 403 之后，就再也取不到了。</summary>
    public const int FailureCooldownSeconds = ImageFetchLimits.FailureCooldownSeconds;

    /// <summary>**停滞**上限（毫秒）：多久没有新字节就判定卡住（不是"总时长"）。
    /// 每读到数据就重置 —— 见 FetchLimits.ImageStallMs 里的踩坑记录。</summary>
    public const int StallMs = ImageFetchLimits.StallMs;

    /// <summary>一张图的整条任务上限（毫秒）：直连 + 通道加起来超过这个数就报失败原因，绝不无限转。</summary>
    public const int JobTimeoutMs = ImageFetchLimits.JobTimeoutMs;

    private readonly HttpClient _http;
    private readonly ITextureProvider _textures;
    private readonly IPluginLog _log;
    private readonly int _maxBytes;
    private readonly string _dir;
    private readonly BrowserChannel? _channel;

    private readonly ImageJobState _jobs = new();
    private readonly ConcurrentDictionary<string, DateTime> _failedAt = new();
    private readonly ConcurrentDictionary<string, string> _status = new();
    private readonly ConcurrentDictionary<string, long> _dlGot = new();
    private readonly ConcurrentDictionary<string, long> _dlTotal = new();
    private readonly Dictionary<string, ISharedImmediateTexture?> _texturesByUrl = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastUse = new();
    private readonly CancellationTokenSource _cts = new();

    /// <summary>诊断信息出口（插件把它接到「关于」页的诊断日志上，出问题能把真实过程发出来）。</summary>
    public Action<string>? Diag { get; set; }

    public ThumbCache(HttpClient http, ITextureProvider textures, IPluginLog log, string dir, int maxMegabytes,
                      BrowserChannel? channel = null)
    {
        _http = http;
        _textures = textures;
        _log = log;
        _dir = dir;
        _maxBytes = Math.Max(16, maxMegabytes) * 1024 * 1024;
        _channel = channel;
        Directory.CreateDirectory(_dir);
    }

    /// <summary>绘制线程调用：拿纹理，拿不到就返回 null（并在后台开始下载）。
    /// allowChannel=true 表示这张图值得回退到浏览器通道（详情面板的预览图就是这样）。</summary>
    public ISharedImmediateTexture? Get(string url, bool allowChannel = false)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (_texturesByUrl.TryGetValue(url, out var cached)) return cached;

        if (_failedAt.TryGetValue(url, out var failedAt) &&
            (DateTime.UtcNow - failedAt).TotalSeconds < FailureCooldownSeconds)
            return null;   // 冷却期内不再自动重试（界面会给「重试」按钮）

        var path = PathFor(url);
        if (!File.Exists(path))
        {
            if (_jobs.Request(url, allowChannel) == ImageJobState.Ask.Begin)
                StartDownload(url, path);
            return null;
        }

        try
        {
            var tex = _textures.GetFromFile(path);
            _texturesByUrl[url] = tex;
            _lastUse[url] = DateTime.UtcNow;
            return tex;
        }
        catch (Exception e)
        {
            _log.Debug($"[XmaBrowser] 缩略图读不了（{Path.GetFileName(path)}）：{e.Message}");
            // ⚠ 读不出来多半是文件坏了/不完整：**把它删掉**，否则下次还会读到同一个坏文件，
            // 永远重试、永远失败（原来就是这样）。
            TryDelete(path);
            Fail(url, "缓存文件读不出（已删除，等会重试）");
            return null;
        }
    }

    /// <summary>
    /// 只重挂纹理，**文件不动**。
    ///
    /// 用于「文件已经在本地了，但纹理拿不到」这种状态 —— 也就是主人猜的
    /// 「其实已经加载好了，就是不显示」。这种情况绝不能把 1.4 MB 又下一遍。
    /// </summary>
    public void ReloadTexture(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        lock (_texturesByUrl) _texturesByUrl.Remove(url);
        _failedAt.TryRemove(url, out _);
        Note($"重挂纹理（不重下文件）：{Short(url)}");
    }

    /// <summary>
    /// 手动/自动重试一张图：清掉失败记忆与坏文件，并允许这次走浏览器通道。
    /// 详情面板发现图没出来时会自动调一次（每个 mod 只看一次），界面也给了按钮。
    /// </summary>
    public void Retry(string url, bool allowChannel = true)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        _failedAt.TryRemove(url, out _);
        _status.TryRemove(url, out _);
        lock (_texturesByUrl) _texturesByUrl.Remove(url);
        TryDelete(PathFor(url));

        if (_jobs.Request(url, allowChannel) == ImageJobState.Ask.Begin)
        {
            Note($"预览图重试（allowChannel={allowChannel}）：{Short(url)}");
            StartDownload(url, PathFor(url));
        }
    }

    /// <summary>给界面显示这张图现在什么状态（空 = 还没开始）。</summary>
    public string Status(string url)
        => string.IsNullOrWhiteSpace(url) ? string.Empty : (_status.TryGetValue(url, out var s) ? s : string.Empty);

    /// <summary>
    /// 这张图现在的**按字节**下载进度（0..1），返回 -1 表示还判断不出来。
    ///
    /// 为什么必须有：站点的全尺寸预览图是**整张大图**（本机实测一张 1496873 字节 ≈ 1.5 MB）。
    /// 只按「阶段」画进度的话，下载那几秒里进度条纹丝不动 —— 用户看到的就是「一直在加载、没成功」。
    /// 有了它就能显示「0.6 / 1.5 MB」，是真的在动。
    /// </summary>
    public float ImageProgress(string url, out long got, out long total)
    {
        got = 0;
        total = 0;
        if (string.IsNullOrWhiteSpace(url) || !_jobs.IsRunning(url)) return -1f;

        got = _dlGot.TryGetValue(url, out var g) ? g : 0;
        total = _dlTotal.TryGetValue(url, out var t) ? t : 0;
        if (total <= 0) return -1f;                     // 站点没给 Content-Length 就算不出比例
        var f = (float)((double)got / total);
        return f < 0f ? 0f : (f > 1f ? 1f : f);
    }

    private void Fail(string url, string why)
    {
        _failedAt[url] = DateTime.UtcNow;
        _status[url] = "失败：" + why;
        Note($"预览图失败：{why} ← {Short(url)}");
        _log.Warning($"[XmaBrowser] 取图失败（{why}）：{Short(url)}");
    }

    private void Note(string msg)
    {
        try { Diag?.Invoke("[图] " + msg); } catch { /* 忽略 */ }
    }

    private static string Short(string url) => url.Length <= 70 ? url : url[..70] + "…";

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 忽略 */ }
    }

    private void StartDownload(string url, string path)
    {
        _status[url] = "下载中…";

        _ = Task.Run(async () =>
        {
            // 整条任务的总上限：直连 + 通道加一起也不能无限等
            using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            jobCts.CancelAfter(JobTimeoutMs);

            var sw = Stopwatch.StartNew();
            var tmp = path + ".part";
            try
            {
                byte[]? bytes = null;
                string why = string.Empty;
                var route = string.Empty;

                // ① 直连（用**停滞检测**而不是总时长截断：实测一张整图要 9127 ms，
                //    拿总时长去截会把本来会成功的下载掐掉）
                try
                {
                    using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(jobCts.Token);
                    stallCts.CancelAfter(FetchLimits.ImageStallMs);

                    // ResponseHeadersRead + 手动读流：这样才能把**字节进度**报给界面。
                    // 站点的整张预览图实测 1.43 MB，全程没有反馈的话用户只会看到「一直在转」。
                    // 另外带上站点头：实测同一张图「裸请求 15670 ms / 带头 7349 ms」。
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.TryAddWithoutValidation("User-Agent", XmaUrls.UserAgent);
                    req.Headers.TryAddWithoutValidation("Referer", XmaUrls.Root + "/");
                    req.Headers.TryAddWithoutValidation("Accept", "image/avif,image/webp,image/*,*/*;q=0.8");

                    using var r = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, stallCts.Token)
                                             .ConfigureAwait(false);
                    var media = r.Content.Headers.ContentType?.MediaType ?? string.Empty;
                    if (r.IsSuccessStatusCode && !media.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
                    {
                        bytes = await ReadWithProgressAsync(r, url, stallCts, stallCts.Token).ConfigureAwait(false);
                        if (bytes is { Length: > 0 }) route = "直连";
                        else { bytes = null; why = "空响应"; }
                    }
                    else
                    {
                        why = r.IsSuccessStatusCode
                            ? $"返回的是网页（{media}）——多半是 403/人机校验"
                            : $"HTTP {(int)r.StatusCode}";
                    }
                }
                catch (OperationCanceledException) when (!jobCts.IsCancellationRequested)
                {
                    why = $"直连停滞 {FetchLimits.ImageStallMs} ms（没有任何新数据）";
                }
                catch (Exception e)
                {
                    why = e.GetType().Name;
                }

                // ② 直连没成 → 浏览器通道（**在决定回退时才问**当前的授权，
                //    这样列表先发起、详情面板随后授权的场景也能救回来）
                if (bytes is null && _channel is { Enabled: true } && _jobs.ChannelAllowed(url))
                {
                    _status[url] = "走浏览器通道取图中…";
                    _dlGot.TryRemove(url, out _);       // 通道是一次性拿回来的，没有字节进度
                    _dlTotal.TryRemove(url, out _);
                    Note($"直连没成功（{why}）→ 改走浏览器通道：{Short(url)}");
                    var chSw = Stopwatch.StartNew();
                    try
                    {
                        var viaChannel = await _channel.GetImageBytesAsync(url, jobCts.Token).ConfigureAwait(false);
                        chSw.Stop();
                        if (viaChannel is { Length: > 0 })
                        {
                            bytes = viaChannel;
                            route = $"通道 {chSw.ElapsedMilliseconds} ms";
                        }
                        else
                        {
                            why = $"{why}；通道：{_channel.LastError}";
                        }
                    }
                    catch (OperationCanceledException) when (!jobCts.IsCancellationRequested)
                    {
                        why = $"{why}；通道超时";
                    }
                }
                else if (bytes is null && _channel is { Enabled: true })
                {
                    why = $"{why}（未授权走浏览器通道 —— 列表缩略图不走通道）";
                }

                if (bytes is null)
                {
                    Fail(url, why.Length > 0 ? why : "没取到");
                    return;
                }

                await File.WriteAllBytesAsync(tmp, bytes, jobCts.Token).ConfigureAwait(false);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                _lastUse[url] = DateTime.UtcNow;
                _status[url] = route.StartsWith("通道", StringComparison.Ordinal)
                    ? "已缓存（浏览器通道）" : "已缓存";
                Note($"预览图成功：{bytes.Length} 字节，{route}，共 {sw.ElapsedMilliseconds} ms ← {Short(url)}");
                _log.Debug($"[XmaBrowser] 图片已缓存 {bytes.Length} 字节（{route}）");
            }
            catch (OperationCanceledException)
            {
                Fail(url, $"整条任务超过 {JobTimeoutMs} ms 仍未完成（已放弃，可点「重试」）");
            }
            catch (Exception e)
            {
                Fail(url, e.GetType().Name);
                _log.Debug($"[XmaBrowser] 缩略图下载失败：{e.Message}");
            }
            finally
            {
                TryDelete(tmp);
                _dlGot.TryRemove(url, out _);
                _dlTotal.TryRemove(url, out _);
                _jobs.Finish(url);          // 必须收尾，否则这个 URL 再也不会被下发
            }
        });
    }

    /// <summary>
    /// 边读边报字节数（界面就能显示「0.62 MB / 1.43 MB」而不是干等）。
    ///
    /// ⭐ 关键：这里是**停滞检测** —— 每读到数据就把 stall 计时重置。
    /// 只要字节还在流就不打断（实测一张整图要 9 秒多，总时长截断会误杀）。
    /// </summary>
    private async Task<byte[]?> ReadWithProgressAsync(HttpResponseMessage r, string url,
                                                      CancellationTokenSource stallCts, CancellationToken ct)
    {
        var total = r.Content.Headers.ContentLength ?? 0;
        _dlTotal[url] = total;
        _dlGot[url] = 0;

        using var ms = new MemoryStream(total is > 0 and < int.MaxValue ? (int)total : 1 << 16);
        await using var src = await r.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buf = new byte[1 << 16];
        int n;
        while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            ms.Write(buf, 0, n);
            _dlGot[url] = ms.Length;
            try { stallCts.CancelAfter(FetchLimits.ImageStallMs); } catch { /* 已取消/已释放就算了 */ }
        }
        if (ms.Length == 0) return null;
        Note($"直连下载完成 {ms.Length} 字节" + (total > 0 ? $"（站点声明 {total}）" : "（站点没给长度）"));
        return ms.ToArray();
    }

    /// <summary>磁盘缓存超上限时淘汰最久没访问的（在 Draw 里低频调用即可）。</summary>
    public void Trim()
    {
        try
        {
            var files = new DirectoryInfo(_dir).GetFiles("*.img");
            var total = files.Sum(f => f.Length);
            if (total <= _maxBytes) return;

            foreach (var f in files.OrderBy(f => f.LastWriteTimeUtc))
            {
                if (total <= _maxBytes) break;
                var size = f.Length;
                try
                {
                    f.Delete();
                    total -= size;
                }
                catch { /* 忽略 */ }
            }
            _log.Info($"[XmaBrowser] 缩略图缓存超限，已淘汰到 {total / 1048576.0:0.0} MB");
        }
        catch { /* 忽略 */ }
    }

    public string PathFor(string url)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..32].ToLowerInvariant();
        return Path.Combine(_dir, hash + ".img");
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
