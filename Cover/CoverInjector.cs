using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XmaBrowser.Cover;

/// <summary>
/// 把封面**塞进包里**再交给 Penumbra —— 这是让图真正落进 mod 目录的可靠做法。
///
/// 为什么（ModBridge 的实证结论，别退回旧做法）：Penumbra 的 InstallMod 只接受包文件、
/// 由它自己解包成目录；包内没有图 → 解出来就没有图。而「装完再往目录里写」会因时机问题失败。
///
/// 写三处，谁认哪份都能出图：
///   1) &lt;根&gt;\cover.&lt;ext&gt;              （有人按固定名 + 扩展名找）
///   2) &lt;根&gt;\images\_MetaImage.&lt;ext&gt;   （官方版那一套；插件自己画封面读的也是它）
///   3) meta.json 的 Image 指向 2)
/// 注意：**不伪造 cover.webp**（内容不是 WebP 会让认它的汉化版 Penumbra 解不出图，还会把后续修复死锁）。
/// </summary>
public static class CoverInjector
{
    private const string TempDirName = "XmaBrowserCoverPacks";

    /// <summary>返回注入了封面的新包路径；没封面/不是 zip/失败 → null（调用方继续用原包）。</summary>
    public static string? InjectIntoPackage(string packagePath, string? coverPath, string? workRoot = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath)) return null;
            if (string.IsNullOrWhiteSpace(coverPath) || !File.Exists(coverPath)) return null;

            var ext = Path.GetExtension(coverPath).ToLowerInvariant();
            if (!CoverLocator.DrawableExts.Contains(ext)) return null;   // 只注入能解码的格式

            var dir = Path.Combine(workRoot ?? Path.Combine(Path.GetTempPath(), TempDirName), "inject");
            Directory.CreateDirectory(dir);

            var pkgExt = Path.GetExtension(packagePath);
            if (string.IsNullOrWhiteSpace(pkgExt)) pkgExt = ".pmp";
            var dest = Path.Combine(dir, Path.GetFileNameWithoutExtension(packagePath) + "-cover" + pkgExt);
            try { if (File.Exists(dest)) File.Delete(dest); } catch { /* 忽略 */ }

            string? metaJson = null;
            using (var src = ZipFile.OpenRead(packagePath))
            using (var outStream = File.Create(dest))
            using (var dst = new ZipArchive(outStream, ZipArchiveMode.Create))
            {
                foreach (var e in src.Entries)
                {
                    var name = e.FullName.Replace('\\', '/');
                    var atRoot = name.IndexOf('/') < 0;

                    // 根目录的 cover.*：由我们统一写一份
                    if (atRoot && e.Name.StartsWith("cover.", StringComparison.OrdinalIgnoreCase))
                        continue;
                    // images/_MetaImage.*：同上
                    if (!atRoot && name.StartsWith("images/", StringComparison.OrdinalIgnoreCase)
                        && e.Name.StartsWith("_MetaImage.", StringComparison.OrdinalIgnoreCase))
                        continue;
                    // meta.json 最后自己写回去（要改 Image 字段）
                    if (atRoot && e.Name.Equals("meta.json", StringComparison.OrdinalIgnoreCase))
                    {
                        using var s = e.Open();
                        using var reader = new StreamReader(s, Encoding.UTF8);
                        metaJson = reader.ReadToEnd();
                        continue;
                    }

                    var copy = dst.CreateEntry(name, CompressionLevel.Fastest);
                    using var from = e.Open();
                    using var to = copy.Open();
                    from.CopyTo(to);
                }

                var bytes = File.ReadAllBytes(coverPath);
                AddEntry(dst, "cover" + ext, bytes);
                AddEntry(dst, "images/_MetaImage" + ext, bytes);

                if (metaJson is not null)
                {
                    var text = metaJson;
                    try
                    {
                        if (JsonNode.Parse(metaJson) is JsonObject obj)
                        {
                            obj["Image"] = "images\\_MetaImage" + ext;
                            text = obj.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
                        }
                    }
                    catch { /* 解析不了就原样写回 */ }

                    var entry = dst.CreateEntry("meta.json", CompressionLevel.Fastest);
                    using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                    w.Write(text);
                }
            }

            return File.Exists(dest) ? dest : null;
        }
        catch
        {
            return null;   // 注入失败不影响安装
        }
    }

    /// <summary>装完之后往 mod 目录补写封面（包注入失败时的兜底）。已有真 WebP 封面就不动。</summary>
    public static bool Apply(string modFolder, string? coverPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(modFolder) || !Directory.Exists(modFolder)) return false;
            if (CoverLocator.HasRealWebpCover(modFolder)) return false;
            if (string.IsNullOrWhiteSpace(coverPath) || !File.Exists(coverPath)) return false;

            var ext = Path.GetExtension(coverPath).ToLowerInvariant();
            if (!CoverLocator.DrawableExts.Contains(ext)) return false;

            File.Copy(coverPath, Path.Combine(modFolder, "cover" + ext), true);

            var imgDir = Path.Combine(modFolder, CoverLocator.MetaDir);
            Directory.CreateDirectory(imgDir);
            foreach (var other in CoverLocator.DrawableExts.Where(x => x != ext))
            {
                var stale = Path.Combine(imgDir, CoverLocator.MetaBase + other);
                try { if (File.Exists(stale)) File.Delete(stale); } catch { /* 忽略 */ }
            }
            File.Copy(coverPath, Path.Combine(imgDir, CoverLocator.MetaBase + ext), true);

            var meta = Path.Combine(modFolder, "meta.json");
            if (File.Exists(meta))
            {
                try
                {
                    if (JsonNode.Parse(File.ReadAllText(meta)) is JsonObject obj)
                    {
                        obj["Image"] = "images\\_MetaImage" + ext;
                        File.WriteAllText(meta, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = false }),
                                          new UTF8Encoding(false));
                    }
                }
                catch { /* 忽略：图已经写好了 */ }
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>清掉注入留下的临时包（插件启动时调一次）。</summary>
    public static void CleanupOld(int olderThanHours = 24)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), TempDirName);
            if (!Directory.Exists(dir)) return;
            var limit = DateTime.UtcNow.AddHours(-olderThanHours);
            foreach (var f in Directory.GetFiles(dir, "*-cover.*"))
            {
                try { if (File.GetLastWriteTimeUtc(f) < limit) File.Delete(f); } catch { /* 忽略 */ }
            }
        }
        catch { /* 忽略 */ }
    }

    private static void AddEntry(ZipArchive dst, string name, byte[] data)
    {
        var e = dst.CreateEntry(name, CompressionLevel.Fastest);
        using var s = e.Open();
        s.Write(data, 0, data.Length);
    }
}
