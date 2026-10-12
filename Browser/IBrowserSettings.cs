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
}
