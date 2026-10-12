using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;

namespace XmaBrowser.Install;

/// <summary>
/// Penumbra 的 IPC 封装（全部调用切 framework 线程）。
///
/// ⚠️ 关键教训（2026-10-12 实机定位，别再踩）：
/// Dalamud 的 <c>IFramework.RunOnFrameworkThread</c> 返回 **Task**：
///   · 已在 framework 线程 → 内联执行，返回已完成的 Task
///   · **在后台线程 → 只是把动作排队，Task 完成前方法就返回了**
/// 所以「调完就立刻读结果变量」在后台线程上读到的是**占位初值**
/// （表现为：GetModList 空、GetModPath 空路径、SetModPath 只报一个没有信息的 Exception）。
/// 官方文档明确：需要 `.Wait()` / `.Result` 时就用这个方法 —— 因此这里**统一等 Task 完成**再取值。
/// （ModBridge 没暴露此问题，只是因为它从不看返回值。）
/// </summary>
public sealed class PenumbraBridge : IDisposable, Cover.CoverContext
{
    private static readonly TimeSpan IpcTimeout = TimeSpan.FromSeconds(15);

    private readonly IFramework _framework;
    private readonly IPluginLog _log;

    private readonly ApiVersion _apiVersion;
    private readonly GetModDirectory _getModDirectory;
    private readonly GetModList _getModList;
    private readonly GetCollection _getCollection;
    private readonly InstallMod _installMod;
    private readonly AddMod _addMod;
    private readonly SetModPath _setModPath;
    private readonly GetModPath _getModPath;
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
        _setModPath = new SetModPath(pi);
        _getModPath = new GetModPath(pi);
        _reloadMod = new ReloadMod(pi);
        _trySetMod = new TrySetMod(pi);
        _trySetModPriority = new TrySetModPriority(pi);
        _redrawAll = new RedrawAll(pi);
        _openMainWindow = new OpenMainWindow(pi);

