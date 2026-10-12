using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using XmaBrowser.Browser;
using XmaBrowser.Site;

namespace XmaBrowser.Cover;

/// <summary>
/// 取「这条 Mod 的封面图」并存成本地 jpg。
///
/// 两条路（站点全尺寸预览图挂在 Cloudflare 后面，直连可能 403）：
///   1) 直连 HttpClient（公开缩略图基本都能拿到）
///   2) 直连被拒 → 浏览器通道：让浏览器打开图片，用 canvas 转成 **JPEG** 回传
///      （顺带解决「Dalamud 贴图不认 WebP」——通道回传的一律是 JPEG）
/// </summary>
public sealed class CoverFetcher
{
    private readonly XmaClient _site;
    private readonly BrowserChannel _channel;
    private readonly IXmaLog _log;
    private readonly string _dir;

    public CoverFetcher(XmaClient site, BrowserChannel channel, IXmaLog log, string dir)
    {
        _site = site;
        _channel = channel;
        _log = log;
        _dir = dir;
        Directory.CreateDirectory(_dir);
    }

    public async Task<string?> EnsureAsync(string? coverUrl, string modId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(coverUrl)) return null;

        Directory.CreateDirectory(_dir);
        var dest = Path.Combine(_dir, modId + ".jpg");

        try
        {
            if (File.Exists(dest) && new FileInfo(dest).Length > 1024)
                return dest;                          // 已有缓存
        }
        catch { /* 忽略 */ }

        try
        {
            var r = await _site.DownloadAsync(coverUrl, dest, null, ct).ConfigureAwait(false);
            if (r.Bytes > 1024) return dest;
        }
        catch (XmaAccessException e)
        {
            _log.Info($"封面直连被拒（{e.Message}），改走浏览器通道");
        }
        catch (Exception e)
        {
            _log.Debug($"封面直连失败：{e.Message}");
        }

        if (!_channel.Enabled) return null;

        try
        {
            var bytes = await _channel.GetImageBytesAsync(coverUrl, ct).ConfigureAwait(false);
            if (bytes is { Length: > 1024 })
            {
                await File.WriteAllBytesAsync(dest, bytes, ct).ConfigureAwait(false);
                _log.Info($"封面经浏览器通道取得（{bytes.Length} 字节，JPEG）");
                return dest;
            }
            _log.Warn("浏览器通道也没取到封面：" + _channel.LastError);
        }
        catch (Exception e)
        {
            _log.Warn($"浏览器通道取封面失败：{e.GetType().Name} {e.Message}");
        }

        return null;
    }
}
