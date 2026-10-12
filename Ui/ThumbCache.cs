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

namespace XmaBrowser.Ui;

/// <summary>
/// 缩略图/预览图缓存：
///   · 磁盘按 URL 哈希存文件（上限可配，超了按最久未访问淘汰）
///   · 纹理缓存在内存（失败也缓存，避免每帧重试）
///   · Get() 永不阻塞绘制线程：没下载好就先返回 null，后台下完下一帧自然出现
/// </summary>
public sealed class ThumbCache : IDisposable
{
    private readonly HttpClient _http;
    private readonly ITextureProvider _textures;
    private readonly IPluginLog _log;
    private readonly int _maxBytes;
    private readonly string _dir;

    private readonly ConcurrentDictionary<string, byte> _downloading = new();
    private readonly ConcurrentDictionary<string, byte> _failed = new();
    private readonly Dictionary<string, ISharedImmediateTexture?> _texturesByUrl = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastUse = new();
    private readonly CancellationTokenSource _cts = new();

    public ThumbCache(HttpClient http, ITextureProvider textures, IPluginLog log, string dir, int maxMegabytes)
    {
        _http = http;
        _textures = textures;
        _log = log;
        _dir = dir;
        _maxBytes = Math.Max(16, maxMegabytes) * 1024 * 1024;
        Directory.CreateDirectory(_dir);
    }

    /// <summary>绘制线程调用：拿纹理，拿不到就返回 null（并在后台开始下载）。</summary>
    public ISharedImmediateTexture? Get(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (_texturesByUrl.TryGetValue(url, out var cached)) return cached;
        if (_failed.ContainsKey(url)) return null;

        var path = PathFor(url);
        if (!File.Exists(path))
        {
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
            _failed[url] = 1;
            return null;
        }
    }

    private void StartDownload(string url, string path)
    {
        if (!_downloading.TryAdd(url, 1)) return;

        _ = Task.Run(async () =>
        {
            var tmp = path + ".part";
            try
            {
                using var r = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, _cts.Token)
                                         .ConfigureAwait(false);
                if (!r.IsSuccessStatusCode)
                {
                    _failed[url] = 1;
                    return;
                }
                await using (var src = await r.Content.ReadAsStreamAsync(_cts.Token).ConfigureAwait(false))
                await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 15, true))
                {
                    await src.CopyToAsync(dst, _cts.Token).ConfigureAwait(false);
                }
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                _lastUse[url] = DateTime.UtcNow;
            }
            catch (Exception e)
            {
                _failed[url] = 1;
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