        _initializedSub = Penumbra.Api.IpcSubscribers.Initialized.Subscriber(pi, Refresh);
        _disposedSub = Penumbra.Api.IpcSubscribers.Disposed.Subscriber(pi, () => Available = false);
        Refresh();
    }

    // ---------------------------------------------------------------- 核心：等 Task 完成再取值

    /// <summary>
    /// 在 framework 线程上执行并**等它真的跑完**再返回结果。
    /// 后台线程调用时这里会阻塞到 framework 线程执行完毕（框架线程不会反过来等我们，所以不会死锁）。
    /// </summary>
    private T OnFx<T>(string what, Func<T> body, T fallback, bool requireAvailable = true)
    {
        if (requireAvailable && !Available) return fallback;

        try
        {
            var task = _framework.RunOnFrameworkThread(() =>
            {
                try
                {
                    return body();
                }
                catch (Exception e)
                {
                    _log.Warning($"[XmaBrowser] Penumbra.{what} 调用出错：{e.GetType().Name} {e.Message}");
                    return fallback;
                }
            });

            if (!task.Wait(IpcTimeout))
            {
                _log.Warning($"[XmaBrowser] Penumbra.{what} 超时（{IpcTimeout.TotalSeconds:0} 秒未完成）");
                return fallback;
            }

            return task.Result;
        }
        catch (AggregateException ae)
        {
            _log.Warning($"[XmaBrowser] Penumbra.{what} 失败：{ae.InnerException?.Message ?? ae.Message}");
            return fallback;
        }
        catch (Exception e)
        {
            _log.Warning($"[XmaBrowser] Penumbra.{what} 失败：{e.GetType().Name} {e.Message}");
            return fallback;
        }
    }

    private void OnFx(string what, Action body, bool requireAvailable = true)
        => OnFx<object?>(what, () => { body(); return null; }, null, requireAvailable);

    // ---------------------------------------------------------------- 状态

    public void Refresh() => OnFx("Refresh", () =>
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
    }, false);

    /// <summary>当前所有 Mod：key = 目录名（Penumbra 的 ModPath.Name），value = 显示名。</summary>
    public Dictionary<string, string> Mods()
        => OnFx("GetModList", () =>
        {
            var result = new Dictionary<string, string>();
            foreach (var kv in _getModList.Invoke())
                result[kv.Key] = kv.Value;
            return result;
        }, new Dictionary<string, string>());

    /// <summary>当前角色实际生效的集合（「装完就启用」用它）。</summary>
    public (Guid Id, string Name)? YourCollection()
        => OnFx<(Guid, string)?>("GetCollection", () =>
        {
            var cur = _getCollection.Invoke(ApiCollectionType.Yourself);
            return cur == null ? null : (cur.Value.Id, cur.Value.Name);
        }, null);

    // ---------------------------------------------------------------- 安装 / 路径

    public string Install(string packagePath)
        => OnFx("InstallMod", () => _installMod.Invoke(packagePath).ToString(), "Exception");

    /// <summary>只接受 mod 根目录下的名字（Penumbra 内部会 Path.GetFileName + 校验父目录），所以别传子路径。</summary>
    public string Add(string modDirectoryName)
        => OnFx("AddMod", () => _addMod.Invoke(modDirectoryName).ToString(), "Exception");

    /// <summary>改 mod 的路径（可带文件夹）—— 内部是 Penumbra 的 RenameAndMove，折叠组靠它。</summary>
    public string SetPath(string modDirectory, string modName, string newPath)
        => OnFx("SetModPath", () => _setModPath.Invoke(modDirectory, modName, newPath).ToString(), "Exception");

    /// <summary>读回 Mod 的**真实**路径（含文件夹）。改完必须读回来核对。</summary>
    public (string Ec, string Path, bool CustomPath, bool CustomName) GetPath(string modDirectory, string modName)
        => OnFx<(string, string, bool, bool)>("GetModPath", () =>
        {
            var (result, full, isPathCustom, isNameCustom) = _getModPath.Invoke(modDirectory, modName);
            return (result.ToString(), full ?? string.Empty, isPathCustom, isNameCustom);
        }, ("Exception", string.Empty, false, false));

    public string Reload(string modDirectory, string modName)
        => OnFx("ReloadMod", () => _reloadMod.Invoke(modDirectory, modName).ToString(), "Exception");

    public string SetEnabled(Guid collectionId, string modDirectory, bool enabled, string modName)
        => OnFx("TrySetMod", () => _trySetMod.Invoke(collectionId, modDirectory, enabled, modName).ToString(), "Exception");

    public string SetPriority(Guid collectionId, string modDirectory, int priority, string modName)
        => OnFx("TrySetModPriority",
                () => _trySetModPriority.Invoke(collectionId, modDirectory, priority, modName).ToString(), "Exception");

    public void Redraw() => OnFx("RedrawAll", () => { _redrawAll.Invoke(RedrawType.Redraw); return 0; }, 0);

    public string OpenModsTab(string modDirectory, string modName)
        => OnFx("OpenMainWindow",
                () => _openMainWindow.Invoke(TabType.Mods, modDirectory, modName).ToString(), "Exception");

    // ---------------------------------------------------------------- 诊断辅助

    /// <summary>把当前所有 Mod 的路径读出来（诊断：一眼看到 Penumbra 认为它们在哪）。</summary>
    public List<string> DumpPaths()
    {
        var lines = new List<string>();
        foreach (var kv in Mods())
        {
            var (ec, path, customPath, customName) = GetPath(kv.Key, kv.Value);
            lines.Add($"{kv.Key}  →  {kv.Value}  | 路径={path}  | ec={ec}  | 自定义路径={customPath} 自定义名={customName}");
        }
        return lines;
    }

    /// <summary>当前 mod 列表的紧凑快照（dir=name; …），用于对比「移动前后标识是否变了」。</summary>
    public string SnapshotList()
        => string.Join("; ", Mods().Select(kv => $"{kv.Key}={kv.Value}"));

    public void Dispose()
    {
        _initializedSub.Dispose();
        _disposedSub.Dispose();
    }
}
