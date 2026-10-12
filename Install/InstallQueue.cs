using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

    public event Action? Changed;

    public InstallQueue(XmaClient site, PenumbraBridge penumbra, Configuration cfg, BrowserChannel channel,
                        CoverFetcher covers, IXmaLog log, string workRoot)
    {
        _site = site;
        _penumbra = penumbra;
        _cfg = cfg;
        _channel = channel;
        _covers = covers;
        _log = log;
        _workRoot = workRoot;
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

            var collection = _penumbra.YourCollection();
            if (collection is null)
            {
                job.Message = "装好了，但拿不到当前集合，没自动启用（去 Penumbra 里勾一下）";
            }
            else
            {
                var mods = _penumbra.Mods();
                foreach (var dir in newDirs)
                    _penumbra.SetEnabled(collection.Value.Id, dir, true, mods.TryGetValue(dir, out var n) ? n : dir);
                job.Message = $"已装好并在「{collection.Value.Name}」里启用";
            }
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
            var affected = newDirs.Concat(addedNames).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (folder.Length > 0 && affected.Count > 0)
                GroupInstalled(job, folder, affected);
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
    /// 把刚装好的 Mod 移进文件夹（Penumbra 列表里就是折叠组）。
    ///
    /// 用 SetModPath（内部是 Penumbra 的 RenameAndMove）；目标文件夹先落盘，
    /// 因为 RenameAndMove 未必会自己创建中间目录（Penumbra 自己的「默认导入文件夹」
    /// 也是先 FindOrCreateAllFolders 再挪）。
    /// </summary>
    private void GroupInstalled(InstallJob job, string folder, List<string> dirs)
    {
        var modRoot = _penumbra.ModDirectory;
        if (string.IsNullOrEmpty(modRoot))
        {
            _log.Warn("拿不到 Penumbra 的 mod 目录，跳过归入文件夹");
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.Combine(modRoot, folder.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception e)
        {
            _log.Debug($"预建目标文件夹失败（继续尝试移动）：{e.Message}");
        }

        var moved = 0;
        var failed = 0;
        foreach (var dir in dirs)
        {
            var (curDir, curName) = ResolveMod(dir, job.Title);
            var target = GroupPath.BuildFullPath(folder, curDir);
            if (target.Length == 0 || !GroupPath.IsSafeRelativePath(target))
            {
                _log.Warn($"分组路径不安全，跳过：{target}");
                failed++;
                continue;
            }
            if (string.Equals(curDir.Replace('\\', '/'), target, StringComparison.OrdinalIgnoreCase))
            {
                moved++;                                  // 已经在该位置（比如上次装过）
                continue;
            }

            var result = _penumbra.SetPath(curDir, curName, target);
            if (result == "Success")
            {
                moved++;
                continue;
            }

            // 可能被 Penumbra 自己的「默认导入文件夹」刚挪走 → 按名字重新定位再试一次
            var (dir2, name2) = ResolveMod(curName, job.Title);
            if (!string.Equals(dir2, curDir, StringComparison.OrdinalIgnoreCase))
            {
                var retry = _penumbra.SetPath(dir2, name2, target);
                if (retry == "Success")
                {
                    moved++;
                    continue;
                }
                result = retry;
            }

            failed++;
            _log.Warn($"归入文件夹失败（{curDir} → {target}）：{result}");
        }

        if (moved > 0)
        {
            job.Message += $"，已归入 {folder}/";
            _log.Info($"已把 {moved} 个 Mod 归入 {folder}/（失败 {failed}）");
        }
        else if (failed > 0)
        {
            job.Message += $"（归入 {folder}/ 失败，仍在根目录）";
        }
    }

    /// <summary>在 Penumbra 的 mod 列表里定位一个 Mod：先按目录名，再按路径尾段，最后按显示名。</summary>
    private (string Dir, string Name) ResolveMod(string dirOrName, string title)
    {
        var mods = _penumbra.Mods();

        if (mods.TryGetValue(dirOrName, out var byKey))
            return (dirOrName, byKey);

        foreach (var kv in mods)
        {
            if (kv.Key.EndsWith("/" + dirOrName, StringComparison.OrdinalIgnoreCase) ||
                kv.Key.Equals(dirOrName, StringComparison.OrdinalIgnoreCase))
                return (kv.Key, kv.Value);
        }

        if (title.Length > 0)
        {
            foreach (var kv in mods)
                if (kv.Value.Equals(title, StringComparison.OrdinalIgnoreCase))
                    return (kv.Key, kv.Value);
        }

        return (dirOrName, title);
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
