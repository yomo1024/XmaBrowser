namespace XmaBrowser.Site;

/// <summary>
/// 搜索返回 0 条时，把**原因**说清楚。
///
/// 动因：主人报「搜不到」，但界面上只显示一句「这一页没有结果 —— 换个关键词，或放宽筛选」，
/// 这句话**既不能让他自救、也不能让我定位**。0 条至少有四种完全不同的成因：
///   ① 站点确实没有匹配（换个关键词就行）
///   ② 站点回的是 Cloudflare 人机校验页（不是搜索结果）
///   ③ 站点回的是未登录页面（这条搜索需要登录态）
///   ④ 页面里明明有卡片、却一张都没解析出来（站点改版 → 解析规则要更新）
///
/// 本模块只依据**页面本身**判定，判不出来就直说「站点确实回了 0 条」并附页面大小，
/// 绝不编一个听起来合理的理由。
/// </summary>
public static class EmptyResultDiagnosis
{
    /// <summary>低于这个字节数的页面基本不可能是正常搜索结果页（空文档只有几十字节）。</summary>
    public const int SuspiciouslySmallBytes = 2048;

    public static string Describe(string? html, string url)
    {
        var page = html ?? string.Empty;

        if (page.Length == 0) return $"站点没返回任何内容；实际请求 {url}";

        var facts = LoginStateDetector.Analyze(page);

        var reason = facts.State switch
        {
            XmaLoginState.CloudflareChallenge => "站点回的是 Cloudflare 人机校验页，不是搜索结果",
            // 「页面有卡片却一张都没解析出来」比「未登录」更具体：普通列表页本来就带登录链接，
            // 若把未登录排在前面，解析失败会被误报成「需要登录态」。所以这条优先。
            _ when page.Length < SuspiciouslySmallBytes =>
                $"站点回的页面异常小（{page.Length} 字节），可能被拦了",
            _ when facts.CardCount > 0 =>
                $"页面里有 {facts.CardCount} 张卡片，但一张都没解析出来（多半是站点改版，需要更新解析规则）",
            XmaLoginState.LoggedOut => "站点回的是未登录页面（这条搜索可能需要登录态）",
            _ => $"站点确实回了 0 条（页面 {page.Length} 字节）",
        };

        return $"{reason}；实际请求 {url}";
    }
}
