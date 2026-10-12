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
            if (!_channel.Enabled)
                throw new InvalidOperationException(e.Message + "（可在设置里开启浏览器通道）");

            job.UsedBrowserChannel = true;
            job.Message = "走浏览器通道下载（需要登录态的内容）…";
            Changed?.Invoke();
            var ok = await _channel.DownloadAsync(job.Url, dest, Progress, ct).ConfigureAwait(false);
            if (!ok)
                throw new InvalidOperationException("浏览器通道也拿不到这个文件：" + _channel.LastError);
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

        var packages = new List<string>();
        string? looseInstallName = null;

        switch (PackageResolver.KindOf(dest))
        {
            case PackageKind.PenumbraPackage:
                packages.Add(dest);
                break;

            case PackageKind.Zip:
            {
                var inner = PackageResolver.ExtractInnerPackages(dest, workDir, out var err);
                if (inner.Count > 0)
                {
                    packages.AddRange(inner);
                    job.Message = inner.Count > 1 ? $"包里含 {inner.Count} 个包，逐个安装" : "包内找到 Mod 包";
                }
                else
                {
                    // 松散文件：解到 Penumbra 的 mod 根目录下，再用 AddMod 登记
                    var modRoot = _penumbra.ModDirectory;
                    if (string.IsNullOrEmpty(modRoot))
                        throw new InvalidOperationException("拿不到 Penumbra 的 mod 目录，无法登记松散文件包");

                    looseInstallName = PackageResolver.SafeModDirName(job.FileName, $"mod-{job.ModId}");
                    var target = Path.Combine(modRoot, looseInstallName);
                    if (!PackageResolver.ExtractLoose(dest, target, out var err2))
                        throw new IOException(err2.Length > 0 ? err2 : "解压失败");

                    job.Message = "松散文件：已放到 Penumbra mod 目录，准备登记";
                    _ = err;
                }
                break;
            }

            default:
                throw new NotSupportedException(
                    $"暂不支持这种包（{Path.GetExtension(dest)}）。7z/rar 请先用工具转成 .pmp 或 .zip。");
        }

        // ---------- 3) 装进 Penumbra ----------
        job.State = JobState.Installing;
        job.Message = packages.Count > 0 ? $"安装 {packages.Count} 个包…" : "登记 mod…";
        Changed?.Invoke();

        var before = _penumbra.Mods();

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

        if (looseInstallName is not null)
            _penumbra.Add(looseInstallName);

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
        if (job.CoverPath.Length > 0 && newDirs.Count > 0)
        {
            var modRoot = _penumbra.ModDirectory;
            if (!string.IsNullOrEmpty(modRoot))
            {
                var applied = 0;
                foreach (var dir in newDirs)
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

    public void Dispose()
    {
        _cts.Cancel();
        try { _worker.Wait(TimeSpan.FromSeconds(3)); } catch { /* 忽略 */ }
        _cts.Dispose();
        _wake.Dispose();
    }
}
