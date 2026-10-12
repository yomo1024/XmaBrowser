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
}
