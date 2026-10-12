using System;
using Dalamud.Configuration;
using XmaBrowser.Browser;
using XmaBrowser.Site;

namespace XmaBrowser;

[Serializable]
public class Configuration : IPluginConfiguration, IXmaSettings, IBrowserSettings
{
    public int Version { get; set; } = 1;

    // ---------------- 下载与安装 ----------------

    /// <summary>下载缓存目录（空 = 用插件配置目录下的 downloads）。</summary>
    public string CacheDirectory { get; set; } = string.Empty;

    /// <summary>装完后自动在（当前生效的）集合里启用。</summary>
    public bool EnableOnInstall { get; set; } = true;

    /// <summary>装完后触发重绘，让 Mod 立刻在游戏里可见。</summary>
    public bool RedrawOnInstall { get; set; } = true;

    /// <summary>安装成功后删除下载的包文件（Penumbra 已经把内容拷进自己的目录）。</summary>
    public bool CleanupAfterInstall { get; set; } = true;

    /// <summary>并发下载数。默认 1：礼貌抓取，避免被站点限速/拉黑。</summary>
    public int MaxConcurrentDownloads { get; set; } = 1;

    // ---------------- 浏览 ----------------

    /// <summary>每页条数（站点是 15 条一页）。</summary>
    public int PageSize { get; set; } = 15;

    /// <summary>默认只看兼容当前版本的 Mod。</summary>
    public bool OnlyDtCompatible { get; set; } = true;

    /// <summary>列表里是否显示 NSFW 条目（NSFW 需要浏览器通道，见下）。</summary>
    public bool ShowNsfw { get; set; } = false;

    /// <summary>缩略图磁盘缓存上限（MB），超出按最久未用淘汰。</summary>
    public int ThumbCacheMb { get; set; } = 256;

    /// <summary>列表里显示的最近搜索关键词（最多 10 个）。</summary>
    public System.Collections.Generic.List<string> RecentSearches { get; set; } = [];

    // ---------------- 浏览器通道（NSFW 必需）----------------

    /// <summary>
    /// 启用浏览器通道：需要登录/被 Cloudflare 拦的内容（NSFW）交给本机浏览器去取。
    /// 原理：把已登录浏览器的 Cookies + Local State 复制到插件自己的临时 profile，
    /// 用同一个浏览器 exe 起一个 headless 实例，通过 CDP 取页面与文件。用户无需登录操作。
    /// </summary>
    public bool EnableBrowserChannel { get; set; } = true;

    /// <summary>浏览器 exe 路径（空 = 自动探测 Chrome / Edge / 其它）。</summary>
    public string BrowserExe { get; set; } = string.Empty;

    /// <summary>浏览器用户数据目录（空 = 按 exe 自动推导）。</summary>
    public string BrowserUserDataDir { get; set; } = string.Empty;

    /// <summary>
    /// 自动挑选「能读到登录态」的浏览器 profile（多浏览器时逐个试）。
    /// 关掉则只用上面指定的那一个。
    /// </summary>
    public bool AutoPickProfile { get; set; } = true;

    /// <summary>单个 Mod 走浏览器通道的超时（秒）。</summary>
    public int BrowserTimeoutSec { get; set; } = 180;

    /// <summary>用完是否删除临时 profile（里面含 cookie 副本，建议保持开启）。</summary>
    public bool CleanupBorrowedProfile { get; set; } = true;

    // ---------------- 行为细节 ----------------

    /// <summary>列表里双击条目 = 安装（关掉则双击只展开详情）。</summary>
    public bool DoubleClickInstalls { get; set; } = true;

    /// <summary>请求间隔（毫秒）：串行抓站时的最小间隔，礼貌值。</summary>
    public int RequestDelayMs { get; set; } = 350;

    /// <summary>下载完成/失败是否在聊天框提示。</summary>
    public bool NotifyInChat { get; set; } = true;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
