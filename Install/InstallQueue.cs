using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Penumbra.Api.Enums;
using XmaBrowser.Browser;
using XmaBrowser.Cover;
using XmaBrowser.Site;

namespace XmaBrowser.Install;

public enum JobState
{
    Queued,
    Downloading,
    Extracting,
    Installing,
    Enabling,
    Done,
    Error,
    Cancelled,
}

public sealed class InstallJob
{
    public string Id { get; } = Guid.NewGuid().ToString("n")[..8];
    public string ModId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    /// <summary>封面图地址（详情页大图优先，退回列表缩略图）。</summary>
    public string CoverUrl { get; set; } = string.Empty;
    /// <summary>封面本地路径（取到时填）。</summary>
    public string CoverPath { get; set; } = string.Empty;

    public JobState State { get; set; } = JobState.Queued;
    public int Percent { get; set; }
    public long Bytes { get; set; }
    public long Total { get; set; }
    public string Message { get; set; } = "排队中…";
    public string Error { get; set; } = string.Empty;
    /// <summary>真实下载用的是浏览器通道（NSFW 才会走到）。</summary>
    public bool UsedBrowserChannel { get; set; }
    /// <summary>走浏览器通道的原因（站点人机校验 / 需要登录态 / 直链需浏览器）。</summary>
    public string BrowserReason { get; set; } = string.Empty;
    /// <summary>站点上的类型（Gear / Hair / Skin…），用于装进文件夹的分组。</summary>
    public string SiteType { get; init; } = string.Empty;
    /// <summary>
    /// 自动归组不可用（本机 Penumbra 的 SetModPath 是空实现）时，给用户的**目标虚拟路径**：
    /// 在 Penumbra 里右键该 Mod → Move Mod → 粘贴这个路径 → 回车，就分组好了。
    /// </summary>
    public string GroupTargetPath { get; set; } = string.Empty;

    /// <summary>这条 Mod 在 Penumbra 里的目录名（聚焦窗口/移动时要用真实目录名）。</summary>
    public string ModDirName { get; set; } = string.Empty;

    public DateTime StartedUtc { get; } = DateTime.UtcNow;

    public bool Finished => State is JobState.Done or JobState.Error or JobState.Cancelled;
}

/// <summary>
/// 安装队列：单线程串行（礼貌抓站 + 一次只装一个），下载 → 解析 → 交给 Penumbra → 启用 → 重绘。
/// NSFW / 被 CF 拦的内容自动改走浏览器通道。
/// </summary>
public sealed class InstallQueue : IDisposable
{
    private readonly XmaClient _site;
    private readonly PenumbraBridge _penumbra;
    private readonly Configuration _cfg;
    private readonly BrowserChannel _channel;
    private readonly CoverFetcher _covers;
    private readonly IXmaLog _log;
    private readonly string _workRoot;

    private readonly List<InstallJob> _jobs = [];
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;

    /// <summary>给界面看/复制的诊断日志（最近若干条）。主人找不到 dalamud.log，所以自己留一份。</summary>
    private readonly List<string> _diagLog = [];
    private const int MaxDiagLog = 80;

    /// <summary>播报关键结果（插件里接到游戏聊天框）。</summary>
    private readonly Action<string>? _notify;

    /// <summary>
    /// 本机 Penumbra 的 `SetModPath` 是否「假成功」（返回 Success 但路径不变）。
    ///
    /// 实机证据（2026-10-12，8 种写法全试）：连最简单的「只改名」都是
    /// `SetModPath(...) → Success` 而读回路径/自定义名标志完全不变 → 该实现是空壳。
    /// 识别到之后**不再做无效重试**，只给用户明确指引。
    /// </summary>
    public bool SetModPathNoOp { get; private set; }

    public IReadOnlyList<string> DiagLog
    {
        get { lock (_diagLog) return _diagLog.ToArray(); }
    }

    private void LogDiag(string line)
    {
        lock (_diagLog)
        {
            _diagLog.Add($"{DateTime.Now:HH:mm:ss}  {line}");
            if (_diagLog.Count > MaxDiagLog) _diagLog.RemoveAt(0);
        }
    }

    /// <summary>识别到「SetModPath 假成功」后调用一次：停用自动归组并给出指引。</summary>
    private void FlagSetModPathNoOp(string why)
    {
        if (SetModPathNoOp) return;
        SetModPathNoOp = true;

        _log.Warn($"[XmaBrowser] 检测到本机 Penumbra 的 SetModPath 不生效（返回 Success 但路径不变）：{why}");
        LogDiag("⚠ 检测到 SetModPath 假成功（返回 Success 但路径不变）→ 已停用自动归组");
        _notify?.Invoke("[XMA Browser] 检测到你这版 Penumbra 的 SetModPath 不生效（返回成功但不动路径），" +
                        "已停用自动归组。想分组可用 Penumbra 自带的「默认导入文件夹」：" +
                        "在 Penumbra 的 mod 列表里右键任意文件夹 → Set Folder as... → Set Default Import Folder。");
    }

