using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using XmaBrowser.Install.Archives;

namespace XmaBrowser.Install;

public enum PackageKind
{
    /// <summary>本身就是可以直接交给 Penumbra.InstallMod 的包（.pmp/.pcp/.ttmp/.ttmp2）。</summary>
    PenumbraPackage,
    /// <summary>压缩容器（zip / 7z / rar / tar…）：里面可能是多个包、也可能一个包都没有。</summary>
    Container,
    /// <summary>不认识的格式。</summary>
    Unsupported,
}

/// <summary>一个「要作为独立 Mod 登记」的目录（从容器里解出来的一份）。</summary>
public sealed class LooseMod
{
    public string SafeName { get; init; } = string.Empty;
    public string ExtractedDir { get; init; } = string.Empty;
    public int FileCount { get; init; }
}

/// <summary>
/// 容器的处理方案：要么交给 InstallMod 的包列表，要么是若干个要 AddMod 登记的目录。
/// Summary / Problem 是直接显示给用户的话。
/// </summary>
public sealed class InstallPlan
{
    public List<string> Packages { get; } = [];
    public List<LooseMod> LooseMods { get; } = [];
    public string Summary { get; set; } = string.Empty;
    public string? Problem { get; set; }
    public bool Ok => Packages.Count > 0 || LooseMods.Count > 0;
}

/// <summary>
/// 把「下载下来的一个文件」变成「Penumbra 能装的东西」。
///
/// 刻意不自己拼 .pmp（meta.toml 由 Penumbra 内部 API 生成，格式别猜），只用官方 IPC 两条路：
///   ① 包文件 → InstallMod；② 目录 → 放到 mod 根目录下再 AddMod。
///
/// **容器无关**：zip / 7z / rar / tar 都走同一套结构探测（见 <see cref="BuildPlan"/>），
/// 因为「里面有几个包、有没有包」跟容器格式无关。
/// </summary>
public static class PackageResolver
{
    public static readonly string[] PenumbraExtensions = [".pmp", ".pcp", ".ttmp", ".ttmp2"];

    /// <summary>Penumbra 用来标记「这是一个 Mod 目录」的文件名。</summary>
    private static readonly string[] ModMarkerFiles =
        ["meta.toml", "meta.json", "default_mod.json", "heliosphere.json"];

