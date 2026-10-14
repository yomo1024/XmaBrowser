using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace XmaBrowser.Site;

/// <summary>
/// 压缩包索引：**不下载整包**，只用几次 Range 请求读 zip 的中央目录，就能在安装前
/// 列出包内清单、真实大小、以及里面装了几个 mod。
///
/// 本文件只做字节解析、不碰网络 → 可离线断言（见 tests/SiteTests）。
///
/// 实测依据（真实站点包，2026-10）：
///   · `.zip` / `.pmp` / `.ttmp2` **三种都是 zip 容器** → 都能这么读
///     （16 个样本的格式分布：.ttmp2 6 / .zip 4 / .pmp 3 / .7z 1 / .rar 1 / 外链 1
///      → 可列内容 13/15 ≈ 87%）
///   · `.7z` / `.rar` 不是 zip → 只能给大小，**如实降级，绝不假装能列**
///   · 尾部 256 KB 足以覆盖 EOCD + 中央目录（最大的样本包 258 MB 也够）
///   · 一个 zip 里可以塞多个 `.pmp`（实测有装 4 个的）→ 必须能把内嵌包名列出来，
///     否则用户根本不知道点「安装」会装进去几个 mod
/// </summary>
public static class PackageIndex
{
    /// <summary>取尾部多少字节来定位 EOCD。EOCD 的注释上限是 64 KB，256 KB 留足余量。</summary>
    public const int TailProbeBytes = 262_144;

    /// <summary>EOCD 结构本身的最小长度（无注释时）。</summary>
    public const int EocdMinBytes = 22;

    private const uint SigEocd        = 0x0605_4B50;
    private const uint SigZip64Eocd  = 0x0606_4B50;
    private const uint SigZip64Loc   = 0x0706_4B50;
    private const uint SigCentral    = 0x0201_4B50;
    private const uint U32Max        = 0xFFFF_FFFF;
    private const ushort U16Max      = 0xFFFF;

    public sealed record Entry(
        string Name, long UncompressedSize, long CompressedSize, int Method, long LocalHeaderOffset);

    /// <summary>扩展名（含点，小写）。取不到返回空串。</summary>
    /// <remarks>
    /// **既接受文件名/URL，也接受裸扩展名**（".zip"）—— 曾经因为只认前者，
    /// 导致「拿扩展名回头再判一遍」时全部判成 unrecognized（联网探针抓到的真 bug）。
    /// </remarks>
    public static string ExtensionOf(string? fileNameOrPath)
    {
        var s = StripQuery(fileNameOrPath);
        var slash = s.LastIndexOfAny(['/', '\\']);
        var name = slash >= 0 ? s[(slash + 1)..] : s;
        var dot = name.LastIndexOf('.');
        return dot >= 0 && dot < name.Length - 1 ? name[dot..].ToLowerInvariant() : string.Empty;
    }

    /// <summary>
    /// 是不是「zip 家族」——实测 .zip / .pmp / .ttmp2 三者都是 zip 容器。
    /// 判错会导致「明明能列却去下整包」或反之，所以三种都要认。
    /// </summary>
    public static bool IsZipFamily(string? fileNameOrPath) =>
        ExtensionOf(fileNameOrPath) is ".zip" or ".pmp" or ".ttmp2";

    /// <summary>7z / rar 明确不是 zip，界面据此显示「只能看大小」。</summary>
    public static bool IsKnownNonZipArchive(string? fileNameOrPath) =>
        ExtensionOf(fileNameOrPath) is ".7z" or ".rar";

