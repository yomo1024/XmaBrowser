using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace XmaBrowser.Site;

// 说明：XmaAccessException（带原因）与判定逻辑在 XmaBlock.cs —— 那里是唯一的一份声明。

public sealed class DownloadResult
{
    public string FilePath { get; set; } = string.Empty;
    public long Bytes { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public bool LooksLikeZip { get; set; }
}

/// <summary>XIVModArchive 的直连客户端（公开内容）。NSFW 走浏览器通道，不在这里。</summary>
public sealed class XmaClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly IXmaLog _log;
    private readonly int _minIntervalMs;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastRequestUtc = DateTime.MinValue;

    public string CacheDirectory { get; }

    public XmaClient(HttpClient http, IXmaSettings settings, string cacheDirectory, IXmaLog log)
    {
        _http = http;
        _log = log;
        _minIntervalMs = Math.Max(0, settings.RequestDelayMs);
        CacheDirectory = cacheDirectory;
        Directory.CreateDirectory(CacheDirectory);
    }

    public Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken ct = default)
        => Task.Run(async () =>
        {
            var url = XmaUrls.Search(query);
            var html = await GetStringAsync(url, ct).ConfigureAwait(false);
            var result = XmaParser.ParseSearch(html, query.Page, url);
            result.Url = url;

            // 0 条时把**原因**查出来（页面像不像人机校验 / 未登录页 / 就是个空结果页）。
            // 「搜不出来」不能只有一句「没有结果」—— 那样用户和我都无从下手。
            if (result.Hits.Count == 0)
            {
                result.Diagnostic = EmptyResultDiagnosis.Describe(html, url);
                _log.Warn($"[XmaBrowser] 搜索 0 条：{result.Diagnostic}");
            }

            _log.Info($"搜索「{query.Text}」第 {query.Page} 页 → 解析到 {result.Hits.Count} 条" +
                      $"（站点提示共 {result.TotalMods} 个 Mod）｜{url}");
            return result;
        }, ct);

    public Task<ModDetail> DetailAsync(string modId, CancellationToken ct = default)
        => Task.Run(async () =>
        {
            var url = XmaUrls.ModPage(modId);
            var html = await GetStringAsync(url, ct).ConfigureAwait(false);
            var detail = XmaParser.ParseDetail(html, modId, url);
            _log.Info($"详情 {modId}：{detail.Title} / {detail.Author}，下载链接 {(detail.DownloadHref.Length > 0 ? "有" : "无")}");
            return detail;
        }, ct);

    /// <summary>
    /// 下载到 destFile。站内 /private/ 直链匿名可下（实测 206 + PK 头）。
    /// progress 回调参数为 (已下载字节, 总字节)；总字节为 0 表示站点没给 Content-Length。
    /// </summary>
    public async Task<DownloadResult> DownloadAsync(string url, string destFile, Action<long, long>? progress = null,
                                                    CancellationToken ct = default)
    {
        using var request = BuildRequest(url);
        using var response = await SendThrottledAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        // 失败时把错误体读出来再判定原因：只靠状态码会把「人机校验」误报成「需要登录态」
        if (!response.IsSuccessStatusCode)
        {
            var body = await ReadErrorBodyAsync(response, ct).ConfigureAwait(false);
            ThrowIfBad(response, url, body);
        }

        var total = response.Content.Headers.ContentLength ?? 0;
        Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);

        long got = 0;
        var buffer = new byte[1 << 17];
        var head = new byte[4];
        var headLen = 0;

        await using (var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var dst = new FileStream(destFile, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 17, true))
        {
            int n;
            while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                if (headLen < 4)
                {
                    var take = Math.Min(4 - headLen, n);
                    Array.Copy(buffer, 0, head, headLen, take);
                    headLen += take;
                }
                await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                got += n;
                progress?.Invoke(got, total);
            }
        }

        var isZip = headLen >= 4 && head[0] == 0x50 && head[1] == 0x4B;
        _log.Info($"下载完成 {Path.GetFileName(destFile)}：{got} 字节，ZIP 容器={isZip}");
        return new DownloadResult
        {
            FilePath = destFile,
            Bytes = got,
            ContentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty,
            LooksLikeZip = isZip,
        };
    }

    public async Task<string> GetStringAsync(string url, CancellationToken ct = default)
    {
        using var request = BuildRequest(url);
        using var response = await SendThrottledAsync(request, HttpCompletionOption.ResponseContentRead, ct)
            .ConfigureAwait(false);
        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        ThrowIfBad(response, url, text);
        return text;
    }

    private HttpRequestMessage BuildRequest(string url, string? range = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        // 这套头是本机实测过的：站点把这种请求当同源 XHR，公开内容一律 200
        req.Headers.TryAddWithoutValidation("User-Agent", XmaUrls.UserAgent);
        req.Headers.TryAddWithoutValidation("Referer", XmaUrls.Root + "/");
        req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        req.Headers.TryAddWithoutValidation("Accept", "text/html,application/json,*/*");
        req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        // 装前看包要用 Range 只取几十 KB（实测站点支持，返回 206 + Content-Range）
        if (!string.IsNullOrEmpty(range))
            req.Headers.TryAddWithoutValidation("Range", range);
        return req;
    }

    /// <summary>Range 单次最多取多少字节 —— 防站点忽略 Range 时把整包吐回来。</summary>
    private const int MaxRangeBytes = 8 * 1024 * 1024;

    /// <summary>
    /// 装前看包：**不下载整包**，只用 1~3 次 Range 请求问出真实大小，
    /// 并对 zip 家族（.zip / .pmp / .ttmp2 —— 实测三者都是 zip 容器）列出包内清单。
    ///
    /// 设计原则：**绝不把失败变成假数据**。列不出来就如实写清原因，
    /// 界面对 .7z/.rar 照实显示「只能看大小」。
    /// </summary>
    public async Task<PackageInspection> InspectPackageAsync(string url, CancellationToken ct = default)
    {
        var fileName = FileNameOf(url);
        var ext = PackageIndex.ExtensionOf(url);

        try
        {
            var total = await ProbeSizeAsync(url, ct).ConfigureAwait(false);
            if (total <= 0)
                return PackageInspection.Failed(fileName, ext, "站点没给出文件大小（可能被挡了，稍后再试）");

            if (!PackageIndex.IsZipFamily(ext))
                return PackageInspection.SizeOnly(fileName, ext, total,
                    PackageIndex.IsKnownNonZipArchive(ext)
                        ? $"{ext} 不是 zip 容器，下载前列不出内容"
                        : "这种格式没验证过，下载前列不出内容");

            var tailStart = Math.Max(0, total - PackageIndex.TailProbeBytes);
            var tail = await GetRangeAsync(url, tailStart, total - 1, ct).ConfigureAwait(false);
            if (tail is null || !PackageIndex.TryFindEocd(tail, tailStart, out var cdOff, out var cdSize, out _))
                return PackageInspection.SizeOnly(fileName, ext, total, "这个包读不出 zip 目录，下载前列不出内容");

            byte[]? central = null;
            var rel = cdOff - tailStart;
            if (rel >= 0 && rel + cdSize <= tail.Length)
                central = tail.AsSpan((int)rel, (int)cdSize).ToArray();
            else if (cdSize > 0 && cdSize <= MaxRangeBytes)
                central = await GetRangeAsync(url, cdOff, cdOff + cdSize - 1, ct).ConfigureAwait(false);

            if (central is null || central.Length == 0)
                return PackageInspection.SizeOnly(fileName, ext, total, "找到了 zip 目录的位置但取不回来，下载前列不出内容");

            var entries = PackageIndex.ParseCentral(central);
            if (entries.Count == 0)
                return PackageInspection.SizeOnly(fileName, ext, total, "zip 目录里没有可读条目，下载前列不出内容");

            var summary = PackageIndex.Summarize(total, fileName, entries);
            _log.Info($"[XmaBrowser] 装前看包 {fileName}：{summary.SizeText}，" +
                      $"{summary.EntryCount} 个条目，内含 {summary.ModCount} 个 mod");
            return PackageInspection.Listed(summary);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            _log.Warn($"[XmaBrowser] 装前看包失败（不影响安装）：{e.Message}");
            return PackageInspection.Failed(fileName, ext, "探测失败：" + e.Message);
        }
    }

    /// <summary>
    /// 只为问大小：Range 0-0 拿 Content-Range 里的总长；站点不支持 Range 时退回 Content-Length。
    /// **非 200/206 一律返回 0** —— 否则会把 Cloudflare 挑战页的长度当成 mod 大小。
    /// </summary>
    private async Task<long> ProbeSizeAsync(string url, CancellationToken ct)
    {
        using var req = BuildRequest(url, "bytes=0-0");
        using var resp = await SendThrottledAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        var code = (int)resp.StatusCode;
        if (code is not (200 or 206)) return 0;

        if (code == 206 && resp.Content.Headers.ContentRange?.Length is { } len && len > 0) return len;
        return resp.Content.Headers.ContentLength is { } cl && cl > 0 ? cl : 0;
    }

    /// <summary>
    /// 取一段字节。**只接受 206**：站点若忽略 Range 直接回 200，就是打算把整包吐出来 ——
    /// 立刻放弃，绝不为了看清单而下载整包。
    /// </summary>
    private async Task<byte[]?> GetRangeAsync(string url, long from, long to, CancellationToken ct)
    {
        if (to < from) return null;
        var want = to - from + 1;
        if (want <= 0 || want > MaxRangeBytes) return null;

        using var req = BuildRequest(url, $"bytes={from}-{to}");
        using var resp = await SendThrottledAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if ((int)resp.StatusCode != 206)
        {
            _log.Warn($"[XmaBrowser] Range 未生效（HTTP {(int)resp.StatusCode}）→ 不列包内内容");
            return null;
        }

        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buf = new byte[want];
        var got = 0;
        while (got < want)
        {
            var n = await src.ReadAsync(buf.AsMemory(got, (int)(want - got)), ct).ConfigureAwait(false);
            if (n <= 0) break;
            got += n;
        }

        if (got == 0) return null;
        return got == want ? buf : buf[..got];
    }

    /// <summary>从 URL 里取文件名（给界面显示）。取不到就退回原串尾部。</summary>
    private static string FileNameOf(string url)
    {
        try
        {
            var path = new Uri(url).AbsolutePath;
            var slash = path.LastIndexOf('/');
            return Uri.UnescapeDataString(slash >= 0 ? path[(slash + 1)..] : path);
        }
        catch
        {
            var slash = url.LastIndexOf('/');
            return slash >= 0 ? url[(slash + 1)..] : url;
        }
    }

    private async Task<HttpResponseMessage> SendThrottledAsync(HttpRequestMessage req,
                                                               HttpCompletionOption completion, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_minIntervalMs > 0)
            {
                var wait = _minIntervalMs - (int)(DateTime.UtcNow - _lastRequestUtc).TotalMilliseconds;
                if (wait > 0) await Task.Delay(wait, ct).ConfigureAwait(false);
            }
            var response = await _http.SendAsync(req, completion, ct).ConfigureAwait(false);
            _lastRequestUtc = DateTime.UtcNow;
            return response;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>把失败响应的小体积错误体读出来（用于区分「人机校验」与「需要登录态」）。</summary>
    private static async Task<string> ReadErrorBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var take = Math.Min(bytes.Length, 16 * 1024);
            return System.Text.Encoding.UTF8.GetString(bytes, 0, take);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void ThrowIfBad(HttpResponseMessage response, string url, string? body = null)
    {
        var code = (int)response.StatusCode;
        if (code is >= 200 and < 300) return;

        var cfMitigated = response.Headers.TryGetValues("cf-mitigated", out var values) &&
                          values.Any(v => v.Contains("challenge", StringComparison.OrdinalIgnoreCase));

        var reason = XmaBlockClassifier.Classify(code, body, cfMitigated);
        throw new XmaAccessException(XmaBlockClassifier.Describe(reason), reason);
    }

    public void Dispose() => _gate.Dispose();
}