    public event Action? Changed;

    public InstallQueue(XmaClient site, PenumbraBridge penumbra, Configuration cfg, BrowserChannel channel,
                        CoverFetcher covers, IXmaLog log, string workRoot, Action<string>? notify = null)
    {
        _site = site;
        _penumbra = penumbra;
        _cfg = cfg;
        _channel = channel;
        _covers = covers;
        _log = log;
        _workRoot = workRoot;
        _notify = notify;
        Directory.CreateDirectory(_workRoot);
        _worker = Task.Run(LoopAsync);
    }

    public IReadOnlyList<InstallJob> Snapshot()
    {
        lock (_jobs) return _jobs.ToArray();
    }

    public InstallJob Enqueue(ModDetail detail, string coverUrl = "")
    {
        var job = new InstallJob
        {
            ModId = detail.ModId,
            Title = detail.Title,
            Author = detail.Author,
            Url = detail.AbsoluteDownloadUrl,
            CoverUrl = coverUrl.Length > 0 ? coverUrl : (detail.Images.Count > 0 ? detail.Images[0] : string.Empty),
            SiteType = detail.Type,
            FileName = PackageResolver.SafeModDirName(detail.FileNameHint, $"mod-{detail.ModId}"),
            Message = "排队中…",
        };
        lock (_jobs) _jobs.Add(job);
        _wake.Release();
        Changed?.Invoke();
        _log.Info($"入队安装：{job.Title}（{job.ModId}）");
        return job;
    }

    public void Cancel(string id)
    {
        lock (_jobs)
        {
            var job = _jobs.FirstOrDefault(j => j.Id == id);
            if (job is null || job.Finished) return;
            if (job.State == JobState.Queued)
            {
                job.State = JobState.Cancelled;
                job.Message = "已取消";
            }
        }
        Changed?.Invoke();
    }

    public void ClearFinished()
    {
        lock (_jobs) _jobs.RemoveAll(j => j.Finished);
        Changed?.Invoke();
    }

    private async Task LoopAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            InstallJob? next;
            lock (_jobs) next = _jobs.FirstOrDefault(j => j.State == JobState.Queued);

