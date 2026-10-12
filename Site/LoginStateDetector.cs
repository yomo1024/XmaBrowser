using System;

namespace XmaBrowser.Site;

/// <summary>页面反映出的站点登录态。</summary>
public enum XmaLoginState
{
    /// <summary>认不出来（页面为空 / 结构变化）。</summary>
    Unknown,

    /// <summary>未登录（页面里有登录入口）。</summary>
    LoggedOut,

    /// <summary>已登录（页面里有登出入口）。</summary>
    LoggedIn,

    /// <summary>Cloudflare 挑战页（不是真页面）。</summary>
    CloudflareChallenge,
}

/// <summary>
/// 一次页面取回后的客观事实（全是能从 HTML 直接数出来的东西，不做推测）。
/// </summary>
/// <param name="State">登录态。</param>
/// <param name="TotalMods">站点自报的总条数（53281 普通 / 61787 普通+NSFW / 8506 只看 NSFW）。</param>
/// <param name="CardCount">本页卡片数（精确数 <c>class="mod-card </c> 带空格的那种，实测一页 15 条；
    /// 注意还有 <c>mod-card-img-container</c> / <c>mod-card-overlay</c>，它们后面紧跟引号，别误数）。</param>
/// <param name="NsfwButtonDisabled">站点那个 NSFW 三态按钮是否处于 disabled（未登录时禁用）。</param>
/// <param name="Note">一句话说明。</param>
public sealed record XmaPageFacts(XmaLoginState State, int TotalMods, int CardCount, bool NsfwButtonDisabled, string Note);

/// <summary>
/// 从页面 HTML 判断「有没有登录站点」。
///
/// 判据是**实测对比**出来的（同一条 URL，未登录直连 vs 带登录态的浏览器通道）：
///   · 未登录页：href="/login" 出现 1 次、出现「Log In with Discord」、NSFW 三态按钮带 disabled
///   · 已登录页：href="/logout" 出现 1 次、没有登录入口、NSFW 按钮**不带** disabled
/// 这两条互斥且稳定，所以直接用它判登录态，不需要去解密浏览器的 cookie。
/// </summary>
public static class LoginStateDetector
{
    public static XmaPageFacts Analyze(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return new XmaPageFacts(XmaLoginState.Unknown, 0, 0, false, "页面为空");

        if (LooksLikeChallenge(html))
            return new XmaPageFacts(XmaLoginState.CloudflareChallenge, 0, CountOf(html, "class=\"mod-card "),
                                    false, "这是 Cloudflare 挑战页，不是真页面");

        var total = XmaParser.ParseTotalMods(html);
        var cards = CountOf(html, "class=\"mod-card ");
        var nsfwDisabled = NsfwButtonDisabled(html);

        var hasLogout = html.Contains("href=\"/logout\"", StringComparison.OrdinalIgnoreCase);
        var hasLogin = html.Contains("href=\"/login\"", StringComparison.OrdinalIgnoreCase) ||
                       html.Contains("Log In with Discord", StringComparison.OrdinalIgnoreCase);

        if (hasLogout && !hasLogin)
            return new XmaPageFacts(XmaLoginState.LoggedIn, total, cards, nsfwDisabled,
                                    $"已登录（页面有登出入口；站点总数 {total}）");

        if (hasLogin && !hasLogout)
            return new XmaPageFacts(XmaLoginState.LoggedOut, total, cards, nsfwDisabled,
                                    $"未登录（页面有登录入口；站点总数 {total}）");

        return new XmaPageFacts(XmaLoginState.Unknown, total, cards, nsfwDisabled,
                                "看不出登录态（登录/登出入口都没有，站点结构可能变了）");
    }

    public static bool LooksLikeChallenge(string? html)
    {
        if (string.IsNullOrEmpty(html)) return false;
        return html.Contains("Just a moment", StringComparison.OrdinalIgnoreCase)
               || html.Contains("cf-mitigated", StringComparison.OrdinalIgnoreCase)
               || html.Contains("_cf_chl_opt", StringComparison.OrdinalIgnoreCase)
               || html.Contains("cf-chl", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 站点那个 NSFW 三态按钮（<c>ternary-nsfw</c>）是不是被禁用了。
    /// 未登录时站点会给它加 disabled（悬停提示「You must log in and enable NSFW content…」）。
    /// </summary>
    public static bool NsfwButtonDisabled(string? html)
    {
        if (string.IsNullOrEmpty(html)) return false;

        var at = html.IndexOf("ternary-nsfw", StringComparison.OrdinalIgnoreCase);
        while (at >= 0)
        {
            // 看这个元素标签结束（'>'）之前有没有 disabled
            var end = html.IndexOf('>', at);
            if (end < 0) break;
            var tag = html.AsSpan(at, end - at);
            if (tag.Contains("disabled", StringComparison.OrdinalIgnoreCase)) return true;
            at = html.IndexOf("ternary-nsfw", end, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static int CountOf(string haystack, string needle)
    {
        var n = 0;
        var at = 0;
        while ((at = haystack.IndexOf(needle, at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            n++;
            at += needle.Length;
        }
        return n;
    }
}