    /// <summary>
    /// 在尾部字节里定位 EOCD，读出中央目录的位置/大小/条目数。
    /// <paramref name="tailFileOffset"/> = 这段字节在**整个文件里的绝对起始偏移**，
    /// 用于把 zip64 里的绝对位置换算成尾部缓冲区的相对下标。
    /// 标准 EOCD 读不出来时回退到 zip64（EOCD 前 20 字节的定位器）。
    /// </summary>
    public static bool TryFindEocd(
        ReadOnlySpan<byte> tail, long tailFileOffset,
        out long centralOffset, out long centralSize, out int entryCount)
    {
        centralOffset = 0;
        centralSize = 0;
        entryCount = 0;

        if (tail.Length < EocdMinBytes) return false;

        // 从后往前扫。EOCD 注释最长 64 KB，所以最多回扫 64KB+22 字节就够。
        var stop = Math.Max(0, tail.Length - 65_557);
        for (var i = tail.Length - EocdMinBytes; i >= stop; i--)
        {
            if (ReadU32(tail, i) != SigEocd) continue;

            var count  = ReadU16(tail, i + 10);
            var size   = ReadU32(tail, i + 12);
            var offset = ReadU32(tail, i + 16);

            if (count != U16Max && size != U32Max && offset != U32Max)
            {
                centralOffset = offset;
                centralSize = size;
                entryCount = count;
                return true;
            }

            // zip64：EOCD 之前 20 字节是定位器，它指向 zip64 EOCD 记录（8 字节字段）
            var loc = i - 20;
            if (loc < 0 || ReadU32(tail, loc) != SigZip64Loc) return false;

            var z64Abs = (long)ReadU64(tail, loc + 8);
            var rel = z64Abs - tailFileOffset;
            if (rel < 0 || rel + 56 > tail.Length || ReadU32(tail, (int)rel) != SigZip64Eocd) return false;

            var n = ReadU64(tail, (int)rel + 32);
            entryCount = n > int.MaxValue ? int.MaxValue : (int)n;
            centralSize = (long)ReadU64(tail, (int)rel + 40);
            centralOffset = (long)ReadU64(tail, (int)rel + 48);
            return true;
        }

        return false;
    }

    /// <summary>
    /// 解析中央目录字节 → 条目列表。遇到不认识的字节就停下（宁可少列，也不乱列）。
    /// zip64 的 0xFFFFFFFF 占位会去扩展字段（id 0x0001）里取真值。
    /// </summary>
    public static IReadOnlyList<Entry> ParseCentral(ReadOnlySpan<byte> central)
    {
        var list = new List<Entry>();
        var p = 0;

        while (p + 46 <= central.Length && ReadU32(central, p) == SigCentral)
        {
            var method   = ReadU16(central, p + 10);
            var csize    = ReadU32(central, p + 20);
            var usize    = ReadU32(central, p + 24);
            var nameLen  = ReadU16(central, p + 28);
            var extraLen = ReadU16(central, p + 30);
            var commentLen = ReadU16(central, p + 32);
            var lho      = ReadU32(central, p + 42);

            var name = p + 46 + nameLen <= central.Length
                ? Encoding.UTF8.GetString(central.Slice(p + 46, nameLen))
                : string.Empty;

            long us = usize, cs = csize, off = lho;
            if (usize == U32Max || csize == U32Max || lho == U32Max)
                ReadZip64Extra(central, p + 46 + nameLen, extraLen, ref us, ref cs, ref off);

            list.Add(new Entry(name, us, cs, method, off));
            p += 46 + nameLen + extraLen + commentLen;
        }

        return list;
    }

    /// <summary>zip64 扩展字段（id 0x0001）：只有对应字段是 0xFFFFFFFF 时才按序出现。</summary>
    private static void ReadZip64Extra(
        ReadOnlySpan<byte> central, int extraStart, int extraLen, ref long us, ref long cs, ref long off)
    {
        var end = Math.Min(extraStart + extraLen, central.Length);
        var e = extraStart;

        while (e + 4 <= end)
        {
            var id = ReadU16(central, e);
            var size = ReadU16(central, e + 2);
            var body = e + 4;

            if (id == 0x0001)
            {
                var q = body;
                if (us == U32Max && q + 8 <= central.Length) { us = (long)ReadU64(central, q); q += 8; }
                if (cs == U32Max && q + 8 <= central.Length) { cs = (long)ReadU64(central, q); q += 8; }
                if (off == U32Max && q + 8 <= central.Length) { off = (long)ReadU64(central, q); }
                return;
            }

            if (size <= 0) return;
            e = body + size;
        }
    }

