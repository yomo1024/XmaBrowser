using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace XmaBrowser.Site;

/// <summary>
/// XIVModArchive 页面解析。所有选择器都是**照着真实页面结构写**的（samples/ 下就是当时抓的原文），
/// 站点改版时先看回归测试哪条挂了，再改这里。
/// </summary>
public static class XmaParser
{
    public static SearchResult ParseSearch(string html, int page = 1, string url = "")
    {
        var doc = Load(html);
        var res = new SearchResult { Page = page, Url = url };

        foreach (var card in doc.DocumentNode.Descendants("div").Where(n => HasClass(n, "mod-card")))
        {
            var link = card.Descendants("a")
                           .FirstOrDefault(a => a.GetAttributeValue("href", "").StartsWith("/modid/", StringComparison.Ordinal));
            if (link == null) continue;

            var mm = Regex.Match(link.GetAttributeValue("href", ""), @"/modid/(\d+)");
            if (!mm.Success) continue;

            var hit = new SearchHit { ModId = mm.Groups[1].Value };

            var thumb = card.Descendants("img").FirstOrDefault(i => HasClass(i, "mod-card-img"));
            if (thumb != null) hit.ThumbUrl = thumb.GetAttributeValue("src", "").Trim();

            var overlay = card.Descendants("img").FirstOrDefault(i => HasClass(i, "mod-card-overlay"));
            if (overlay != null)
            {
                var src = overlay.GetAttributeValue("src", "");
                var file = src.Split('/').LastOrDefault() ?? "";
                hit.Overlay = System.IO.Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            }

            var h5 = card.Descendants("h5").FirstOrDefault();
            if (h5 != null)
            {
                // 卡片标题被 CSS 截断，完整标题在 tooltip 的 title 属性里
                var tip = h5.GetAttributeValue("title", "");
                hit.Title = CleanText(string.IsNullOrWhiteSpace(tip) ? h5.InnerText : tip);
            }

            var author = card.Descendants("a")
                             .FirstOrDefault(a => a.GetAttributeValue("href", "").StartsWith("/user/", StringComparison.Ordinal));
            if (author != null)
            {
                hit.Author = CleanText(author.InnerText);
                hit.AuthorId = Digits(author.GetAttributeValue("href", ""));
            }

            foreach (var code in card.Descendants("code"))
            {
                var t = CleanText(code.InnerText);
                if (t.StartsWith("Type:", StringComparison.OrdinalIgnoreCase))
                    hit.Type = t[5..].Trim();
                else if (t.StartsWith("Genders:", StringComparison.OrdinalIgnoreCase))
                    hit.Genders = t[8..].Trim();
            }

            foreach (var span in card.Descendants("span"))
            {
                var title = span.GetAttributeValue("title", "");
                var value = ToInt(span.InnerText);
                switch (title)
                {
                    case "Lifetime Views": hit.Views = value; break;
                    case "Lifetime Downloads": hit.Downloads = value; break;
                    case "Followers": hit.Followers = value; break;
                }
            }

            res.Hits.Add(hit);
        }

        // 站点把总数写成 <code>53278 Results</code>
        res.TotalMods = ParseTotalMods(doc.DocumentNode.InnerText ?? string.Empty);

        return res;
    }

    /// <summary>
    /// 只取站点自报的总条数（页面里是 <c>&lt;code&gt;53278 Results&lt;/code&gt;</c>）。
    /// 顺带也是判断「这条通道有没有带登录态」的抓手：
    /// 混合模式 未登录=53281、带登录态=61787（=53281 普通 + 8506 NSFW）。
    /// </summary>
    public static int ParseTotalMods(string html)
    {
        if (string.IsNullOrEmpty(html)) return 0;
        var total = Regex.Match(html, @"([\d,]+)\s*(?:Results?|Mods?)\b", RegexOptions.IgnoreCase);
        return total.Success ? ToInt(total.Groups[1].Value) : 0;
    }

