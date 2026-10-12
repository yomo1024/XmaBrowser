using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using SharpCompress.Archives;

namespace XmaBrowser.Install.Archives;

/// <summary>容器里的一个条目（路径统一用 '/' 分隔）。</summary>
public sealed class ArchiveEntry
{
    public string Key { get; init; } = string.Empty;
    public long Size { get; init; }
    public bool IsDirectory { get; init; }
}

/// <summary>
/// 统一的「读容器」抽象：zip 与 7z/rar/tar 走各自的实现，但上层（结构探测）只认这一套。
/// 这样「内层有几个包 / 有没有包 / 是不是 mod 结构」的判断逻辑只有一份。
/// </summary>
public interface IArchiveReader : IDisposable
{
    IReadOnlyList<ArchiveEntry> Entries { get; }
    Stream OpenEntry(ArchiveEntry entry);
}

public static class ArchiveReaders
{
    /// <summary>交给 SharpCompress 的容器后缀（它可读 Rar / 7Zip / Tar / GZip / Arc / Arj / Ace）。</summary>
    private static readonly HashSet<string> SharpExts = new(StringComparer.OrdinalIgnoreCase)
        { ".7z", ".rar", ".tar", ".gz", ".tgz", ".arj", ".ace", ".arc" };

    public static bool IsSupportedContainer(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".zip", StringComparison.OrdinalIgnoreCase) || SharpExts.Contains(ext);
    }

    public static IArchiveReader Open(string path)
    {
        var ext = Path.GetExtension(path);
        if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            return new ZipArchiveReader(path);
        if (SharpExts.Contains(ext))
            return new SharpArchiveReader(path);
        throw new NotSupportedException($"不认识的容器格式：{ext}");
    }
}

/// <summary>zip 走 .NET 内置实现（快、稳；写包也用同一套）。</summary>
internal sealed class ZipArchiveReader : IArchiveReader
{
    private readonly ZipArchive _zip;

    public ZipArchiveReader(string path)
    {
        var stream = File.OpenRead(path);
        try
        {
            _zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch
        {
            stream.Dispose();
            throw;
        }

        Entries = _zip.Entries
            .Select(e => new ArchiveEntry
            {
                Key = e.FullName.Replace('\\', '/'),
                Size = e.Length,
                IsDirectory = e.FullName.EndsWith("/", StringComparison.Ordinal) || e.Length == 0 && e.Name.Length == 0,
            })
            .ToList();
    }

    public IReadOnlyList<ArchiveEntry> Entries { get; }

    public Stream OpenEntry(ArchiveEntry entry)
    {
        var e = _zip.GetEntry(entry.Key) ?? _zip.Entries.FirstOrDefault(x => x.FullName.Replace('\\', '/') == entry.Key);
        if (e is null) throw new FileNotFoundException("容器里没有这个条目：" + entry.Key);
        return e.Open();
    }

    public void Dispose() => _zip.Dispose();
}

/// <summary>7z / rar / tar 等交给 SharpCompress（纯托管，可读 Rar 与 7Zip）。</summary>
internal sealed class SharpArchiveReader : IArchiveReader
{
    private readonly IArchive _archive;
    private readonly Dictionary<string, IArchiveEntry> _byKey = new(StringComparer.Ordinal);

    public SharpArchiveReader(string path)
    {
        _archive = ArchiveFactory.Open(path);
        var list = new List<ArchiveEntry>();
        foreach (var e in _archive.Entries)
        {
            var key = (e.Key ?? string.Empty).Replace('\\', '/');
            if (key.Length == 0) continue;
            _byKey[key] = e;
            list.Add(new ArchiveEntry { Key = key, Size = e.Size, IsDirectory = e.IsDirectory });
        }
        Entries = list;
    }

    public IReadOnlyList<ArchiveEntry> Entries { get; }

    public Stream OpenEntry(ArchiveEntry entry)
    {
        if (!_byKey.TryGetValue(entry.Key, out var e))
            throw new FileNotFoundException("容器里没有这个条目：" + entry.Key);
        return e.OpenEntryStream();
    }

    public void Dispose() => _archive.Dispose();
}
