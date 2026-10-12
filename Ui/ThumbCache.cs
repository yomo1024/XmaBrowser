using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

namespace XmaBrowser.Ui;

/// <summary>
/// 缩略图/预览图缓存：
///   · 磁盘按 URL 哈希存文件（上限可配，超了按最久未访问淘汰）
///   · 纹理缓存在内存（失败也记住，但**会过期**，不再永久判死）
///   · Get() 永不阻塞绘制线程：没下载好就先返回 null，后台下完下一帧自然出现
///
/// 两条取图路（allowChannel=true 时才启用第二条）：
///   1) 直连 HttpClient —— 公开缩略图基本都能拿到（快）
///   2) 直连被拒（403 / 返回网页而不是图片）→ 浏览器通道（带登录态，NSFW 的图只有这条路拿得到）
///
/// 为什么第二条要按调用点开关：NSFW 搜索结果一页有 15 张缩略图，
/// 如果全部回退到通道，就会把通道占满（每张都要一次导航），
/// 反过来把「搜索」和「读详情」堵住。所以只有**用户正在看的那张预览图**才允许走通道。
/// </summary>
public sealed class ThumbCache : IDisposable
{
    /// <summary>失败后多久才允许重试同一个 URL。
    /// 原来失败是永久记住的 —— NSFW 的图第一次直连 403 之后，就再也取不到了（详情面板一直空着）。</summary>
    public const int FailureCooldownSeconds = 600;

    private readonly HttpClient _http;
    private readonly ITextureProvider _textures;
    private readonly IPluginLog _log;
    private readonly int _maxBytes;
    private readonly string _dir;
    private readonly BrowserChannel? _channel;

    private readonly ConcurrentDictionary<string, byte> _downloading = new();
    private readonly ConcurrentDictionary<string, DateTime> _failedAt = new();
    private readonly ConcurrentDictionary<string, string> _status = new();
    private readonly Dictionary<string, ISharedImmediateTexture?> _texturesByUrl = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastUse = new();
    private readonly CancellationTokenSource _cts = new();

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
    /// allowChannel=true 表示这条图值得回退到浏览器通道（详情面板的预览图就是这样）。</summary>
    public ISharedImmediateTexture? Get(string url, bool allowChannel = false)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (_texturesByUrl.TryGetValue(url, out var cached)) return cached;

        if (_failedAt.TryGetValue(url, out var failedAt))
        {
            if ((DateTime.UtcNow - failedAt).TotalSeconds < FailureCooldownSeconds) return null;
            // 冷却过了：允许再试一次（站点状态会变，登录态也会变）
            _failedAt.TryRemove(url, out _);
        }

        var path = PathFor(url);
        if (!File.Exists(path))
        {
            StartDownload(url, path, allowChannel);
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
            Fail(url, "文件损坏，读不出图片");
            return null;
        }
    }

    /// <summary>给界面显示这张图现在什么状态（空 = 还没开始）。</summary>
    public string Status(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        if (_status.TryGetValue(url, out var s)) return s;
        return string.Empty;
    }

    private void Fail(string url, string why)
    {
        _failedAt[url] = DateTime.UtcNow;
        _status[url] = "失败：" + why;
    }

    private void StartDownload(string url, string path, bool allowChannel)
    {
        if (!_downloading.TryAdd(url, 1)) return;
        _status[url] = "下载中…";

        _ = Task.Run(async () =>
        {
            var tmp = path + ".part";
            try
            {
                // ① 直连
                var viaChannel = false;
                byte[]? direct = null;
                string directWhy = string.Empty;

                try
                {
                    using var r = await _http.GetAsync(url, HttpCompletionOption.ResponseContentRead, _cts.Token)
                                             .ConfigureAwait(false);
                    var media = r.Content.Headers.ContentType?.MediaType ?? string.Empty;
                    if (r.IsSuccessStatusCode && !media.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
                    {
                        direct = await r.Content.ReadAsByteArrayAsync(_cts.Token).ConfigureAwait(false);
                        if (direct.Length == 0) { direct = null; directWhy = "空响应"; }
                    }
                    else
                    {
                        directWhy = r.IsSuccessStatusCode
                            ? $"返回的是网页（{media}）——多半是 403/人机校验"
                            : $"HTTP {(int)r.StatusCode}";
                    }
                }
                catch (Exception e)
                {
                    directWhy = e.GetType().Name;
                }

                // ② 直连不行 → 浏览器通道（只在调用点允许时）
                if (direct is null && allowChannel && _channel is { Enabled: true })
                {
                    _status[url] = "走浏览器通道取图中…";
                    _log.Debug($"[XmaBrowser] 图片直连没成功（{directWhy}），改走浏览器通道");
                    var bytes = await _channel.GetImageBytesAsync(url, _cts.Token).ConfigureAwait(false);
                    if (bytes is { Length: > 0 })
                    {
                        direct = bytes;
                        viaChannel = true;
                    }
                    else
                    {
                        directWhy = directWhy.Length > 0 ? $"{directWhy}；通道：{_channel.LastError}" : _channel.LastError;
                    }
                }

                if (direct is null)
                {
                    Fail(url, directWhy.Length > 0 ? directWhy : "没取到");
                    return;
                }

                await File.WriteAllBytesAsync(tmp, direct, _cts.Token).ConfigureAwait(false);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                _lastUse[url] = DateTime.UtcNow;
                _status[url] = viaChannel ? "已缓存（浏览器通道）" : "已缓存";
                _log.Debug($"[XmaBrowser] 图片已缓存 {direct.Length} 字节（{(viaChannel ? "通道" : "直连")}）");
            }
            catch (Exception e)
            {
                Fail(url, e.GetType().Name);
                _log.Debug($"[XmaBrowser] 缩略图下载失败：{e.Message}");
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 忽略 */ }
                _downloading.TryRemove(url, out _);
            }
        });
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
