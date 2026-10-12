using Dalamud.Plugin.Services;

namespace XmaBrowser.Site;

/// <summary>把 Dalamud 的 IPluginLog 适配成站点层的 IXmaLog（只有这个文件依赖 Dalamud）。</summary>
public sealed class DalamudLogAdapter(IPluginLog log) : IXmaLog
{
    public void Info(string message) => log.Information("[XmaBrowser] " + message);
    public void Warn(string message) => log.Warning("[XmaBrowser] " + message);
    public void Debug(string message) => log.Debug("[XmaBrowser] " + message);
    public void Error(string message) => log.Error("[XmaBrowser] " + message);
}
