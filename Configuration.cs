using System;
using Dalamud.Configuration;
using XmaBrowser.Browser;
using XmaBrowser.Site;
using XmaBrowser.Translate;

namespace XmaBrowser;

[Serializable]
public class Configuration : IPluginConfiguration, IXmaSettings, IBrowserSettings, ITranslateSettings
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

    /// <summary>
    /// 旧字段，只用于迁移：以前是个 bool「显示 NSFW」，而它实际发的是 <c>nsfw=true</c>，
    /// 站点语义是**只看 NSFW**（所以主人会说「显示 NSFW 不是只显示 NSFW」）。见 <see cref="NsfwMode"/>。
    /// </summary>
    public bool ShowNsfw { get; set; }

    /// <summary>
    /// 内容过滤三态（站点语义实测得出）：0 = 排除 NSFW（默认） 1 = 混合（NSFW 与 SFW 都显示）
    /// 2 = 只看 NSFW。URL 映射：0 → <c>nsfw=false</c>，1 → **不传参数**，2 → <c>nsfw=true</c>。
    /// -1 表示「还没从旧的 ShowNsfw 迁移过」。
    /// </summary>
    public int NsfwMode { get; set; } = -1;

    /// <summary>缩略图磁盘缓存上限（MB），超出按最久未用淘汰。</summary>
    public int ThumbCacheMb { get; set; } = 256;

    /// <summary>浏览结果用宫格（卡片）展示；false = 列表。</summary>
    public bool GridView { get; set; }

    /// <summary>宫格卡片宽度（未缩放的像素宽；实际宽度会乘 UI 缩放）。</summary>
    public int GridCardWidth { get; set; } = 172;

    // ---------------- 翻译（英文 Mod 标题/描述 → 中文）----------------

    /// <summary>
    /// 翻译后端：
    /// <c>google</c> = 谷歌免费端点（免 Key，**默认**，质量最好、实测不限流；需要能访问谷歌 ——
    ///             .NET 默认走 Windows 系统代理，所以挂着代理的机器直接可用）；
    /// <c>youdao</c> = 有道公共接口（免 Key，国内直连可用，但限流很紧：约 1 次/2 秒）；
    /// <c>tencent</c> = 腾讯交互翻译（免 Key，好使但部分机器证书链在 Windows 下验证不过）；
    /// <c>openai</c> = 任意 OpenAI 兼容接口（DeepSeek 等，质量最好最快，需要填 Key）。
    /// </summary>
    public string TranslateBackend { get; set; } = "google";

    /// <summary>
    /// 翻译请求用的代理，形如 http://127.0.0.1:7877。**留空 = 用 Windows 系统代理**（.NET 默认行为）。
    /// 谷歌需要走到境外，本机没系统代理时才需要在这里手填。
    /// </summary>
    public string TranslateProxy { get; set; } = string.Empty;

    /// <summary>OpenAI 兼容接口的 BaseUrl（默认 DeepSeek）。</summary>
    public string TranslateBaseUrl { get; set; } = "https://api.deepseek.com/v1";

    /// <summary>OpenAI 兼容接口的 API Key。</summary>
    public string TranslateApiKey { get; set; } = string.Empty;

    /// <summary>OpenAI 兼容接口的模型名。</summary>
    public string TranslateModel { get; set; } = "deepseek-chat";

    /// <summary>打开详情时自动翻译标题 + 描述。</summary>
    public bool AutoTranslateDetail { get; set; } = true;

    /// <summary>列表/宫格里附上中文标题（**只用已有缓存**，不会每行都发请求）。</summary>
    public bool ShowTranslatedTitles { get; set; } = true;

    // ---------------- 装进文件夹（Penumbra 列表里就是折叠组）----------------

    /// <summary>
    /// 安装时把 Mod 归进文件夹。Penumbra 的 mod 列表是文件系统树，文件夹即折叠组。
    /// 实现：装完调 SetModPath(dir, name, 模板算出的路径)。
    /// </summary>
    public bool GroupIntoFolders { get; set; } = true;

    /// <summary>
    /// 分组路径模板。占位符：{prefix} {type} {author} {modid} {name}
    /// 例如 "{prefix}/{type}" → XMA/Gear/MyMod；留空 = 不分组。
    /// </summary>
    public string GroupTemplate { get; set; } = "{prefix}/{type}";

    /// <summary>{prefix} 的值（默认 XMA，便于认出「这是这个插件装进来的」）。</summary>
    public string GroupPrefix { get; set; } = "XMA";

    // ---------------- 封面（Penumbra 面板里画图 + 随包交付）----------------

    /// <summary>在 Penumbra 的 mod 面板里画自己装进去的封面。</summary>
    public bool DrawCovers { get; set; } = true;

    /// <summary>封面高度 = 面板宽度 × 这个比例（与 ModBridge / Heliosphere 的默认值一致）。</summary>
    public float CoverSize { get; set; } = 0.375f;

    /// <summary>
    /// 把封面塞进包再交给 Penumbra（最可靠：Penumbra 只接受包文件，包内没图 = 装出来没图）。
    /// 关掉则只在装完后往 mod 目录补写。
    /// </summary>
    public bool UseCoverInPackage { get; set; } = true;

    /// <summary>安装时自动下载封面（优先详情页大图，退回列表缩略图）。</summary>
    public bool FetchCoverOnInstall { get; set; } = true;

    /// <summary>列表里显示的最近搜索关键词（最多 10 个）。</summary>
    public System.Collections.Generic.List<string> RecentSearches { get; set; } = [];

    // ---------------- 浏览器通道（NSFW 必需）----------------

    /// <summary>
    /// 直连被站点拒绝时，自动改用浏览器通道。
    ///
    /// 关掉的用处：站点做人机校验（Just a moment）时，公开内容也会直连被拒 → 会一路落到浏览器通道，
    /// 想「只要直连、失败就报错」的人可以关掉它。
    /// </summary>
    public bool AutoFallbackToBrowser { get; set; } = true;

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

    /// <summary>给三态值做个夹取（越界/未迁移都当 0）。</summary>
    public int EffectiveNsfwMode => NsfwMode is 1 or 2 ? NsfwMode : 0;

    /// <summary>
    /// 老配置迁移：旧版「显示 NSFW」勾上时发的是 <c>nsfw=true</c>，站点语义是**只看 NSFW**；
    /// 但主人勾它的本意是「把 NSFW 也显示出来」→ 迁到**混合**（1）。只跑一次。
    /// </summary>
    public bool Migrate()
    {
        if (NsfwMode >= 0) return false;
        NsfwMode = ShowNsfw ? 1 : 0;
        ShowNsfw = false;
        return true;
    }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
