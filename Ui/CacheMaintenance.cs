using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace XmaBrowser.Ui;

/// <summary>一个缓存目录的占用情况（给界面显示用）。</summary>
public sealed record CacheUsage(string Name, string Path, long Bytes, int Files, long LimitBytes)
{
    public string Describe()
    {
        if (!Directory.Exists(Path)) return $"{Name}：还没有";
        var cap = LimitBytes > 0 ? $"／上限 {Mb(LimitBytes)}" : string.Empty;
        return $"{Name}：{Mb(Bytes)}（{Files} 个文件）{cap}";
    }

    public static string Mb(long bytes) => bytes < 1024 * 1024
        ? $"{bytes / 1024.0:0.0} KB"
        : $"{bytes / 1048576.0:0.00} MB";
}

/// <summary>
/// 缓存维护（纯文件操作，可离线自测）。
///
/// 为什么要它：插件会往磁盘上写好几类东西，而**只有一部分原来有清理**：
///   · thumbs（预览图/缩略图）—— 只在「搜索完」顺手按大小淘汰，没搜索就不跑
///   · covers（封面图）—— **原来完全没有清理**，装一个 Mod 存一张，只增不减
///   · translate（翻译缓存）—— **原来完全没有清理**
///   · temp（下载的包 + 解压目录）—— 成功装完删；失败/取消时残留（已在别处修）
/// 这里提供：量占用、按"最久未使用"淘汰到上限、清掉过期文件。策略统一，界面也能显示。
///
/// 淘汰顺序按**最后写入时间**（LastWriteTimeUtc）从旧到新 ——
/// 缓存文件写入时间≈使用时间，所以这就是「最久没用过的先删」。
/// </summary>
public static class CacheMaintenance
{
    /// <summary>统计目录占用（递归）。目录不存在时返回 0。</summary>
    public static (long Bytes, int Files) Measure(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return (0, 0);
            long bytes = 0;
            var files = 0;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    bytes += new FileInfo(f).Length;
                    files++;
                }
                catch { /* 忽略单个文件 */ }
            }
            return (bytes, files);
        }
        catch { return (0, 0); }
    }

    /// <summary>
    /// 把目录按"最久未写入"淘汰到不超过 limitBytes。返回删掉的文件数。
    /// limitBytes &lt;= 0 表示不淘汰。
    /// </summary>
    public static int EvictToLimit(string dir, long limitBytes, out long remainingBytes)
    {
        remainingBytes = 0;
        var deleted = 0;
        try
        {
            if (!Directory.Exists(dir)) return 0;

            var entries = new List<(string Path, long Size, DateTime Written)>();
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var info = new FileInfo(f);
                    entries.Add((f, info.Length, info.LastWriteTimeUtc));
                }
                catch { /* 忽略 */ }
            }

            var total = entries.Sum(e => e.Size);
            if (limitBytes <= 0 || total <= limitBytes)
            {
                remainingBytes = total;
                RemoveEmptyDirs(dir);      // 即使不用淘汰，空目录残留也该收掉
                return 0;
            }

            foreach (var e in entries.OrderBy(e => e.Written))
            {
                if (total <= limitBytes) break;
                try
                {
                    File.Delete(e.Path);
                    total -= e.Size;
                    deleted++;
                }
                catch { /* 被占用就跳过 */ }
            }

            RemoveEmptyDirs(dir);
            remainingBytes = total;
        }
        catch { /* 忽略 */ }
        return deleted;
    }

    /// <summary>
    /// 收掉目录里的空子目录（解压残留常见）。
    /// 从最深的开始删，这样嵌套的空目录能被一次清干净。**不动传入的根目录本身。**
    /// </summary>
    public static int RemoveEmptyDirs(string dir)
    {
        var removed = 0;
        try
        {
            if (!Directory.Exists(dir)) return 0;
            foreach (var d in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories)
                                       .OrderByDescending(d => d.Length))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(d).Any()) { Directory.Delete(d); removed++; }
                }
                catch { /* 忽略 */ }
            }
        }
        catch { /* 忽略 */ }
        return removed;
    }

    /// <summary>删掉目录里「超过 age 没被写过」的文件，返回删掉的个数。</summary>
    public static int DeleteOlderThan(string dir, TimeSpan age, DateTime nowUtc)
    {
        var deleted = 0;
        try
        {
            if (!Directory.Exists(dir)) return 0;
            var limit = nowUtc - age;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(f) < limit) { File.Delete(f); deleted++; }
                }
                catch { /* 忽略 */ }
            }
        }
        catch { /* 忽略 */ }
        return deleted;
    }

    /// <summary>一次维护：各目录按上限淘汰 + 清掉临时目录里的陈年残留。</summary>
    public static string Maintain(IReadOnlyList<(string Name, string Path, long LimitBytes)> dirs,
                                  string tempDir, TimeSpan tempAge, DateTime nowUtc)
    {
        var notes = new List<string>();
        foreach (var (name, path, limit) in dirs)
        {
            if (limit <= 0) continue;
            var deleted = EvictToLimit(path, limit, out var left);
            if (deleted > 0)
                notes.Add($"{name}：按上限淘汰 {deleted} 个文件，剩 {CacheUsage.Mb(left)}");
        }

        var stale = DeleteOlderThan(tempDir, tempAge, nowUtc);
        if (stale > 0) notes.Add($"临时目录：清掉 {stale} 个超过 {tempAge.TotalHours:0} 小时的残留");

        // 临时目录里解压留下的空目录也一并收掉
        var emptyDirs = RemoveEmptyDirs(tempDir);
        if (emptyDirs > 0) notes.Add($"临时目录：收掉 {emptyDirs} 个空目录");

        return notes.Count > 0 ? string.Join("；", notes) : "缓存都在上限内，没动";
    }
}
