using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;

namespace XmaBrowser.Install;

/// <summary>
/// Penumbra 的 IPC 封装（照搬同机已编译通过的 ModBridge 做法：类型化订阅器 + 全部切 framework 线程）。
/// IPC 不是请求-应答式的：Penumbra 没装/没加载时会抛，这里统一吞掉并返回安全值。
/// </summary>
public sealed class PenumbraBridge : IDisposable
{
    private readonly IFramework _framework;
    private readonly IPluginLog _log;

    private readonly ApiVersion _apiVersion;
    private readonly GetModDirectory _getModDirectory;
    private readonly GetModList _getModList;
    private readonly GetCollection _getCollection;
    private readonly InstallMod _installMod;
    private readonly AddMod _addMod;
    private readonly ReloadMod _reloadMod;
    private readonly TrySetMod _trySetMod;
    private readonly TrySetModPriority _trySetModPriority;
    private readonly RedrawAll _redrawAll;
    private readonly OpenMainWindow _openMainWindow;

    private readonly IDisposable _initializedSub;
    private readonly IDisposable _disposedSub;

    public bool Available { get; private set; }
    public int ApiMajor { get; private set; }
    public int ApiMinor { get; private set; }
    public string ModDirectory { get; private set; } = string.Empty;

    public PenumbraBridge(IDalamudPluginInterface pi, IFramework framework, IPluginLog log)
    {
        _framework = framework;
        _log = log;

        _apiVersion = new ApiVersion(pi);
        _getModDirectory = new GetModDirectory(pi);
        _getModList = new GetModList(pi);
        _getCollection = new GetCollection(pi);
        _installMod = new InstallMod(pi);
        _addMod = new AddMod(pi);
        _reloadMod = new ReloadMod(pi);
        _trySetMod = new TrySetMod(pi);
        _trySetModPriority = new TrySetModPriority(pi);
        _redrawAll = new RedrawAll(pi);
        _openMainWindow = new OpenMainWindow(pi);

        _initializedSub = Penumbra.Api.IpcSubscribers.Initialized.Subscriber(pi, Refresh);
        _disposedSub = Penumbra.Api.IpcSubscribers.Disposed.Subscriber(pi, () => Available = false);
        Refresh();
    }

    public void Refresh() => RunOnFx(() =>
    {
        try
        {
            var (major, minor) = _apiVersion.Invoke();
            ApiMajor = major;
            ApiMinor = minor;
            Available = major > 0;
            ModDirectory = Available ? _getModDirectory.Invoke() : string.Empty;
            _log.Information($"[XmaBrowser] Penumbra API {major}.{minor}，mod 目录 {ModDirectory}");
        }
        catch (Exception e)
        {
            Available = false;
            ModDirectory = string.Empty;
            _log.Debug($"[XmaBrowser] Penumbra 暂不可用：{e.Message}");
        }
    });

    public Dictionary<string, string> Mods()
    {
        var result = new Dictionary<string, string>();
        RunOnFx(() =>
        {
            try
            {
                foreach (var kv in _getModList.Invoke()) result[kv.Key] = kv.Value;
            }
            catch (Exception e) { _log.Debug($"[XmaBrowser] GetModList 失败：{e.Message}"); }
        });
        return result;
    }

    /// <summary>当前角色实际生效的集合（「装完就启用」用它）。</summary>
    public (Guid Id, string Name)? YourCollection()
    {
        (Guid, string)? result = null;
        RunOnFx(() =>
        {
            try
            {
                var cur = _getCollection.Invoke(ApiCollectionType.Yourself);
                if (cur != null) result = (cur.Value.Id, cur.Value.Name);
            }
            catch (Exception e) { _log.Debug($"[XmaBrowser] GetCollection 失败：{e.Message}"); }
        });
        return result;
    }

    public string Install(string packagePath)
        => Call(nameof(Install), () => _installMod.Invoke(packagePath).ToString());

    public string Add(string modDirectoryName)
        => Call(nameof(Add), () => _addMod.Invoke(modDirectoryName).ToString());

    public string Reload(string modDirectory, string modName)
        => Call(nameof(Reload), () => _reloadMod.Invoke(modDirectory, modName).ToString());

    public string SetEnabled(Guid collectionId, string modDirectory, bool enabled, string modName)
        => Call(nameof(SetEnabled), () => _trySetMod.Invoke(collectionId, modDirectory, enabled, modName).ToString());

    public string SetPriority(Guid collectionId, string modDirectory, int priority, string modName)
        => Call(nameof(SetPriority), () => _trySetModPriority.Invoke(collectionId, modDirectory, priority, modName).ToString());

    public void Redraw() => Call(nameof(Redraw), () =>
    {
        _redrawAll.Invoke(RedrawType.Redraw);
        return "ok";
    });

    public string OpenModsTab(string modDirectory, string modName)
        => Call(nameof(OpenModsTab), () => _openMainWindow.Invoke(TabType.Mods, modDirectory, modName).ToString());

    private string Call(string what, Func<string> body)
    {
        if (!Available) return "PenumbraUnavailable";

        var result = "Exception";
        RunOnFx(() =>
        {
            try { result = body(); }
            catch (Exception e)
            {
                result = "Exception: " + e.Message;
                _log.Warning($"[XmaBrowser] Penumbra.{what} 调用失败：{e}");
            }
        });
        return result;
    }

    private void RunOnFx(Action action)
    {
        try { _framework.RunOnFrameworkThread(action); }
        catch (Exception e)
        {
            _log.Debug($"[XmaBrowser] RunOnFrameworkThread 失败：{e.Message}");
            try { action(); } catch { /* 忽略 */ }
        }
    }

    public void Dispose()
    {
        _initializedSub.Dispose();
        _disposedSub.Dispose();
    }
}