    /// <summary>看到这些扩展名就认为里面是 Mod 素材（用来判断「没包但整体是个 Mod」）。</summary>
    private static readonly HashSet<string> ModAssetExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tex", ".mdl", ".mtrl", ".dds", ".imc", ".avfx", ".tmb", ".pap", ".atch", ".eid",
        ".shpk", ".uld", ".sklb", ".pbd", ".atex", ".atexb", ".scd", ".eqp", ".eqdp", ".gmp",
    };

    public static PackageKind KindOf(string file)
    {
        var ext = Path.GetExtension(file ?? string.Empty).ToLowerInvariant();
        if (PenumbraExtensions.Contains(ext)) return PackageKind.PenumbraPackage;
        if (ArchiveReaders.IsSupportedContainer(file ?? string.Empty)) return PackageKind.Container;
        return PackageKind.Unsupported;
    }

    public static bool IsPenumbraPackage(string nameOrPath)
        => PenumbraExtensions.Contains(Path.GetExtension(nameOrPath).ToLowerInvariant());

    /// <summary>
    /// 结构探测：看容器里到底是什么，给出确定的处理方案。
    ///
    /// 规则（按优先级）：
    ///   1. 本身就是包 → 直接 InstallMod
    ///   2. 容器里有 .pmp/.ttmp2 … → 全部解出来逐个 InstallMod（**多个包就装多个**）
    ///   3. 没有包，但有「像 Mod 的顶层子目录」（各自含 meta.*/default_mod.json，或 files/，或素材文件）
    ///      → 每个子目录单独作为一条 Mod（**不混成一坨**）
    ///   4. 没有包，根目录本身就像 Mod → 整体作为一条 Mod
    ///   5. 都不像 → Problem：把顶层内容列出来告诉你为什么装不了
    /// </summary>
    public static InstallPlan BuildPlan(string containerPath, string workDir)
    {
        var plan = new InstallPlan();

        switch (KindOf(containerPath))
        {
            case PackageKind.PenumbraPackage:
                plan.Packages.Add(containerPath);
                plan.Summary = "本身就是 Mod 包，直接安装";
                return plan;

            case PackageKind.Unsupported:
                plan.Problem = $"不认识的格式（{Path.GetExtension(containerPath)}）—— 目前支持 .pmp/.pcp/.ttmp/.ttmp2 与 zip/7z/rar/tar";
                return plan;
        }

        IArchiveReader reader;
        try
        {
            reader = ArchiveReaders.Open(containerPath);
        }
        catch (Exception e)
        {
            plan.Problem = $"打不开这个压缩包：{e.GetType().Name} {e.Message}";
            return plan;
        }

        using (reader)
        {
            var files = reader.Entries.Where(e => !e.IsDirectory && !IsJunk(e.Key)).ToList();
            var dirs = reader.Entries.Where(e => e.IsDirectory && !IsJunk(e.Key)).Select(e => e.Key).ToList();

            if (files.Count == 0)
            {
                plan.Problem = "压缩包是空的（没有任何文件）";
                return plan;
            }

            // ---- 2) 内层包：可能有多个，也可能藏在子目录里 ----
            var packageEntries = files.Where(e => IsPenumbraPackage(e.Key)).ToList();
            if (packageEntries.Count > 0)
            {
                var dir = Path.Combine(workDir, "packages");
                Directory.CreateDirectory(dir);
                foreach (var e in packageEntries)
                {
                    var name = SafeModDirName(Path.GetFileName(e.Key), "inner.pmp");
                    if (!IsPenumbraPackage(name)) name += Path.GetExtension(e.Key);
                    var dest = Unique(Path.Combine(dir, name));
                    using var src = reader.OpenEntry(e);
                    using var dst = File.Create(dest);
                    src.CopyTo(dst);
                    plan.Packages.Add(dest);
                }

                plan.Summary = packageEntries.Count == 1
                    ? "包内发现 1 个 Mod 包"
                    : $"包内发现 {packageEntries.Count} 个 Mod 包（逐个安装）";
                return plan;
            }

            // ---- 3) 没有包：先看「整包本身就是一个 Mod」（根上有标记文件或 files/ 目录）----
            //     必须放在子目录判断之前：否则 `default_mod.json + files/x.tex` 这种标准 Mod 结构
            //     会被误认成「files 是个 Mod 目录」，既丢掉根上的 meta，又平白剥掉一层目录（实测踩过）。
            if (LooksLikeModItself(files.Select(e => e.Key)))
            {
                var target = UniqueDir(Path.Combine(workDir, "loose", SafeModDirName(
                    Path.GetFileNameWithoutExtension(containerPath), "mod")));
                ExtractUnder(reader, string.Empty, target);
                plan.LooseMods.Add(new LooseMod
                {
                    SafeName = Path.GetFileName(target),
                    ExtractedDir = target,
                    FileCount = files.Count,
                });
                plan.Summary = $"没有内层包：整个压缩包作为 1 个 Mod 登记（{files.Count} 个文件）";
                return plan;
            }

            // ---- 4) 再看「若干个顶层子目录，各自是一个 Mod」----
            var topDirs = files.Select(e => e.Key.IndexOf('/') is var i && i > 0 ? e.Key[..i] : string.Empty)
                               .Where(s => s.Length > 0)
                               .Distinct(StringComparer.OrdinalIgnoreCase)
                               .ToList();

            var candidates = new List<(string Prefix, int Count)>();
            foreach (var d in topDirs)
            {
                if (IsInternalDirName(d)) continue;          // files / mod / textures 这类是 Mod 内部结构，不是 Mod
                var prefix = d + "/";
                var under = files.Where(e => e.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
                if (under.Count == 0) continue;
                if (LooksLikeModFolder(under.Select(e => e.Key[prefix.Length..])))
                    candidates.Add((prefix, under.Count));
            }

            if (candidates.Count > 0)
            {
                var looseRoot = Path.Combine(workDir, "loose");
                Directory.CreateDirectory(looseRoot);

                foreach (var (prefix, count) in candidates)
                {
                    var safe = SafeModDirName(prefix.TrimEnd('/'), "mod");
                    var target = UniqueDir(Path.Combine(looseRoot, safe));
                    ExtractUnder(reader, prefix, target);
                    plan.LooseMods.Add(new LooseMod
                    {
                        SafeName = Path.GetFileName(target),
                        ExtractedDir = target,
                        FileCount = count,
                    });
                }

                plan.Summary = candidates.Count == 1
                    ? $"没有内层包：按 1 个 Mod 目录处理（{plan.LooseMods[0].SafeName}，{plan.LooseMods[0].FileCount} 个文件）"
                    : $"没有内层包：找到 {candidates.Count} 个 Mod 目录，逐个登记";
                return plan;
            }

            // ---- 5) 都不像 ----
            var top = files.Select(e => e.Key).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
            var more = files.Count > top.Count ? $" 等共 {files.Count} 个文件" : "";
            plan.Problem = "这个压缩包里既没有 Mod 包（.pmp/.ttmp2），也没有 Mod 应有的结构（没有 meta.json/default_mod.json，" +
                           "也没有 .tex/.mdl 之类的素材）。顶层内容：" + string.Join("、", top) + more +
                           "。可能是说明书/素材包，不是可安装的 Mod。";
            return plan;
        }
    }

    /// <summary>Mod 目录内部常见的子目录名 —— 这些不能当成「一个 Mod」。</summary>
    private static readonly HashSet<string> InternalDirNames = new(StringComparer.OrdinalIgnoreCase)
        { "files", "mod", "textures", "images", "chara", "vfx", "ui", "sound", "common" };

    private static bool IsInternalDirName(string name) => InternalDirNames.Contains(name.Trim());

    /// <summary>
    /// 「整包自己就是一个 Mod」的判定：**只看根这一层**。
    /// 宽松会出事：`ModA/files/a.tex` 里有 .tex，如果把「任意深度出现素材」当成"整包是 Mod"，
    /// 那么「两个子目录各是一个 Mod」的包就会被当成一个（实测踩过）。
    /// </summary>
    private static bool LooksLikeModItself(IEnumerable<string> relativePaths)
    {
        foreach (var raw in relativePaths)
        {
            var p = raw.Replace('\\', '/').TrimStart('/');
            if (p.Length == 0) continue;

            var isTopLevel = p.IndexOf('/') < 0;
            if (isTopLevel)
            {
                if (ModMarkerFiles.Contains(p, StringComparer.OrdinalIgnoreCase)) return true;   // meta.* / default_mod.json
                if (ModAssetExts.Contains(Path.GetExtension(p))) return true;                    // 根上直接放着素材
            }
            else if (p.StartsWith("files/", StringComparison.OrdinalIgnoreCase) ||
                     p.StartsWith("mod/", StringComparison.OrdinalIgnoreCase))
            {
                return true;                                                                    // 根上的 files/ 或 mod/
            }
        }
        return false;
    }

    /// <summary>「这个子目录自己是一个 Mod」的判定（深度不限：子目录内部有素材就算）。</summary>
    private static bool LooksLikeModFolder(IEnumerable<string> relativePaths)
    {
        var list = relativePaths.Select(p => p.Replace('\\', '/').TrimStart('/')).ToList();
        if (list.Count == 0) return false;

        foreach (var p in list)
        {
            if (p.IndexOf('/') < 0 && ModMarkerFiles.Contains(p, StringComparer.OrdinalIgnoreCase)) return true;
            if (p.StartsWith("files/", StringComparison.OrdinalIgnoreCase) ||
                p.StartsWith("mod/", StringComparison.OrdinalIgnoreCase)) return true;
            if (ModAssetExts.Contains(Path.GetExtension(p))) return true;
        }
        return false;
    }

    /// <summary>把容器里某个前缀下的内容解到 targetDir（去掉前缀）。带 Zip Slip 防护。</summary>
    private static void ExtractUnder(IArchiveReader reader, string prefix, string targetDir)
    {
        var root = Path.GetFullPath(targetDir);
        Directory.CreateDirectory(root);

        foreach (var e in reader.Entries)
        {
            if (e.IsDirectory || IsJunk(e.Key)) continue;
            if (prefix.Length > 0 && !e.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            var rel = prefix.Length > 0 ? e.Key[prefix.Length..] : e.Key;
            if (rel.Length == 0) continue;

            var dest = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!dest.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                continue;                                  // 想跳出去？丢掉

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using var src = reader.OpenEntry(e);
            using var dst = File.Create(dest);
            src.CopyTo(dst);
        }
    }

    /// <summary>mod 目录名：去掉非法字符，别太长（Penumbra 要拿它当目录名）。</summary>
    public static string SafeModDirName(string name, string fallback)
    {
        var bad = Path.GetInvalidFileNameChars();
        var cleaned = new string((name ?? string.Empty).Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
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

    private static string UniqueDir(string path)
    {
        if (!Directory.Exists(path)) return path;
        for (var i = 2; i < 1000; i++)
        {
            var p = path + "_" + i;
            if (!Directory.Exists(p)) return p;
        }
        return path;
    }
}
