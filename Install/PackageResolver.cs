using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace XmaBrowser.Install;

public enum PackageKind
{
    /// <summary>可以直接交给 Penumbra.InstallMod 的包（.pmp/.pcp/.ttmp/.ttmp2）。</summary>
    PenumbraPackage,
    /// <summary>压缩包：里面可能含多个包，也可能是松散文件。</summary>
    Zip,
    /// <summary>7z/rar 等暂不支持（刻意不引带漏洞告警的第三方解压库）。</summary>
    Unsupported,
}

public sealed class ResolveResult
{
    /// <summary>交给 Penumbra.InstallMod 的包（zip 里可能有好几个）。</summary>
    public List<string> Packages { get; } = [];
    /// <summary>松散文件解出来的目录（走 Penumbra.AddMod 登记）。</summary>
    public string LooseDirectory { get; set; } = string.Empty;
    /// <summary>人话说明，直接显示给用户。</summary>
    public string Note { get; set; } = string.Empty;
    public bool Ok => Packages.Count > 0 || LooseDirectory.Length > 0;
}

/// <summary>
/// 把「下载下来的一个文件」变成「Penumbra 能装的东西」。
/// 不自己拼 .pmp（Penumbra 的 meta.toml 由它自己的内部 API 生成，我们不猜格式），只用官方 IPC 的两条路：
///   ① 包文件 → InstallMod；② 松散文件夹 → 放到 mod 根目录下再 AddMod。
/// </summary>
public static class PackageResolver
{
    public static readonly string[] PenumbraExtensions = [".pmp", ".pcp", ".ttmp", ".ttmp2"];

    public static PackageKind KindOf(string file)
    {
        var ext = Path.GetExtension(file ?? "").ToLowerInvariant();
        if (PenumbraExtensions.Contains(ext)) return PackageKind.PenumbraPackage;
        if (ext is ".zip") return PackageKind.Zip;
        return PackageKind.Unsupported;
    }

    public static bool IsPenumbraPackage(string nameOrPath)
        => PenumbraExtensions.Contains(Path.GetExtension(nameOrPath).ToLowerInvariant());

    /// <summary>列出 zip 里的包文件（.pmp/.ttmp2…）的完整解出路径；没有就返回空表。</summary>
    public static List<string> ExtractInnerPackages(string zipPath, string workDir, out string error)
    {
        var found = new List<string>();
        error = string.Empty;
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var entries = zip.Entries
                .Where(e => e.Length > 0 && IsPenumbraPackage(e.FullName) && !IsJunk(e.FullName))
                .ToList();

            Directory.CreateDirectory(workDir);
            for (var i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                var safeName = Sanitize(Path.GetFileName(e.FullName));
                if (safeName.Length == 0) safeName = $"inner{i}.pmp";
                var dest = Unique(Path.Combine(workDir, safeName));
                e.ExtractToFile(dest, true);
                found.Add(dest);
            }
        }
        catch (Exception ex)
        {
            error = $"解压 {Path.GetFileName(zipPath)} 失败：{ex.GetType().Name} {ex.Message}";
        }
        return found;
    }

    /// <summary>把 zip 里除包文件以外的内容解到 targetDir（走 AddMod 的路径）。带 Zip Slip 防护。</summary>
    public static bool ExtractLoose(string zipPath, string targetDir, out string error)
    {
        error = string.Empty;
        try
        {
            var root = Path.GetFullPath(targetDir);
            Directory.CreateDirectory(root);

            using var zip = ZipFile.OpenRead(zipPath);
            foreach (var e in zip.Entries)
            {
                if (IsJunk(e.FullName)) continue;
                if (e.FullName.EndsWith("/", StringComparison.Ordinal)) continue;
                if (IsPenumbraPackage(e.FullName)) continue;   // 內层包走 InstallMod，不混进来

                var dest = Path.GetFullPath(Path.Combine(root, e.FullName.Replace('/', Path.DirectorySeparatorChar)));
                if (!dest.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(dest, root, StringComparison.OrdinalIgnoreCase))
                {
                    error = $"压缩包里有可疑路径，已跳过：{e.FullName}";
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                e.ExtractToFile(dest, true);
            }
            return true;
        }
        catch (Exception ex)
        {
            error = $"解压失败：{ex.GetType().Name} {ex.Message}";
            return false;
        }
    }

    /// <summary>mod 目录名：去掉非法字符，别太长（Penumbra 要拿它当目录名）。</summary>
    public static string SafeModDirName(string name, string fallback)
    {
        var bad = Path.GetInvalidFileNameChars();
        var cleaned = new string((name ?? "").Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
        cleaned = cleaned.Replace('\\', '_').Replace('/', '_');
        if (cleaned.Length == 0) cleaned = fallback;
        return cleaned.Length > 80 ? cleaned[..80] : cleaned;
    }

    private static bool IsJunk(string entryPath)
    {
        var p = entryPath.Replace('\\', '/').TrimStart('/');
        return p.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase) ||
               p.EndsWith("/.DS_Store", StringComparison.OrdinalIgnoreCase) ||
               p.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase) ||
               p.EndsWith("Thumbs.db", StringComparison.OrdinalIgnoreCase);
    }

    private static string Sanitize(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        return new string((name ?? "").Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
    }

    private static string Unique(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; i < 1000; i++)
        {
            var p = Path.Combine(dir, $"{stem}_{i}{ext}");
            if (!File.Exists(p)) return p;
        }
        return path;
    }
}
