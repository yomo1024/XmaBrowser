using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace XmaBrowser.Site;

/// <summary>站点拒绝访问（403，或 /private/ 直链 404）：需要浏览器通道。</summary>
public sealed class XmaAccessException(string message) : Exception(message);

public sealed class XmaHttpException(int status, string url, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Url { get; } = url;
}

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
            _log.Info($"搜索「{query.Text}」第 {query.Page} 页 → 解析到 {result.Hits.Count} 条（站点提示共 {result.TotalMods} 个 Mod）");
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
        ThrowIfBad(response, url);

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

    private HttpRequestMessage BuildRequest(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        // 这套头是本机实测过的：站点把这种请求当同源 XHR，公开内容一律 200
        req.Headers.TryAddWithoutValidation("User-Agent", XmaUrls.UserAgent);
        req.Headers.TryAddWithoutValidation("Referer", XmaUrls.Root + "/");
        req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        req.Headers.TryAddWithoutValidation("Accept", "text/html,application/json,*/*");
        req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        return req;
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

    private static void ThrowIfBad(HttpResponseMessage response, string url, string? body = null)
    {
        var code = (int)response.StatusCode;
        if (code is >= 200 and < 300) return;

        if (code == 403)
        {
            // Cloudflare 人机校验页（本机实测：请求打得太密时整站会这样返回，连公开内容也一样）
            if (body is not null && body.Contains("Just a moment", StringComparison.OrdinalIgnoreCase))
                throw new XmaAccessException(
                    "站点正在做 Cloudflare 人机校验（Just a moment）—— 稍等一会儿再试，或启用浏览器通道交给浏览器取。");

            throw new XmaAccessException(
                "站点返回 403：这条内容需要站点登录态（通常是 NSFW）。启用浏览器通道后再试。");
        }

        if (code == 404 && url.Contains("/private/", StringComparison.OrdinalIgnoreCase))
            throw new XmaAccessException(
                "下载直链返回 404：这类文件要带登录态的浏览器去取（NSFW）。启用浏览器通道后再试。");

        throw new XmaHttpException(code, url, $"站点返回 HTTP {code}：{url}");
    }

    public void Dispose() => _gate.Dispose();
}