    /// <summary>把条目清单折成人能看懂的一页摘要。</summary>
    public static PackageSummary Summarize(long bytes, string fileName, IReadOnlyList<Entry> entries)
    {
        var inner = entries
            .Where(e => IsInnerModPack(e.Name))
            .Select(e => ShortName(e.Name))
            .ToList();

        var top = entries
            .Select(e => ShortTop(e.Name))
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();

        var total = entries.Sum(e => Math.Max(0L, e.UncompressedSize));

        return new PackageSummary(bytes, fileName, entries.Count, total, inner, top);
    }

    private static bool IsInnerModPack(string name) =>
        name.EndsWith(".pmp", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".ttmp", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".ttmp2", StringComparison.OrdinalIgnoreCase);

    private static string ShortName(string path)
    {
        var s = path.Replace('\\', '/');
        var i = s.LastIndexOf('/');
        return i >= 0 ? s[(i + 1)..] : s;
    }

    private static string ShortTop(string path)
    {
        var s = path.Replace('\\', '/').Trim('/');
        var i = s.IndexOf('/');
        return i > 0 ? s[..i] : s;
    }

    private static string StripQuery(string? s)
    {
        var v = (s ?? string.Empty).Trim();
        var q = v.IndexOfAny(['?', '#']);
        return q >= 0 ? v[..q] : v;
    }

    /// <summary>人话大小。取不到（0）时返回 "?"，绝不显示成 0。</summary>
    public static string HumanSize(long bytes) =>
        bytes <= 0 ? "?"
        : bytes >= 1024L * 1024 * 1024 ? $"{bytes / 1073741824.0:0.00} GB"
        : bytes >= 1024L * 1024 ? $"{bytes / 1048576.0:0.00} MB"
        : bytes >= 1024 ? $"{bytes / 1024.0:0.0} KB"
        : $"{bytes} B";

    private static ushort ReadU16(ReadOnlySpan<byte> s, int at) =>
        at >= 0 && at + 2 <= s.Length ? BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(at, 2)) : (ushort)0;

    private static uint ReadU32(ReadOnlySpan<byte> s, int at) =>
        at >= 0 && at + 4 <= s.Length ? BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(at, 4)) : 0u;

    private static ulong ReadU64(ReadOnlySpan<byte> s, int at) =>
        at >= 0 && at + 8 <= s.Length ? BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(at, 8)) : 0ul;
}

/// <summary>装前看包的结果（给界面直接画）。</summary>
public sealed record PackageSummary(
    long Bytes,
    string FileName,
    int EntryCount,
    long UncompressedBytes,
    IReadOnlyList<string> InnerModPacks,
    IReadOnlyList<string> TopPaths)
{
    public string SizeText => PackageIndex.HumanSize(Bytes);

    /// <summary>里面装了几个 mod（内嵌 .pmp/.ttmp2 的个数）。</summary>
    public int ModCount => InnerModPacks.Count;

    public string UncompressedText => PackageIndex.HumanSize(UncompressedBytes);
}

/// <summary>
/// 装前看包的结果。**核心不变量：列不出内容时 Summary 必须是 null，Bytes 必须是 0**
/// —— 界面据此显示「只能看大小 / 探测失败」，绝不拿假数据糊弄。
/// </summary>
public sealed record PackageInspection(
    string FileName,
    string Extension,
    long Bytes,
    PackageSummary? Summary,
    string Note)
{
    /// <summary>真的列到了包内清单（区别于「只拿到大小」）。</summary>
    public bool HasListing => Summary is not null;

    public bool SizeKnown => Bytes > 0;

    public string SizeText => PackageIndex.HumanSize(Bytes);

    /// <summary>拿到了大小但列不出内容（.7z/.rar，或 zip 目录读不出来）。</summary>
    public static PackageInspection SizeOnly(string fileName, string ext, long bytes, string note) =>
        new(fileName, ext, bytes, null, note);

    /// <summary>列到了内容。</summary>
    public static PackageInspection Listed(PackageSummary summary) =>
        new(summary.FileName, PackageIndex.ExtensionOf(summary.FileName), summary.Bytes, summary, string.Empty);

    /// <summary>连大小都没拿到 —— Bytes = 0，界面必须显示「未知」而不是 0。</summary>
    public static PackageInspection Failed(string fileName, string ext, string note) =>
        new(fileName, ext, 0, null, note);
}
