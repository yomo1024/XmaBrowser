using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace XmaBrowser.Install;

/// <summary>
/// 「装进哪个文件夹」的路径计算。
///
/// 背景（源码级证据）：Penumbra 的 mod 列表本身就是**文件系统树**，
/// 文件夹是可折叠的组（`ModFileSystemDrawer` 里画 CollapsedFolderColor/FolderLineColor），
/// 而 `SetModPath(dir, name, newPath)` 内部是 `_modFileSystem.RenameAndMove(node, newPath)`
/// —— 所以给 mod 一个带文件夹的路径，它在列表里就会归进那个折叠组。
///
/// 这里只做**纯字符串处理**（不碰磁盘、不碰 Dalamud），所以能在无游戏环境里自测。
/// 模板里可用的占位符：{prefix} {type} {author} {modid} {name}
/// </summary>
public static class GroupPath
{
    public const string PrefixToken = "{prefix}";
    public const string TypeToken = "{type}";
    public const string AuthorToken = "{author}";
    public const string ModIdToken = "{modid}";
    public const string NameToken = "{name}";

    private const int MaxSegmentLength = 48;
    private const int MaxDepth = 4;

    /// <summary>
    /// 算出「这个 mod 应该放进哪个文件夹」（相对路径，**不含 mod 自己的目录名**）。
    /// 返回空串表示不分组（放在根下）。
    /// </summary>
    public static string BuildFolder(string template, string prefix, string siteType, string author,
                                     string modId, string modName)
    {
        if (string.IsNullOrWhiteSpace(template)) return string.Empty;

        var text = template
            .Replace(PrefixToken, prefix ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(TypeToken, string.IsNullOrWhiteSpace(siteType) ? "Other" : siteType, StringComparison.OrdinalIgnoreCase)
            .Replace(AuthorToken, author ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(ModIdToken, modId ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(NameToken, modName ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var segments = new List<string>();
        foreach (var raw in text.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            var seg = SanitizeSegment(raw);
            if (seg.Length == 0) continue;          // 空段（例如 prefix 留空）直接跳过
            if (segments.Contains(seg, StringComparer.OrdinalIgnoreCase)) continue;
            segments.Add(seg);
            if (segments.Count >= MaxDepth) break;
        }

        return segments.Count == 0 ? string.Empty : string.Join("/", segments);
    }

    /// <summary>拼出交给 SetModPath 的完整新路径（文件夹 + mod 自己的目录名）。</summary>
    public static string BuildFullPath(string folder, string currentDirectoryName)
    {
        var name = SanitizeSegment(currentDirectoryName);
        if (name.Length == 0) return string.Empty;
        return string.IsNullOrEmpty(folder) ? name : folder + "/" + name;
    }

    /// <summary>单段清洗：去掉非法字符与目录穿越，限长。</summary>
    public static string SanitizeSegment(string segment)
    {
        var s = (segment ?? string.Empty).Trim().Trim('.');
        if (s.Length == 0) return string.Empty;

        var bad = new HashSet<char>(new[] { '<', '>', ':', '"', '|', '?', '*', '/', '\\', '\t', '\r', '\n' });
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (bad.Contains(c) || char.IsControl(c)) sb.Append('_');
            else sb.Append(c);
        }

        var cleaned = sb.ToString().Trim().TrimEnd('.', ' ');

        // 目录穿越：整段是 . 或 .. 一律丢掉
        if (cleaned.Length == 0 || cleaned == "." || cleaned == "..") return string.Empty;

        return cleaned.Length > MaxSegmentLength ? cleaned[..MaxSegmentLength].TrimEnd('.', ' ') : cleaned;
    }

    /// <summary>安全检查：这段相对路径不会跑到 mod 根目录外面去。</summary>
    public static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.Length > 200) return false;

        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains(':')) return false;

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Length > MaxDepth + 1) return false;

        foreach (var p in parts)
            if (p is "." or ".." || p.IndexOfAny(['/', '\\']) >= 0) return false;

        return true;
    }

    /// <summary>给设置页显示的一句话预览。</summary>
    public static string Describe(string template, string prefix)
        => BuildFolder(template, prefix, "Gear", "SomeAuthor", "123456", "My Mod") is var f && f.Length > 0
            ? $"{f}/My Mod"
            : "（不分组，装在根目录下）";

    /// <summary>
    /// `SetModPath` 的 newPath 到底该给哪种形态？
    ///
    /// 我们读不到 Penumbra 内部 `RenameAndMove` 的实现（`BaseFileSystem` 在外部依赖里），
    /// 而它可能把 newPath 当成「含自身名的完整路径」，也可能只当「父文件夹」。所以不赌：
    /// 给出几种候选，调用后用 `GetModPath` **读回真实路径**核对，挑对的那个。
    /// </summary>
    public static IReadOnlyList<string> CandidatePaths(string folder, string currentNodeName, string displayName)
    {
        var list = new List<string>();

        void Add(string s)
        {
            if (s.Length > 0 && IsSafeRelativePath(s) && !list.Contains(s, StringComparer.OrdinalIgnoreCase))
                list.Add(s);
        }

        // 1) 完整路径：文件夹 + 当前节点名（我们的主假设）
        Add(BuildFullPath(folder, currentNodeName));
        // 2) 只给文件夹（若它自己保留节点名）
        if (!string.IsNullOrEmpty(folder)) Add(folder);
        // 3) 文件夹 + 显示名（若它用显示名当节点名）
        var byDisplay = SanitizeSegment(displayName);
        if (byDisplay.Length > 0) Add(string.IsNullOrEmpty(folder) ? byDisplay : folder + "/" + byDisplay);

        return list;
    }

    /// <summary>
    /// 判断读回来的「真实路径」是否落在目标文件夹下。
    /// 兼容相对路径与绝对路径、正反斜杠；要求目标文件夹是一个完整路径段（避免 XMA2 误判成 XMA）。
    /// </summary>
    public static bool PathUnderFolder(string? fullPath, string folder)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || string.IsNullOrWhiteSpace(folder)) return false;

        var p = "/" + fullPath.Replace('\\', '/').Trim('/') + "/";
        var f = "/" + folder.Replace('\\', '/').Trim('/') + "/";
        return p.Contains(f, StringComparison.OrdinalIgnoreCase);
    }
}
