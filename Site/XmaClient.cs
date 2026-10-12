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
