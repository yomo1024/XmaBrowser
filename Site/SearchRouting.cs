namespace XmaBrowser.Site;

/// <summary>
/// 搜索/详情该走哪条路：直连（未登录的 HttpClient）还是浏览器通道（借用本机浏览器的站点登录态）。
///
/// **为什么 NSFW 模式必须走通道**（实测数据，同一 URL「混合＝不传 nsfw 参数」）：
///   · 直连（未登录）   总数 53281 = 只有普通内容
///   · 浏览器通道（登录）总数 61787 = 53281 + 8506（8506 正好是「只看 NSFW」的总数）
/// 即**站点只在登录态下才把 NSFW 并进列表**。所以「含 NSFW（混合）」「只看 NSFW」这两种模式
/// 必须带登录态去取，否则直连再多试也拿不到 NSFW（主人最初看到的就是这个）。
/// </summary>
public static class SearchRouting
{
    /// <summary>NSFW 模式（0=排除 1=混合 2=只看）是否必须优先走浏览器通道。</summary>
    public static bool MustUseBrowserChannel(int nsfwMode, bool channelEnabled, bool autoFallbackToBrowser)
        => nsfwMode != 0 && channelEnabled && autoFallbackToBrowser;

    /// <summary>
    /// 通道拿到 0 条时，要不要再用直连兜一次。
    ///
    /// 为什么需要：混合/只看 NSFW 模式下**每次搜索都走浏览器通道**，而通道是 headless Chrome，
    /// 被 Cloudflare 挑战或不带登录态时就会返回 0 张卡片 —— 用户看到的是「一条都没有」，
    /// 且不知道为什么。直连虽然拿不到 NSFW，但至少能给出 SFW 结果 + 说明「本次可能缺 NSFW」，
    /// 这比一片空白有用得多。
    ///
    /// 不会误伤：登录态下的混合结果 ⊇ 未登录的直连结果，所以通道真的是 0 条时，
    /// 直连不可能凭空多出结果来 —— 只有通道页面本身坏了，兜底才会有产出。
    /// </summary>
    public static bool ShouldFallbackToDirect(int channelHits, bool autoFallbackToBrowser)
        => autoFallbackToBrowser && channelHits == 0;
}
