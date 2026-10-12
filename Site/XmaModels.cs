using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace XmaBrowser.Site;

public static class XmaUrls
{
    public const string Root = "https://www.xivmodarchive.com";

    /// <summary>站点把这些请求当作 XHR/同源请求；实测纯 HttpClient 带这套头一律 200。</summary>
    public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";

    public static string ModPage(string modId) => $"{Root}/modid/{modId}";

    /// <summary>把搜索条件拼成站点 URL（参数名取自站点自己的表单，实测有效）。</summary>
    public static string Search(SearchQuery q)
    {
        var parts = new List<string>
        {
            "sortby=" + q.SortBy,
            "sortorder=" + q.SortOrder,
        };

        // NSFW 三态（**实测出来的语义**，别凭标签猜）：
        //   不传 nsfw 参数 → 混合：NSFW 与 SFW 都返回      ← 「显示 NSFW」应该用这一态
        //   nsfw=false     → 排除 NSFW
        //   nsfw=true      → **只看 NSFW**（不是「显示」！早先按 bool 传给成了这个，等于只显示 NSFW）
        // 实测方式：带登录态（浏览器通道）用具体 Mod 名搜 ——
        //   不传参 → NSFW「Floor Forced Doggy」总数 1、SFW「Pictish Tattoos」总数 1
        //   false  → NSFW 0、SFW 1      true → NSFW 1、SFW 0
        switch (q.NsfwMode)
        {
            case 2:
                parts.Add("nsfw=true");
                break;
            case 1:
                break;                      // 混合：故意不传 nsfw 参数
            default:
                parts.Add("nsfw=false");
                break;
        }

        if (q.OnlyDtCompatible)
            parts.Add("dt_compat=1");
        if (!string.IsNullOrWhiteSpace(q.Text))
            parts.Add("basic_text=" + Uri.EscapeDataString(q.Text.Trim()));
        if (!string.IsNullOrWhiteSpace(q.Name))
            parts.Add("name=" + Uri.EscapeDataString(q.Name.Trim()));
        if (!string.IsNullOrWhiteSpace(q.Author))
            parts.Add("author=" + Uri.EscapeDataString(q.Author.Trim()));
        if (!string.IsNullOrWhiteSpace(q.Tags))
            parts.Add("tags=" + Uri.EscapeDataString(q.Tags.Trim()));
        if (!string.IsNullOrWhiteSpace(q.Races))
            parts.Add("races=" + Uri.EscapeDataString(q.Races.Trim()));
        if (!string.IsNullOrWhiteSpace(q.Genders))
            parts.Add("genders=" + Uri.EscapeDataString(q.Genders.Trim()));
        if (!string.IsNullOrWhiteSpace(q.Affects))
            parts.Add("affects=" + Uri.EscapeDataString(q.Affects.Trim()));
        if (q.Types is { Count: > 0 })
            parts.Add("types=" + string.Join("%2C", q.Types));

        parts.Add("page=" + Math.Max(1, q.Page));
        return $"{Root}/search?" + string.Join("&", parts);
    }
}