    public static ModDetail ParseDetail(string html, string modId = "", string url = "")
    {
        var doc = Load(html);
        var d = new ModDetail { ModId = modId, Url = url };
        if (string.IsNullOrEmpty(d.ModId))
        {
            var m = Regex.Match(url, @"/modid/(\d+)");
            if (m.Success) d.ModId = m.Groups[1].Value;
        }

        var h1 = doc.DocumentNode.SelectSingleNode("//h1[contains(@class,'display-5')]")
                 ?? doc.DocumentNode.SelectSingleNode("//h1");
        if (h1 != null) d.Title = CleanText(h1.InnerText);

        // 作者：取正文里第一个「有可见文字」的 /user/ 链接。
        // 注意站点这段 HTML 是非法嵌套（<a class="user-card-link"><a class="user-card-link">名字</a></a>），
        // 解析器会把内层自动闭合到同级 → 外层文本为空，所以必须按「有文字」挑，不能只看 class。
        var author = doc.DocumentNode.Descendants("a")
                        .Where(a => a.GetAttributeValue("href", "").StartsWith("/user/", StringComparison.Ordinal))
                        .FirstOrDefault(a => CleanText(a.InnerText).Length > 0);
        if (author != null)
        {
            d.Author = CleanText(author.InnerText);
            d.AuthorId = Digits(author.GetAttributeValue("href", ""));
        }

        var dl = doc.DocumentNode.SelectSingleNode("//a[@id='mod-download-link']");
        if (dl != null) d.DownloadHref = dl.GetAttributeValue("href", "").Trim();

        // 元信息块：每个块是「标签 : 值」，值在 <code> 里（可能是链接列表）
        foreach (var block in doc.DocumentNode.SelectNodes("//div[contains(@class,'mod-meta-block')]")
                                 ?? new HtmlNodeCollection(null))
        {
            var text = CleanText(block.InnerText);
            var colon = text.IndexOf(':');
            if (colon <= 0) continue;
            var label = text[..colon].Trim().ToLowerInvariant();
            var value = text[(colon + 1)..].Trim();

            if (label.StartsWith("last version update")) d.LastUpdateLocal = XmaTime.Parse(value);
            else if (label.StartsWith("original release date")) d.ReleaseLocal = XmaTime.Parse(value);
            else if (label.StartsWith("affects")) d.Affects = value;
            else if (label.StartsWith("races")) d.Races = value;
            else if (label.StartsWith("genders")) d.Genders = value;
            else if (label.StartsWith("tags")) d.Tags = SplitList(value);
            else if (label.StartsWith("version")) d.Version = value;
        }

        // 标签兜底：直接抓 /search?tags= 链接（部分页面元信息块里没有 Tags 行）
        if (d.Tags.Count == 0)
        {
            d.Tags = doc.DocumentNode.Descendants("a")
                         .Select(a => a.GetAttributeValue("href", ""))
                         .Where(h => h.StartsWith("/search?tags=", StringComparison.Ordinal))
                         .Select(h => Uri.UnescapeDataString(h["/search?tags=".Length..]))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .ToList();
        }

        // 预览图：站点用 openImage('…') 挂到图上
        d.Images = Regex.Matches(html ?? "", @"openImage\(\s*'([^']+)'\s*\)")
                        .Select(m => m.Groups[1].Value)
                        .Where(u => u.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

        // 类型：详情页里也有 Type 行（有些页面放在元信息块外）
        if (string.IsNullOrEmpty(d.Type))
        {
            var tm = Regex.Match(doc.DocumentNode.InnerText ?? "", @"Type\s*:\s*([A-Za-z /]+)");
            if (tm.Success) d.Type = tm.Groups[1].Value.Trim();
        }

        d.Description = ExtractDescription(doc);
        return d;
    }

    /// <summary>描述：优先取 #info 里「Author's Comments」那一段，取不到就整个 #info。</summary>
    private static string ExtractDescription(HtmlDocument doc)
    {
        var info = doc.DocumentNode.SelectSingleNode("//div[@id='info']");
        if (info == null) return string.Empty;

        HtmlNode? target = null;
        foreach (var lead in info.SelectNodes(".//p[contains(@class,'lead')]") ?? new HtmlNodeCollection(null))
        {
            if (!lead.InnerText.Contains("Comments", StringComparison.OrdinalIgnoreCase)) continue;
            var sib = lead.NextSibling;
            while (sib != null && sib.NodeType == HtmlNodeType.Text) sib = sib.NextSibling;
            if (sib != null && sib.Name.Equals("div", StringComparison.OrdinalIgnoreCase)) target = sib;
            break;
        }

        var text = CleanText((target ?? info).InnerText);
        return text.Length > 4000 ? text[..4000] : text;
    }

    private static HtmlDocument Load(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html ?? string.Empty);
        return doc;
    }

    private static bool HasClass(HtmlNode node, string cls)
    {
        var c = node.GetAttributeValue("class", "");
        return (" " + c.Trim() + " ").Contains(" " + cls + " ", StringComparison.OrdinalIgnoreCase);
    }

    private static string CleanText(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var t = HtmlEntity.DeEntitize(s);
        t = Regex.Replace(t, @"\s+", " ");
        return t.Trim();
    }

    private static List<string> SplitList(string value) =>
        value.Split([',', '、'], StringSplitOptions.RemoveEmptyEntries)
             .Select(x => x.Trim())
             .Where(x => x.Length > 0)
             .Distinct(StringComparer.OrdinalIgnoreCase)
             .ToList();

    private static int ToInt(string? s)
    {
        var d = Digits(s ?? "");
        return int.TryParse(d, out var v) ? v : 0;
    }

    private static string Digits(string s) => new(s.Where(char.IsDigit).ToArray());
}
