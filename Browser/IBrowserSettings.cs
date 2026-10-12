namespace XmaBrowser.Browser;

/// <summary>
/// 浏览器通道需要的设置（窄接口，让通道能脱离 Dalamud 单独编译与测试）。
/// Configuration 会实现它。
/// </summary>
public interface IBrowserSettings
{
    bool EnableBrowserChannel { get; }
    string BrowserExe { get; }
    string BrowserUserDataDir { get; }
    bool AutoPickProfile { get; }
    int BrowserTimeoutSec { get; }
    bool CleanupBorrowedProfile { get; }

    /// <summary>插件自带的浏览器 profile 目录（空 = 默认位置）。登录态就存在这里，长期复用。</summary>
    string ChannelProfileDir { get; }

    /// <summary>通道空闲多少分钟后关掉 headless 浏览器释放内存（0 = 一直留着）。</summary>
    int ChannelIdleMinutes { get; }
}
