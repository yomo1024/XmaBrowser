using System;

namespace XmaBrowser.Site;

/// <summary>站点为什么拒绝这次直连请求 —— 直接决定该不该降级到浏览器通道。</summary>
public enum XmaBlockReason
{
    /// <summary>没被拒绝。</summary>
    None,
    /// <summary>Cloudflare 人机校验（Just a moment）：**与内容是否 NSFW 无关**，公开内容一样会被拦。</summary>
    CloudflareChallenge,
    /// <summary>站点要求登录态（NSFW 等）。</summary>
    NeedsLogin,
    /// <summary>/private/ 下载直链拿不到（这类文件要带登录态的浏览器去取）。</summary>
    DownloadNeedsBrowser,
    /// <summary>其它 HTTP 错误。</summary>
    Other,
}

public sealed class XmaAccessException(string message, XmaBlockReason reason = XmaBlockReason.Other)
    : Exception(message)
{
    public XmaBlockReason Reason { get; } = reason;
}

/// <summary>
/// 判定「直连被拒」的真实原因。
///
/// 为什么要这么较真：**Cloudflare 人机校验和 NSFW 需要登录是两码事**，
/// 但两者都表现为 403。以前一律按「需要登录态」报，于是公开内容也走了浏览器通道、
/// 还给出让人摸不着头脑的提示（主人当场问过：不是 NSFW 为什么要走通道）。
///
/// 判定依据（可靠度从高到低）：
///   1. 响应头 `cf-mitigated: challenge` —— Cloudflare 主动告诉你这是挑战（最准）
///   2. 响应体里的挑战页特征（`Just a moment...` / `cf-chl` / `challenge-platform`）
///   3. 都没有且是 403 → 才算「需要登录态」
/// </summary>
public static class XmaBlockClassifier
{
    private static readonly string[] ChallengeMarkers =
        ["Just a moment", "cf-chl", "challenge-platform", "cf-challenge", "_cf_chl_opt"];

    public static XmaBlockReason Classify(int status, string? body, bool cfMitigated)
    {
        if (status is >= 200 and < 300) return XmaBlockReason.None;

        if (cfMitigated) return XmaBlockReason.CloudflareChallenge;

        if (status == 403)
        {
            if (body is not null && LooksLikeChallenge(body)) return XmaBlockReason.CloudflareChallenge;
            return XmaBlockReason.NeedsLogin;
        }

        if (status == 404) return XmaBlockReason.DownloadNeedsBrowser;

        return XmaBlockReason.Other;
    }

    private static bool LooksLikeChallenge(string body)
    {
        foreach (var marker in ChallengeMarkers)
            if (body.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>给用户看的一句话（说明「为什么走了浏览器通道」）。</summary>
    public static string Describe(XmaBlockReason reason) => reason switch
    {
        XmaBlockReason.CloudflareChallenge =>
            "站点在做 Cloudflare 人机校验（Just a moment）—— 与内容是否 NSFW 无关，公开内容也会被拦；" +
            "通常过一会儿自己会好，本次交浏览器通道取。",
        XmaBlockReason.NeedsLogin =>
            "站点要求登录态（NSFW 等内容）—— 交浏览器通道（借用本机已登录的浏览器）取。",
        XmaBlockReason.DownloadNeedsBrowser =>
            "下载直链拿不到（这类文件要带登录态的浏览器去取）—— 交浏览器通道取。",
        _ => "直连被站点拒绝，改用浏览器通道。",
    };

    /// <summary>短标签，给队列/详情当角标用。</summary>
    public static string ShortLabel(XmaBlockReason reason) => reason switch
    {
        XmaBlockReason.CloudflareChallenge => "站点人机校验",
        XmaBlockReason.NeedsLogin => "需要登录态",
        XmaBlockReason.DownloadNeedsBrowser => "直链需浏览器",
        _ => "直连被拒",
    };
}
