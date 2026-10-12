namespace XmaBrowser.Site;

/// <summary>
/// 取数的**时间上限**（纯常量，可离线自测）。
///
/// 为什么要把它们集中起来、而且每一个都必须有值：
/// 主人报过「点进详情卡了一分钟才出来，而切换一下秒开」。查下来核心缺陷不是某一步慢，
/// 而是**整条链路没有任何总上限**，好几个地方各自能等很久，再相乘：
///   · 通道启动浏览器：预算写的是 BrowserTimeoutSec（默认 180 秒）
///   · CDP 单次调用：默认 60 秒（Page.enable / Page.navigate / 取 HTML 都用默认值）
///   · 取页面失败重试 2 次 × 会话重建 1 次 → 这些"上限"叠起来轻松上一分钟
///   · 通道的操作锁是**无限等**的 —— 前面一个卡住，后面全排队
/// 于是用户就盯着一个不动的进度条。缓存命中之所以"秒开"，正是因为绕开了这一整条。
///
/// 原则：**任何一步都不许无限等；每一层都要有上限；超了就给出原因，让人能重试。**
/// </summary>
public static class FetchLimits
{
    // ---------------- CDP 单次调用 ----------------

    /// <summary>CDP 调用默认上限（原来是 60 秒 —— 一个卡住的调用就够用户等到放弃）。</summary>
    public const int CdpCallMs = 15000;

    /// <summary>Page.enable：只是打开一个域，正常几十毫秒。</summary>
    public const int PageEnableMs = 5000;

    /// <summary>Page.navigate：发出导航并等它受理。</summary>
    public const int PageNavigateMs = 10000;

    /// <summary>轮询"页面好了没"的单次求值。</summary>
    public const int EvalProbeMs = 5000;

    /// <summary>把页面 HTML 取回来（大页面要序列化，给宽一点）。</summary>
    public const int GrabHtmlMs = 15000;

    // ---------------- 通道会话 ----------------

    /// <summary>起浏览器 + 连 CDP 的总预算（通道取数用；登录流程另有放宽，因为要等人操作）。</summary>
    public const int SessionStartBudgetMs = 25000;

    /// <summary>等 CDP 调试端点的总时长（进程起来了但端口一直不响应时，别无限等）。</summary>
    public const int WaitForCdpTotalMs = 20000;

    /// <summary>等 CDP 端点时单次探测的超时 —— 原来是 3 秒，端口不通时白等太久。</summary>
    public const int WaitForCdpProbeMs = 1000;

    /// <summary>等通道操作锁的上限。锁是串行的，前面一个卡住不能让后面无限排队。</summary>
    public const int OpLockWaitMs = 30000;

    // ---------------- 用户可见的整条操作 ----------------

    /// <summary>读详情整条链路的上限（秒）。到点就放弃并写明原因，绝不让人等一分钟。</summary>
    public const int DetailTimeoutSeconds = 25;

    /// <summary>搜索整条链路的上限（秒）。</summary>
    public const int SearchTimeoutSeconds = 30;

    /// <summary>取一张图整条链路的上限（毫秒）。
    /// 给得比较宽，因为**进度条是可见的、而且有停滞检测兜底** —— 慢不等于坏。
    /// 真正该斩的是"卡住不动"，不是"在慢慢下"。</summary>
    public const int ImageJobMs = 90000;

    /// <summary>
    /// 取图时"**停滞**"的上限（毫秒）：多久没有新字节就判定卡住。
    ///
    /// ⚠ 这里踩过一个坑：一开始写成"直连总时长 6 秒"。实测站点的一张整图要 **9127 ms** 才下完，
    /// 于是 6 秒上限会把一个本来会成功的下载掐掉，再退回浏览器通道 —— 反而更慢。
    /// 正确语义是"**没有进展**才放弃"：每读到数据就把这个计时重置。
    /// </summary>
    public const int ImageStallMs = 6000;
}
