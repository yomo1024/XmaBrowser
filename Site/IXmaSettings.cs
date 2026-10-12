namespace XmaBrowser.Site;

/// <summary>
/// 站点层需要的设置项（从一个窄接口拿，避免站点层依赖 Dalamud 的 Configuration）。
/// Configuration 会实现这个接口。
/// </summary>
public interface IXmaSettings
{
    /// <summary>串行抓站时的最小请求间隔（毫秒）。</summary>
    int RequestDelayMs { get; }
}
