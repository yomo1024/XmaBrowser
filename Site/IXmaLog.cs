namespace XmaBrowser.Site;

/// <summary>
/// 站点层只用到日志，抽出来以后就能脱离 Dalamud 单独编译/测试
/// （测试项目直接 Include 源码，测的就是真东西）。
/// </summary>
public interface IXmaLog
{
    void Info(string message);
    void Warn(string message);
    void Debug(string message);
    void Error(string message);
}

public sealed class NullLog : IXmaLog
{
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Debug(string message) { }
    public void Error(string message) { }
}
