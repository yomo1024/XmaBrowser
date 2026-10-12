using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using XmaBrowser.Browser;
using XmaBrowser.Site;

namespace XmaBrowser.Diagnostics;

/// <summary>
/// NSFW 通道 / 站点登录态自检。
///
/// 为什么需要它：站点只在**登录态**下才把 NSFW 并进「混合」列表
/// （实测同一条 URL：未登录直连 总数 53281 ＝ 纯普通内容；带登录态通道 61787 ＝ 53281 + 8506 NSFW）。
/// 而插件的浏览器通道是「复制本机浏览器的 cookies → 起 headless」：
/// **挑错浏览器/资料目录就等于没登录**，表现就是「选了混合却搜不到 NSFW」。
/// 这个自检把「借的谁、有没有登录、拿到多少条」全部摊开，一次定位。
/// </summary>
public static class NsfwDiagnostics
{
    public static async Task<string> RunAsync(BrowserChannel channel, IBrowserSettings cfg,
                                              CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== NSFW 通道 / 站点登录态自检 ===");
        sb.AppendLine($"浏览器通道：{(channel.Enabled ? "已启用" : "**未启用** —— 先去「设置」勾上「启用浏览器通道」")}");
        sb.AppendLine($"设置里的浏览器：{(string.IsNullOrWhiteSpace(cfg.BrowserExe) ? "（未指定，自动挑选）" : cfg.BrowserExe)}");
        sb.AppendLine($"用户数据目录　：{(string.IsNullOrWhiteSpace(cfg.BrowserUserDataDir) ? "（未指定，随浏览器自动推导）" : cfg.BrowserUserDataDir)}");
        sb.AppendLine($"自动挑 profile：{cfg.AutoPickProfile}");
        sb.AppendLine($"插件自带 profile：{channel.OwnedProfileDir}");
        sb.AppendLine($"  → 里面有没有 XIVModArchive 登录态？{(channel.OwnedProfileHasXmaCookie ? "**有**（通道会优先用它）" : "没有 —— 在「关于」页点「登录 XIVModArchive」即可")}");
        sb.AppendLine();

        sb.AppendLine("── 本机探测到的浏览器 ──");
        var detected = BrowserChannel.Detect();
        if (detected.Count == 0)
            sb.AppendLine("  （一个都没探测到）");
        foreach (var c in detected)
        {
            var hasCookie = BrowserChannel.ProfileHasXmaCookie(c);
            sb.AppendLine($"  · {c.Label}");
            sb.AppendLine($"      exe     : {c.Exe}{(File.Exists(c.Exe) ? string.Empty : "   ← 不存在")}");
            sb.AppendLine($"      profile : {c.UserDataDir}");
            sb.AppendLine($"      Cookies : {(File.Exists(c.CookieFile) ? "有" : "无")}    Local State : {(File.Exists(c.LocalState) ? "有" : "无")}");
            sb.AppendLine($"      站点 cookie：{(hasCookie ? "有（登录态很可能在这个 profile 里）" : "没有")}");
        }
        sb.AppendLine();

        if (!channel.Enabled)
        {
            sb.AppendLine("结论：通道没开，NSFW 模式拿不到 NSFW。");
            return sb.ToString();
        }

        // 三态各取一次页面（都用通道，才知道登录态下到底给多少），顺便对比出「混合是否含 NSFW」
        var mixedUrl = XmaUrls.Search(new SearchQuery { NsfwMode = 1, Page = 1 });
        var sfwUrl = XmaUrls.Search(new SearchQuery { NsfwMode = 0, Page = 1 });
        var nsfwUrl = XmaUrls.Search(new SearchQuery { NsfwMode = 2, Page = 1 });

        var mixedHtml = await channel.GetHtmlAsync(mixedUrl, ct).ConfigureAwait(false);
        sb.AppendLine($"使用中的浏览器：{(channel.LastUsedBrowser is { } b ? $"{b.Label}（profile: {b.UserDataDir}）" : "（还没起来）")}");

        if (mixedHtml is null || mixedHtml.Length == 0)
        {
            sb.AppendLine("用通道取「混合」搜索页：**失败** —— " + channel.LastError);
            sb.AppendLine();
            sb.AppendLine("结论：通道起不来或取不到页面。常见原因：");
            sb.AppendLine("  · 浏览器路径/用户数据目录填错（上面「探测到的浏览器」里 exe 标了「不存在」的就是）");
            sb.AppendLine("  · 浏览器正在运行且占用 profile（插件会复制一份再用，一般不受影响）");
            sb.AppendLine("  · 启动超时（设置里的「浏览器通道超时」）");
            return sb.ToString();
        }

        var facts = LoginStateDetector.Analyze(mixedHtml);
        sb.AppendLine();
        sb.AppendLine("── 用通道取「混合」搜索页的结果 ──");
        sb.AppendLine($"  登录态　：{Describe(facts.State)}");
        sb.AppendLine($"  站点总数：{facts.TotalMods}（未登录混合=53281；带登录态混合=61787 = 53281 普通 + 8506 NSFW）");
        sb.AppendLine($"  本页卡片：{facts.CardCount}");
        sb.AppendLine($"  NSFW 按钮：{(facts.NsfwButtonDisabled ? "禁用（未登录的表现）" : "可用")}");
        sb.AppendLine($"  说明　　：{facts.Note}");
        sb.AppendLine();

        if (facts.State == XmaLoginState.CloudflareChallenge)
        {
            sb.AppendLine("结论：被 Cloudflare 挑战拦了（不是登录问题）。稍后再试；或换一个浏览器 profile。");
            return sb.ToString();
        }

        if (facts.State == XmaLoginState.LoggedOut)
        {
            sb.AppendLine("结论：**通道用的身份没有登录 XIVModArchive** → 混合模式只会拿到普通内容，NSFW 不显示。");
            sb.AppendLine("要修（首选，一次就好）：");
            sb.AppendLine("  0) 在「关于」页点「登录 XIVModArchive」→ 在弹出的浏览器窗口里过一次 Discord 授权。");
            sb.AppendLine("     登录态会存进插件自带的 profile，以后通道一直用它，不再依赖你平时用的浏览器。");
            sb.AppendLine("  ── 或者：复用你已有浏览器的登录态 ──");
            sb.AppendLine("  1) 用上面「站点 cookie：有」的那个浏览器打开 xivmodarchive.com 并登录；");
            sb.AppendLine("  2) 或到「设置」把「浏览器程序 / 用户数据目录」指到那个浏览器；");
            sb.AppendLine("  3) 再点一次本自检，确认变成「已登录」。");
            return sb.ToString();
        }

        // 已登录：再比一次「排除」与「只看 NSFW」，用算术判定混合里到底有没有 NSFW
        var sfwHtml = await channel.GetHtmlAsync(sfwUrl, ct).ConfigureAwait(false);
        var nsfwHtml = await channel.GetHtmlAsync(nsfwUrl, ct).ConfigureAwait(false);
        var sfwTotal = XmaParser.ParseTotalMods(sfwHtml ?? string.Empty);
        var nsfwTotal = XmaParser.ParseTotalMods(nsfwHtml ?? string.Empty);

        sb.AppendLine("── 三态对照（都走通道）──");
        sb.AppendLine($"  排除 NSFW：{sfwTotal} 条");
        sb.AppendLine($"  混合　　　：{facts.TotalMods} 条");
        sb.AppendLine($"  只看 NSFW：{nsfwTotal} 条");
        sb.AppendLine();

        if (facts.TotalMods > sfwTotal)
        {
            sb.AppendLine($"结论：✅ 通道带登录态且混合含 NSFW（{facts.TotalMods} > {sfwTotal} 纯普通）。");
            sb.AppendLine("      插件里把「内容」选「含 NSFW（混合）」就能看到 NSFW 条目了。");
        }
        else if (facts.TotalMods > 0 && facts.TotalMods == sfwTotal && nsfwTotal > 0)
        {
            sb.AppendLine($"结论：**已登录，但站点没把 NSFW 并进混合列表**（混合 {facts.TotalMods} ＝ 排除 {sfwTotal}）。");
            sb.AppendLine("      这类不是插件问题：站点账号里要把 NSFW 内容打开 ——");
            sb.AppendLine("      在浏览器登录状态下打开 xivmodarchive.com → 右上账号 → 设置里勾选「启用/显示 NSFW 内容」，再自检一次。");
        }
        else
        {
            sb.AppendLine("结论：数据对不上，请把这份报告整份发我。");
        }

        return sb.ToString();
    }

    private static string Describe(XmaLoginState s) => s switch
    {
        XmaLoginState.LoggedIn => "**已登录**",
        XmaLoginState.LoggedOut => "**未登录**",
        XmaLoginState.CloudflareChallenge => "Cloudflare 挑战页",
        _ => "认不出来",
    };
}
