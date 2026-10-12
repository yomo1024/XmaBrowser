using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace XmaBrowser.Cover;

/// <summary>
/// 在 Mod 目录里找一张「游戏内画得出来」的封面。
///
/// 关键限制（从 ModBridge 的实证结论继承）：**Dalamud 的贴图加载不认 WebP**，
/// 所以自己画封面时只能认 png/jpg/jpeg；webp 一律跳过（那是给认 cover.webp 的汉化版 Penumbra 看的）。
/// </summary>
public static class CoverLocator
{
    public static readonly string[] DrawableExts = [".jpg", ".jpeg", ".png"];
    public const string CoverWebp = "cover.webp";
    public const string MetaDir = "images";
    public const string MetaBase = "_MetaImage";

    /// <summary>按优先级找：cover.jpg/png → images/_MetaImage.* → meta.json 的 Image。</summary>
    public static string? FindDrawable(string modFolder)
    {
        if (string.IsNullOrWhiteSpace(modFolder) || !Directory.Exists(modFolder))
            return null;

        foreach (var ext in DrawableExts)
        {
            var p = Path.Combine(modFolder, "cover" + ext);
            if (File.Exists(p)) return p;
        }

        var imgDir = Path.Combine(modFolder, MetaDir);
        if (Directory.Exists(imgDir))
        {
            foreach (var ext in DrawableExts)
            {
                var p = Path.Combine(imgDir, MetaBase + ext);
                if (File.Exists(p)) return p;
            }
        }

        // meta.json 的 Image（官方版读它；可能是 webp，那就跳过）
        try
        {
            var meta = Path.Combine(modFolder, "meta.json");
            if (!File.Exists(meta)) return null;
            if (JsonNode.Parse(File.ReadAllText(meta)) is not JsonObject obj) return null;
            var rel = obj["Image"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(rel)) return null;
            if (rel.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)) return null;

            var abs = Path.Combine(modFolder, rel.Replace('\\', Path.DirectorySeparatorChar)
                                                   .Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(abs)) return abs;
        }
        catch { /* 解析不了就当没有 */ }

        return null;
    }

    /// <summary>目录里是否已有作者/Heliosphere 给的**真 WebP** 封面（有就别覆盖它）。</summary>
    public static bool HasRealWebpCover(string modFolder)
        => IsRealWebp(Path.Combine(modFolder, CoverWebp));

    /// <summary>RIFF....WEBP —— 用例：分辨真封面和「jpg 改名成 cover.webp」的假货。</summary>
    public static bool IsRealWebp(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
            using var fs = File.OpenRead(path);
            var head = new byte[12];
            if (fs.Read(head, 0, 12) < 12) return false;
            return head[0] == (byte)'R' && head[1] == (byte)'I' && head[2] == (byte)'F' && head[3] == (byte)'F'
                && head[8] == (byte)'W' && head[9] == (byte)'E' && head[10] == (byte)'B' && head[11] == (byte)'P';
        }
        catch { return false; }
    }
}