public sealed class SearchQuery
{
    public string Text { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Tags { get; set; } = string.Empty;
    public string Races { get; set; } = string.Empty;
    public string Genders { get; set; } = string.Empty;
    public string Affects { get; set; } = string.Empty;
    public List<int>? Types { get; set; }

    /// <summary>
    /// NSFW 三态（**站点语义是实测出来的**）：
    /// 0 = 排除 NSFW（默认，URL 发 nsfw=false）；
    /// 1 = 混合，NSFW 与 SFW 都显示（**URL 不传 nsfw 参数**——这才是「显示 NSFW」）；
    /// 2 = 只看 NSFW（URL 发 nsfw=true）。
    /// </summary>
    public int NsfwMode { get; set; }

    public bool OnlyDtCompatible { get; set; } = true;
    public string SortBy { get; set; } = "time_published";
    public string SortOrder { get; set; } = "desc";
    public int Page { get; set; } = 1;
}

/// <summary>列表里的一条（来自搜索卡片）。</summary>
public sealed class SearchHit
{
    public string ModId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string AuthorId { get; set; } = string.Empty;
    public string ThumbUrl { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Genders { get; set; } = string.Empty;
    public int Views { get; set; }
    public int Downloads { get; set; }
    public int Followers { get; set; }
    /// <summary>站点在缩略图右上角盖的角标（new / updated）。</summary>
    public string Overlay { get; set; } = string.Empty;

    public string PageUrl => XmaUrls.ModPage(ModId);
    public bool IsNew => Overlay.Equals("new", StringComparison.OrdinalIgnoreCase);
    public bool IsUpdated => Overlay.Equals("updated", StringComparison.OrdinalIgnoreCase);
}

public sealed class SearchResult
{
    public List<SearchHit> Hits { get; set; } = [];
    public int Page { get; set; } = 1;
    /// <summary>站点提示的 Mod 总数（读不到就是 0）。</summary>
    public int TotalMods { get; set; }
    public string Url { get; set; } = string.Empty;
}

/// <summary>详情页解析结果。</summary>
public sealed class ModDetail
{
    public string ModId { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string AuthorId { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Affects { get; set; } = string.Empty;
    public string Races { get; set; } = string.Empty;
    public string Genders { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = [];
    public List<string> Images { get; set; } = [];
    public DateTime? LastUpdateLocal { get; set; }
    public DateTime? ReleaseLocal { get; set; }
    public string Version { get; set; } = string.Empty;

    /// <summary>下载链接的原始 href（可能是站内 /private/… 也可能是外链）。</summary>
    public string DownloadHref { get; set; } = string.Empty;
    public string AbsoluteDownloadUrl =>
        DownloadHref.Length > 0 && DownloadHref.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? DownloadHref
            : DownloadHref.Length > 0 ? XmaUrls.Root + DownloadHref : string.Empty;

    /// <summary>true = 作者把文件放在外部站点（Patreon 等），插件拿不到直链，只能开浏览器。</summary>
    public bool IsExternalLink =>
        AbsoluteDownloadUrl.Length > 0 && !AbsoluteDownloadUrl.StartsWith(XmaUrls.Root, StringComparison.OrdinalIgnoreCase);

    /// <summary>文件名（从 URL 猜，用于落盘命名）。</summary>
    public string FileNameHint
    {
        get
        {
            var url = AbsoluteDownloadUrl;
            if (url.Length == 0) return string.Empty;
            var seg = url.Split('?')[0].TrimEnd('/').Split('/');
            var last = seg.Length > 0 ? seg[^1] : string.Empty;
            try { return Uri.UnescapeDataString(last); } catch { return last; }
        }
    }
}

/// <summary>站点上的时间串有两种形态（实测）：匿名 = UTC 英文格式；登录态 = 本地 "2026/9/21 08:34:00"。</summary>
public static partial class XmaTime
{
    [GeneratedRegex(@"(\d{4})/(\d{1,2})/(\d{1,2})\s+(\d{1,2}):(\d{2}):(\d{2})")]
    private static partial Regex LocalForm();

    [GeneratedRegex(@"([A-Za-z]{3})\s+([A-Za-z]{3})\s+(\d{1,2})\s+(\d{4})\s+(\d{2}):(\d{2}):(\d{2})")]
    private static partial Regex UtcForm();

    public static DateTime? Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();

        var lm = LocalForm().Match(s);
        if (lm.Success)
        {
            try
            {
                return new DateTime(int.Parse(lm.Groups[1].Value), int.Parse(lm.Groups[2].Value),
                                    int.Parse(lm.Groups[3].Value), int.Parse(lm.Groups[4].Value),
                                    int.Parse(lm.Groups[5].Value), int.Parse(lm.Groups[6].Value),
                                    DateTimeKind.Local);
            }
            catch { /* 落到下面 */ }
        }

        var um = UtcForm().Match(s);
        if (um.Success)
        {
            try
            {
                var utc = new DateTime(int.Parse(um.Groups[4].Value),
                                       DateTime.ParseExact(um.Groups[2].Value, "MMM", CultureInfo.InvariantCulture).Month,
                                       int.Parse(um.Groups[3].Value), int.Parse(um.Groups[5].Value),
                                       int.Parse(um.Groups[6].Value), int.Parse(um.Groups[7].Value),
                                       DateTimeKind.Utc);
                return utc.ToLocalTime();
            }
            catch { /* 落到下面 */ }
        }

        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }
}
