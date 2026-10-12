using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Penumbra.Api.IpcSubscribers;

namespace XmaBrowser.Cover;

/// <summary>
/// 在 Penumbra 的 mod 面板里画封面。
///
/// 做法（与 ModBridge 同一套）：挂 Penumbra 的
/// <c>PreSettingsTabBarDraw(string directory, float width, float titleWidth)</c> IPC 事件
/// （Heliosphere 用的是同一个），在它设置页上方画一张图 —— 不依赖 Heliosphere、也不改 mod 目录名。
///
/// 只画 png/jpg/jpeg：Dalamud 的贴图加载不认 WebP。
/// </summary>
public sealed class PenumbraCoverDrawer : IDisposable
{
    private readonly Configuration _cfg;
    private readonly CoverContext _penumbra;
    private readonly ITextureProvider _textures;
    private readonly IPluginLog _log;

    private IDisposable? _subscription;

    private readonly Dictionary<string, ISharedImmediateTexture?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _order = [];
    private const int MaxCache = 48;

    public bool Active => _subscription is not null;

    public PenumbraCoverDrawer(IDalamudPluginInterface pi, Configuration cfg, CoverContext penumbra,
                               ITextureProvider textures, IPluginLog log)
    {
        _cfg = cfg;
        _penumbra = penumbra;
        _textures = textures;
        _log = log;

        try
        {
            _subscription = PreSettingsTabBarDraw.Subscriber(pi, OnDraw);
            _log.Information("[XmaBrowser] 已挂上 Penumbra 的封面绘制事件");
        }
        catch (Exception e)
        {
            _log.Warning($"[XmaBrowser] 挂 Penumbra 封面绘制事件失败（不会自己画封面）：{e.Message}");
        }
    }

    private void OnDraw(string directory, float width, float titleWidth)
    {
        try
        {
            if (!_cfg.DrawCovers || width <= 0 || string.IsNullOrWhiteSpace(directory)) return;

            var modRoot = _penumbra.ModDirectory;
            if (string.IsNullOrEmpty(modRoot)) return;

            var modDir = Path.Combine(modRoot, directory);
            if (!Directory.Exists(modDir)) return;

            // 目录名是 Heliosphere 规范、或里面已有真 WebP 封面 → 让它们自己出图，我们不抢
            if (CoverLocator.HasRealWebpCover(modDir)) return;

            var path = CoverLocator.FindDrawable(modDir);
            if (path is null) return;

            var wrap = Get(path);
            if (wrap is null || wrap.Height == 0) return;

            var maxHeight = width * Math.Clamp((float)_cfg.CoverSize, 0.1f, 1.0f);
            var aspect = (float)wrap.Width / wrap.Height;
            var drawWidth = width;
            var drawHeight = drawWidth / aspect;
            if (drawHeight > maxHeight)
            {
                drawHeight = maxHeight;
                drawWidth = drawHeight * aspect;
            }

            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0f, (width - drawWidth) / 2f));
            ImGui.Image(wrap.Handle, new Vector2(drawWidth, drawHeight));
            ImGui.Spacing();
        }
        catch (Exception e)
        {
            // 绘制回调里绝不能把异常抛回 Penumbra
            _log.Warning($"[XmaBrowser] 画封面出错（{directory}）：{e.Message}");
        }
    }

    /// <summary>按「路径 + 修改时间」缓存，文件换了自动重载。</summary>
    private IDalamudTextureWrap? Get(string path)
    {
        string key;
        try { key = path + "|" + File.GetLastWriteTimeUtc(path).Ticks; }
        catch { key = path; }

        if (_cache.TryGetValue(key, out var cached))
            return cached?.GetWrapOrDefault();

        ISharedImmediateTexture? tex = null;
        try { tex = _textures.GetFromFile(path); }
        catch (Exception e) { _log.Debug($"[XmaBrowser] 封面读取失败 {Path.GetFileName(path)}：{e.Message}"); }

        _cache[key] = tex;
        _order.AddLast(key);
        while (_order.Count > MaxCache)
        {
            var oldest = _order.First!.Value;
            _order.RemoveFirst();
            _cache.Remove(oldest);
        }
        return tex?.GetWrapOrDefault();
    }

    public void Dispose()
    {
        try { _subscription?.Dispose(); } catch { /* 忽略 */ }
        _subscription = null;
        _cache.Clear();
        _order.Clear();
    }
}