            if (next is null)
            {
                try { await _wake.WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            try
            {
                await ProcessAsync(next, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                next.State = JobState.Cancelled;
                next.Message = "已取消";
            }
            catch (Exception e)
            {
                next.State = JobState.Error;
                next.Error = $"{e.GetType().Name}: {e.Message}";
                next.Message = "失败";
                _log.Warn($"安装 {next.Title} 失败：{e}");
                LogDiag($"{next.Title} 失败：{next.Error}");
                _notify?.Invoke($"[XMA Browser] 安装失败：{next.Title} — {next.Error}");
            }
            finally
            {
                Changed?.Invoke();
            }
        }
    }

    private async Task ProcessAsync(InstallJob job, CancellationToken ct)
    {
        if (!_penumbra.Available)
            throw new InvalidOperationException("Penumbra 不可用（没装或没加载）。先在 Dalamud 里装好 Penumbra。");

        var dest = Path.Combine(_workRoot, job.FileName);
        if (!Path.HasExtension(dest)) dest += ".pmp";

        // ---------- 1) 下载 ----------
        job.State = JobState.Downloading;
        job.Message = "下载中…";
        Changed?.Invoke();

        void Progress(long got, long total)
        {
            job.Bytes = got;
            job.Total = total;
            job.Percent = total > 0 ? (int)Math.Min(99, got * 100 / total) : 0;
            job.Message = total > 0
                ? $"下载中… {job.Percent}%（{Mb(got)} / {Mb(total)}）"
                : $"下载中… {Mb(got)}";
            Changed?.Invoke();
        }

        try
        {
            var r = await _site.DownloadAsync(job.Url, dest, Progress, ct).ConfigureAwait(false);
            job.Bytes = r.Bytes;
        }
        catch (XmaAccessException e)
        {
            if (!_cfg.AutoFallbackToBrowser)
                throw new InvalidOperationException(
                    e.Message + "（设置里关掉了「直连被拒时自动走浏览器通道」，所以没有降级）");

            if (!_channel.Enabled)
                throw new InvalidOperationException(e.Message + "（可在设置里开启浏览器通道）");

            job.UsedBrowserChannel = true;
            job.BrowserReason = XmaBlockClassifier.ShortLabel(e.Reason);
            job.Message = $"走浏览器通道下载（{job.BrowserReason}）…";
            _log.Info($"直连被拒 → 改用浏览器通道 [{job.BrowserReason}]：{e.Message}");
            LogDiag($"直连被拒 → 浏览器通道（{job.BrowserReason}）");
            Changed?.Invoke();
            var ok = await _channel.DownloadAsync(job.Url, dest, Progress, ct).ConfigureAwait(false);
            if (!ok)
                throw new InvalidOperationException($"浏览器通道也拿不到这个文件（{job.BrowserReason}）：{_channel.LastError}");
        }

        if (!File.Exists(dest) || new FileInfo(dest).Length == 0)
            throw new IOException("下载下来的文件是空的");

        // ---------- 1.5) 取封面（要随包一起交给 Penumbra，所以必须在安装之前） ----------
        if (_cfg.FetchCoverOnInstall && job.CoverUrl.Length > 0)
        {
            job.Message = "取封面…";
            Changed?.Invoke();
            try
            {
                var cover = await _covers.EnsureAsync(job.CoverUrl, job.ModId, ct).ConfigureAwait(false);
                if (cover is not null) job.CoverPath = cover;
            }
            catch (Exception e)
            {
                _log.Warn($"封面获取失败（不影响安装）：{e.Message}");
            }
        }

        // ---------- 2) 解析成 Penumbra 能装的东西 ----------
        job.State = JobState.Extracting;
        job.Message = "解析包…";
        job.Percent = 100;
        Changed?.Invoke();

        var workDir = Path.Combine(_workRoot, "extract", job.Id);
        Directory.CreateDirectory(workDir);

        // 结构探测：容器里可能有多个包、可能一个包都没有、也可能根本不是 Mod（见 PackageResolver.BuildPlan）
        var plan = PackageResolver.BuildPlan(dest, workDir);
        if (!plan.Ok)
            throw new NotSupportedException(plan.Problem ?? "这个压缩包里没有可安装的内容");

        job.Message = plan.Summary;
        Changed?.Invoke();
        _log.Info($"{job.Title}：{plan.Summary}");
        LogDiag($"{job.Title}：{plan.Summary}");

        var packages = plan.Packages;

        // ---------- 3) 装进 Penumbra ----------
        job.State = JobState.Installing;
        job.Message = packages.Count > 0 ? $"安装 {packages.Count} 个包…" : "登记 Mod 目录…";
        Changed?.Invoke();

        var before = _penumbra.Mods();

        // 3a) 松散结构：每个目录单独作为一条 Mod（放到 Penumbra 的 mod 根目录下再 AddMod）
        var addedNames = new List<string>();
        if (plan.LooseMods.Count > 0)
        {
            var modRoot = _penumbra.ModDirectory;
            if (string.IsNullOrEmpty(modRoot))
                throw new InvalidOperationException("拿不到 Penumbra 的 mod 目录，无法登记没有内层包的压缩包");

            Directory.CreateDirectory(modRoot);
            foreach (var lm in plan.LooseMods)
            {
                var target = UniqueDirIn(modRoot, lm.SafeName);
                MoveDirectory(lm.ExtractedDir, target);
                var name = Path.GetFileName(target);
                _penumbra.Add(name);
                addedNames.Add(name);
                _log.Info($"登记 Mod 目录：{name}（{lm.FileCount} 个文件）");
            }
        }

        // 3b) 包：交给 InstallMod（封面先塞进包）
        var covered = 0;
        foreach (var p in packages)
        {
            var toInstall = p;

            // 封面塞进包再交给 Penumbra —— 这是让图真正落进 mod 目录的可靠做法
            if (_cfg.UseCoverInPackage && job.CoverPath.Length > 0 && File.Exists(job.CoverPath))
            {
                var injected = CoverInjector.InjectIntoPackage(p, job.CoverPath, _workRoot);
                if (injected is not null)
                {
                    toInstall = injected;
                    covered++;
                }
            }

            _penumbra.Install(toInstall);
        }

        // 等 Penumbra 把新 mod 登记进来（ModAdded 是异步的）
        var newDirs = new List<string>();
        for (var i = 0; i < 40; i++)
        {
            ct.ThrowIfCancellationRequested();
            var now = _penumbra.Mods();
            newDirs = now.Keys.Where(k => !before.ContainsKey(k)).ToList();
            if (newDirs.Count > 0) break;
            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        if (newDirs.Count == 0)
            _log.Warn($"装完后没等出新 mod（{job.Title}）；Penumbra 可能仍在处理");

        LogDiag($"装完：新目录 {newDirs.Count} 个{(newDirs.Count > 0 ? "（" + string.Join(",", newDirs) + "）" : "")}，登记目录 {addedNames.Count} 个");

        // 兜底：没等出新目录时（重装同一个 Mod、或 Penumbra 处理较慢），按显示名把已存在的那个找回来 ——
        // 否则归组会被整个跳过（实测：主人装了却没分组，就是这条路径断了）
        if (newDirs.Count == 0 && (packages.Count > 0 || addedNames.Count > 0) && job.Title.Length > 0)
        {
            if (TryResolveMod(job.Title, job.Title, out var foundDir, out var foundName))
            {
                newDirs.Add(foundDir);
                _log.Info($"按显示名兜底找到了已装好的 Mod：{foundDir} → {foundName}");
                LogDiag($"按显示名兜底找到：{foundDir}");
            }
            else
            {
                _log.Warn($"列表里还没有这条 Mod（{job.Title}）—— Penumbra 可能仍在导入");
                LogDiag($"列表里还没找到这条 Mod，归组推迟处理");
            }
        }

        // 封面兜底：包注入没成功（或包不是 zip）时，往 mod 目录补写 cover.<ext> / images/_MetaImage.<ext> / meta.json
        if (job.CoverPath.Length > 0 && (newDirs.Count > 0 || addedNames.Count > 0))
        {
            var modRoot = _penumbra.ModDirectory;
            if (!string.IsNullOrEmpty(modRoot))
            {
                var applied = 0;
                foreach (var dir in newDirs.Concat(addedNames).Distinct(StringComparer.OrdinalIgnoreCase))
                    if (CoverInjector.Apply(Path.Combine(modRoot, dir), job.CoverPath)) applied++;
                if (applied > 0)
                    _log.Info($"已为 {applied} 个 mod 目录补写封面");
            }
        }
        if (covered > 0)
            _log.Info($"封面已随包注入 {covered} 个包");

        // ---------- 4) 启用 + 重绘 ----------
        if (_cfg.EnableOnInstall && newDirs.Count > 0)
        {
            job.State = JobState.Enabling;
            job.Message = "在当前集合里启用…";
            Changed?.Invoke();

            var mods = _penumbra.Mods();
            var okCount = 0;
            var total = 0;

            foreach (var dir in newDirs.Concat(addedNames).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                total++;
                var name = mods.TryGetValue(dir, out var n) ? n : dir;
                var (ok, line) = TryEnable(dir, name);
                if (ok) okCount++;
                LogDiag("启用自检：" + line);
            }

            job.Message = okCount > 0 && okCount == total
                ? $"已装好并在集合里启用（{okCount} 项，已读回确认）"
                : okCount > 0
                    ? $"装好 {total} 项，其中 {okCount} 项确认启用（其余见「关于」页诊断日志）"
                    : "装好了，但自动启用没成功（原因见「关于」页诊断日志）";
        }
        else if (newDirs.Count > 0)
        {
            job.Message = "已装好（按设置未自动启用）";
        }

        if (_cfg.RedrawOnInstall)
        {
            _penumbra.Redraw();
            job.Message += "，已重绘";
        }

        // ---------- 4.5) 归入文件夹（Penumbra 的列表里就是可折叠的组）----------
        if (_cfg.GroupIntoFolders)
        {
            var folder = GroupPath.BuildFolder(_cfg.GroupTemplate, _cfg.GroupPrefix, job.SiteType,
                                               job.Author, job.ModId, job.Title);

            if (SetModPathNoOp)
            {
                // 本机 Penumbra 的 SetModPath 是空实现 → 不重试，改为给用户备好「目标虚拟路径」
                var target = newDirs.Concat(addedNames).FirstOrDefault() is { Length: > 0 } d
                    ? GroupPath.BuildFullPath(folder, d)
                    : string.Empty;
                if (target.Length > 0)
                {
                    job.GroupTargetPath = target;
                    job.ModDirName = newDirs.Concat(addedNames).First();
                    job.Message += $"（自动归组不可用：在 Penumbra 里对它右键 → Move Mod，粘贴 {target}）";
                    LogDiag($"自动归组不可用，已备好目标路径：{target}");
                }
            }
            else
            {
                var affected = newDirs.Concat(addedNames).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (folder.Length > 0 && affected.Count > 0)
                    GroupInstalled(job, folder, affected);
            }
        }

        // ---------- 5) 收尾 ----------
        if (_cfg.CleanupAfterInstall)
        {
            try { if (File.Exists(dest)) File.Delete(dest); } catch { /* 忽略 */ }
        }

        try
        {
            if (Directory.Exists(workDir)) Directory.Delete(workDir, true);
        }
        catch { /* 忽略 */ }

        job.State = JobState.Done;
        job.Percent = 100;
        _log.Info($"安装完成：{job.Title} → {string.Join(", ", newDirs)}");
    }

    private static string Mb(long n) => n > 1048576 ? (n / 1048576.0).ToString("0.0") + "MB"
                                                     : Math.Max(1, n / 1024) + "KB";

    /// <summary>
    /// 在集合里启用一条 Mod，并**读回**确认是否真的启用了 —— 只看 Set 的返回值会骗人
    /// （TrySetMod 会回 NothingChanged / ModMissing / CollectionMissing，以前没检查就报成功）。
    ///
    /// 依次试两个集合：<c>Yourself</c>（管你自己角色，真正影响游戏）→ <c>Current</c>（Penumbra 界面当前选中的那个）。
    /// 返回 (是否确认启用, 给人看的说明)。
    /// </summary>
    private (bool Ok, string Line) TryEnable(string dir, string name)
    {
        var notes = new List<string>();
        var seen = new List<Guid>();

        foreach (var type in new[] { ApiCollectionType.Yourself, ApiCollectionType.Current })
        {
            var col = _penumbra.Collection(type);
            if (col is null)
            {
                notes.Add($"{type}：拿不到集合");
                continue;
            }

            if (seen.Contains(col.Value.Id)) continue;   // 两个类型指向同一个集合时别重复试
            seen.Add(col.Value.Id);

            var ec = _penumbra.SetEnabled(col.Value.Id, dir, true, name);
            var (recEc, enabled, priority) = _penumbra.GetEnabled(col.Value.Id, dir, name);
            var state = enabled == true ? "已启用" : enabled == false ? "未启用" : "读不到";

            notes.Add($"{type}「{col.Value.Name}」Set={ec} 读回={recEc}/{state}(优先级 {priority})");

            if (enabled == true)
                return (true, $"✓ {type}「{col.Value.Name}」：{state}（SetMod={ec}）");
        }

        return (false, $"✗ {dir}：" + string.Join("；", notes));
    }

    private void GroupInstalled(InstallJob job, string folder, List<string> dirs)
    {
        var ok = 0;
        var done = 0;
        foreach (var dir in dirs)
        {
            var (success, message) = GroupOne(dir, job.Title, folder);
            if (success) ok++;
            done++;
            _log.Info($"归组【{job.Title}】：{(success ? "成功" : "失败")} — {message}");
            LogDiag($"归组【{job.Title}】{(success ? "成功" : "失败")}：{message}");
            _notify?.Invoke($"[XMA Browser] 归组{(success ? "成功" : "失败")}：{job.Title} → {folder}/（{message}）");

            // 移完回头看两次：Penumbra 的导入/重扫可能在之后按旧路径把节点放回去
            ScheduleRecheck(dir, job.Title, folder, 5_000);
            ScheduleRecheck(dir, job.Title, folder, 15_000);
        }

        if (ok > 0)
            job.Message += $"，已归入 {folder}/";
        else if (done > 0)
            job.Message += $"（归入 {folder}/ 失败，仍在根目录，详见日志）";
    }

    /// <summary>
    /// 把一个 Mod 移进目标文件夹，并**读回真实路径核对**（不依赖我对 Penumbra 内部实现的猜测）。
    /// 返回 (是否成功, 人话说明)。供安装流程与界面上的手动归组共用。
    /// </summary>
    public (bool Success, string Message) GroupOne(string dirOrName, string displayName, string folder)
    {
        var (ok, msg, report) = GroupOneDetailed(dirOrName, displayName, folder);
        _log.Info("[归组] " + report.Replace("\n", " ⏎ "));
        return (ok, msg);
    }

    /// <summary>带完整自检报告的版本（报告可复制给开发者定位）。</summary>
    public (bool Success, string Message, string Report) GroupOneDetailed(string dirOrName, string displayName,
                                                                          string folder)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Mod：dir={dirOrName} | name={displayName}");
        sb.AppendLine($"目标文件夹：{(folder.Length > 0 ? folder : "(空)")}");

        if (string.IsNullOrWhiteSpace(folder))
        {
            sb.AppendLine("结果：目标文件夹为空（模板没算出路径）");
            return (false, "目标文件夹为空（模板没算出路径）", sb.ToString());
        }

        var modRoot = _penumbra.ModDirectory;
        sb.AppendLine($"Penumbra mod 根目录：{modRoot}");
        if (string.IsNullOrEmpty(modRoot))
            return (false, "拿不到 Penumbra 的 mod 目录", sb.ToString());

        // 这里曾用 Directory.CreateDirectory(modRoot/folder) 预建物理文件夹 —— v0.3.9 已删除。
        // 原因：Penumbra 的文件夹是纯虚拟的（MoveChild 只改内存树、ScanMods 只扫 mod 根顶层），
        // 在磁盘上建 XMA/Gear 这种空目录不但没用，还会被扫成一条空 mod、并在磁盘留下垃圾。
        // 归组一律只走 IPC SetModPath —— 绝不碰真实文件路径。

        // 动路径之前先确认 IPC 参数顺序没传反（探针无副作用）。
        // 传反的表现是「永远回 Success 但什么都不发生」，极难查 —— 见 PenumbraBridge.SetPath 的注释。
        var argCheck = _penumbra.SelfTestSetPathArgOrder();
        sb.AppendLine("参数顺序自检：" + (argCheck.Ok ? "通过 ✓" : "未通过 ⚠"));
        if (!argCheck.Ok)
        {
            sb.AppendLine(argCheck.Report.TrimEnd());
            FlagSetModPathNoOp("SetModPath 参数顺序自检未通过");
            return (false, "SetModPath 参数顺序自检未通过，已放弃自动归组（请把自检报告发我）", sb.ToString());
        }

        var (curDir, curName) = ResolveMod(dirOrName, displayName);
        sb.AppendLine($"定位到：dir={curDir} | name={curName}");

        if (!TryResolveMod(dirOrName, displayName, out curDir, out curName))
        {
            sb.AppendLine("结果：Penumbra 的 mod 列表里找不到这个 Mod（可能还在导入中，或名字对不上）");
            sb.AppendLine($"列表快照：{_penumbra.SnapshotList()}");
            return (false, $"列表里找不到「{displayName}」—— Penumbra 可能还在导入，稍后再试", sb.ToString());
        }

        var before = _penumbra.GetPath(curDir, curName);
        sb.AppendLine($"移动前：ec={before.Ec} 路径=\"{before.Path}\" 自定义路径={before.CustomPath} 自定义名={before.CustomName}");
        sb.AppendLine($"列表快照（前）：{_penumbra.SnapshotList()}");

        if (GroupPath.PathUnderFolder(before.Path, folder))
        {
            sb.AppendLine("结果：本来就在目标文件夹里，无需移动");
            return (true, $"已经在 {folder}/（{before.Path}）", sb.ToString());
        }

        var candidates = GroupPath.CandidatePaths(folder, curDir, displayName);
        sb.AppendLine($"候选路径：{string.Join(" | ", candidates)}");

        var last = "没有可尝试的路径形态";
        var succeededCalls = 0;
        foreach (var candidate in candidates)
        {
            var result = _penumbra.SetPath(curDir, curName, candidate);
            if (result != "Success")
            {
                sb.AppendLine($"  尝试 \"{candidate}\" → SetModPath={result}");
                last = $"SetModPath(\"{candidate}\") → {result}";
                continue;
            }

            succeededCalls++;
            // 读回验证（移动后节点名可能变了 → 按显示名重新定位）
            var (dir2, name2) = ResolveMod(curName, displayName);
            var after = _penumbra.GetPath(dir2, name2);
            sb.AppendLine($"  尝试 \"{candidate}\" → SetModPath=Success；读回 ec={after.Ec} 路径=\"{after.Path}\" " +
                          $"自定义路径={after.CustomPath}");

            if (GroupPath.PathUnderFolder(after.Path, folder))
            {
                sb.AppendLine($"列表快照（后）：{_penumbra.SnapshotList()}");
                sb.AppendLine("结果：成功");
                return (true, $"已归入 {folder}/（{after.Path}）", sb.ToString());
            }

            last = $"SetModPath(\"{candidate}\") 返回 Success，但读回路径仍是 {(after.Path.Length > 0 ? after.Path : "(空, ec=" + after.Ec + ")")}";
        }

        sb.AppendLine($"列表快照（后）：{_penumbra.SnapshotList()}");
        sb.AppendLine("结果：失败 — " + last);

        // 所有候选都回 Success 却毫无变化 → 停用自动归组，别再白试。
        // 注意：官方实现本身就会「回 Success 但什么都没做」——`ModsApi.SetModPath` 丢弃了
        // `RenameAndMove` 的返回值，而 `RenameAndMove` 在 `newPath == 原路径` 时直接 return。
        // 所以这**不能**证明接口被改过，只说明这条路没走通。
        if (succeededCalls > 0 && succeededCalls == candidates.Count)
            FlagSetModPathNoOp("所有候选都回 Success 但路径不变（可能是参数顺序或本机实现差异）");

        return (false, last, sb.ToString());
    }

    /// <summary>
    /// 延迟复核：Penumbra 的导入/重扫还在跑时，节点位置可能被它按**保存文件里旧的路径**放回去
    /// （`ModFileSystemSaver.Load()` 会按 mod.Path.Folder 重建节点）。所以移完要回头看两次，被改回就再移一次。
    /// </summary>
    private void ScheduleRecheck(string dirOrName, string displayName, string folder, int delayMs)
    {
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
                var (dir, name) = ResolveMod(dirOrName, displayName);
                var path = _penumbra.GetPath(dir, name);

                if (GroupPath.PathUnderFolder(path.Path, folder))
                {
                    _log.Info($"[归组复核 {delayMs / 1000}s] 仍在 {folder}/（{path.Path}）");
                    LogDiag($"复核 {delayMs / 1000}s：仍在 {folder}/ ✓");
                    return;
                }

                _log.Warn($"[归组复核 {delayMs / 1000}s] 位置已变回 \"{path.Path}\"（ec={path.Ec}）—— 重新归组一次");
                LogDiag($"复核 {delayMs / 1000}s：位置被改回 \"{path.Path}\" → 重新归组");
                var (ok, msg, _) = GroupOneDetailed(dirOrName, displayName, folder);
                _log.Info($"[归组复核 {delayMs / 1000}s] 重试结果：{(ok ? "成功" : "失败")} — {msg}");
                LogDiag($"复核 {delayMs / 1000}s 重试：{(ok ? "成功" : "失败")} — {msg}");
                _notify?.Invoke($"[XMA Browser] 复核 {delayMs / 1000}s：{(ok ? "已重新归入" : "仍失败")} {folder}/ → {msg}");
            }
            catch (OperationCanceledException) { /* 卸载中 */ }
            catch (Exception e)
            {
                _log.Debug($"[归组复核] 出错：{e.Message}");
            }
        }, ct);
    }

    /// <summary>在 Penumbra 的 mod 列表里定位一个 Mod：先按目录名，再按路径尾段，最后按显示名。</summary>
    private bool TryResolveMod(string dirOrName, string displayName, out string dir, out string name)
    {
        var mods = _penumbra.Mods();

        if (mods.TryGetValue(dirOrName, out var byKey))
        {
            dir = dirOrName;
            name = byKey;
            return true;
        }

        foreach (var kv in mods)
        {
            if (kv.Key.EndsWith("/" + dirOrName, StringComparison.OrdinalIgnoreCase) ||
                kv.Key.Equals(dirOrName, StringComparison.OrdinalIgnoreCase))
            {
                dir = kv.Key;
                name = kv.Value;
                return true;
            }
        }

        if (displayName.Length > 0)
        {
            foreach (var kv in mods)
            {
                if (kv.Value.Equals(displayName, StringComparison.OrdinalIgnoreCase))
                {
                    dir = kv.Key;
                    name = kv.Value;
                    return true;
                }
            }
        }

        dir = dirOrName;
        name = displayName;
        return false;
    }

    /// <summary>同上的宽松版（找不到时返回传入值，调用方自己判断）。</summary>
    private (string Dir, string Name) ResolveMod(string dirOrName, string title)
        => TryResolveMod(dirOrName, title, out var dir, out var name) ? (dir, name) : (dirOrName, title);

    /// <summary>
    /// 「深入自检」：在一个 Mod 上依次试 8 种路径写法，每种都读回真实结果。
    ///
    /// 为什么需要：`RenameAndMove` 有一条**合法但什么都不做**的分支
    /// （`SuccessNothingDone` + `MoveChild` 里 `newParent == oldParent` 直接返回），
    /// 此时 IPC 仍然回 `Success`。读源码推不出「目标文件夹为什么会被解析成当前父目录」，
    /// 所以直接把各种写法在真机上跑一遍，看哪种真的生效。
    ///
    /// 注意：会在你选的这条 Mod 上改名/移动，最后**尽量还原**（名字改回原名、路径改回根）。
    /// </summary>
    public string RunPathWriteExperiment(string dirOrName, string displayName)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== 归组自检：IPC 探针 + 移动/还原实测 ===");
        sb.AppendLine($"Penumbra API {_penumbra.ApiMajor}.{_penumbra.ApiMinor} | mod 根={_penumbra.ModDirectory}");
        sb.AppendLine($"目标 Mod：{displayName}");
        sb.AppendLine();
        sb.AppendLine("【作用范围】只调用 Penumbra 的 IPC（SetModPath / ReloadMod / GetModPath），");
        sb.AppendLine("  改的只是列表里的折叠位置（Penumbra 把它存在 sort_order.json 的 Folder / SortOrderName），");
        sb.AppendLine("  **不移动 / 不删除 / 不新建任何真实文件或文件夹**，并且结束前会把 Mod 放回原位。");
        var diskBefore = DiskSnapshotTop();
        sb.AppendLine($"磁盘快照（前）：{diskBefore}");
        sb.AppendLine();

        // ---------- 探针：参数顺序自检 ----------
        // v0.4.0 的根因就出在这里（SetModPath 的便捷重载把 newPath 和 modName 换了位），
        // 所以把「空路径必须回 InvalidArgument」做成常备探针，同类错误以后一眼就能抓住。
        sb.AppendLine("【探针】SetModPath 参数顺序自检（无副作用：两条都用不存在的 Mod）");
        var (argOk, argReport) = _penumbra.SelfTestSetPathArgOrder();
        sb.AppendLine("    " + argReport.TrimEnd().Replace("\n", "\n    "));
        sb.AppendLine();

        if (!argOk)
            FlagSetModPathNoOp("SetModPath 参数顺序自检未通过");

        if (!TryResolveMod(dirOrName, displayName, out var dir, out var name))
        {
            sb.AppendLine("列表里找不到这条 Mod，路径写法实验跳过。");
            return sb.ToString();
        }

        var original = name;
        sb.AppendLine($"当前：dir={dir} name={name}");
        var start = _penumbra.GetPath(dir, name);
        sb.AppendLine($"起始读回：ec={start.Ec} 路径=\"{start.Path}\" 自定义路径={start.CustomPath} 自定义名={start.CustomName}");
        sb.AppendLine($"列表：{_penumbra.SnapshotList()}");
        sb.AppendLine();

        (string Ec, string Path, bool CustomPath, bool CustomName) ReadBack()
        {
            var (d, n) = ResolveMod(original, displayName);
            return _penumbra.GetPath(d, n);
        }

        // 用**平时真正会用的分组模板**算目标文件夹 —— 自检覆盖的就是线上那条路径。
        var folder = GroupPath.BuildFolder(_cfg.GroupTemplate, _cfg.GroupPrefix, "Gear", "SelfTest", "0", original);
        if (folder.Length == 0) folder = "XMA/SelfTest";
        var target = GroupPath.BuildFullPath(folder, original);
        sb.AppendLine($"目标文件夹：{folder}");
        sb.AppendLine($"目标完整路径：{target}    <- 末段是原名，所以只移动、不改名");
        sb.AppendLine();

        sb.AppendLine("【实测 1】移入目标文件夹");
        var ec1 = _penumbra.SetPath(dir, original, target);
        var after1 = ReadBack();
        sb.AppendLine($"    SetModPath(\"{target}\") → {ec1}");
        sb.AppendLine($"    读回 ec={after1.Ec} 路径=\"{after1.Path}\" 自定义路径={after1.CustomPath} 自定义名={after1.CustomName}");
        var moved = GroupPath.PathUnderFolder(after1.Path, folder);
        sb.AppendLine($"    判定：{(moved ? "成功 ✓ 已归入 " + folder + "/" : "**未生效** —— 读回的路径没落在 " + folder + "/ 下面")}");

        sb.AppendLine();
        sb.AppendLine("【实测 2】还原到根目录（把 Mod 放回原位）");
        var ec2 = _penumbra.SetPath(dir, original, original);
        var after2 = ReadBack();
        sb.AppendLine($"    SetModPath(\"{original}\") → {ec2}");
        sb.AppendLine($"    读回 ec={after2.Ec} 路径=\"{after2.Path}\"");
        var restored = after2.Path.Length > 0 && after2.Path.IndexOf('/') < 0;
        sb.AppendLine($"    判定：{(restored ? "已回到根目录 ✓（" + after2.Path + "）" : "**没能还原**，请手动核对该 Mod 的位置：" + after2.Path)}");

        sb.AppendLine();
        sb.AppendLine(moved ? "结论：自动归组可用 ✓" : "结论：仍然没生效 —— 请把本报告整份发我。");
        if (!moved)
            FlagSetModPathNoOp("移动实测：SetModPath 回 Success 但读回路径未变");

        sb.AppendLine();
        sb.AppendLine($"（注：\"{folder}\" 这个折叠组会留在列表里，是空组，可在 Penumbra 里直接删掉）");

        sb.AppendLine();
        sb.AppendLine($"结束列表：{_penumbra.SnapshotList()}");
        var diskAfter = DiskSnapshotTop();
        sb.AppendLine($"磁盘快照（后）：{diskAfter}");
        sb.AppendLine(diskBefore == diskAfter
            ? "✔ 磁盘顶层前后完全一致 —— 本次自检没有动任何真实文件路径。"
            : "✘ 磁盘顶层发生变化，请把这份报告整份发我。");
        return sb.ToString();
    }

    /// <summary>
    /// mod 根目录的顶层只读快照（仅列目录名，不读不写内容），用来证明自检没碰磁盘。
    /// </summary>
    private string DiskSnapshotTop()
    {
        try
        {
            var root = _penumbra.ModDirectory;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                return "(拿不到 mod 根目录)";

            var names = Directory.EnumerateDirectories(root)
                                 .Select(Path.GetFileName)
                                 .Where(n => !string.IsNullOrEmpty(n))
                                 .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                                 .Take(3000)
                                 .ToList();

            var head = string.Join(", ", names.Take(15));
            return $"顶层目录 {names.Count} 个｜{head}{(names.Count > 15 ? " …" : string.Empty)}";
        }
        catch (Exception e)
        {
            return "(枚举失败：" + e.Message + ")";
        }
    }

    /// <summary>在 mod 根目录里找一个不冲突的目录名。</summary>
    private static string UniqueDirIn(string root, string safeName)
    {
        var target = Path.Combine(root, safeName);
        if (!Directory.Exists(target)) return target;
        for (var i = 2; i < 1000; i++)
        {
            var p = Path.Combine(root, $"{safeName}_{i}");
            if (!Directory.Exists(p)) return p;
        }
        return target;
    }

    /// <summary>
    /// 把解出来的目录搬进 Penumbra 的 mod 根目录。
    /// 临时目录通常在 C:，mod 根可能在别的盘 → Directory.Move 跨盘会抛，所以要能退回「复制 + 删」。
    /// </summary>
    private static void MoveDirectory(string src, string dst)
    {
        try
        {
            Directory.Move(src, dst);
            return;
        }
        catch (IOException)
        {
            // 跨盘或目标已存在 → 复制过去
        }

        try
        {
            CopyDirectory(src, dst);
            Directory.Delete(src, true);
        }
        catch
        {
            // 复制也没成：把源目录留在原处并让它作为 mod 目录（Penumbra 的 AddMod 只认根目录下的名字，
            // 所以失败时向上抛，让队列把这条标记为失败并把原因说清楚）
            throw new IOException($"无法把解出来的 Mod 目录搬进 {dst}（源：{src}）");
        }
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, f);
            var target = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(f, target, true);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _worker.Wait(TimeSpan.FromSeconds(3)); } catch { /* 忽略 */ }
        _cts.Dispose();
        _wake.Dispose();
    }
}
