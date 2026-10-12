using System;
using System.Collections.Generic;
using XmaBrowser.Site;

namespace XmaBrowser.Ui;

/// <summary>
/// 取图的时间上限（纯常量，可离线自测）。
///
/// 为什么值得单独拿出来：这三个数直接决定用户会不会看到「进度条一直转」——
/// 只要有一条路没有上限，界面就只能永远等下去。
/// 具体数值统一由 <see cref="FetchLimits"/> 给出（一处定义，免得两套数字走偏）。
/// </summary>
public static class ImageFetchLimits
{
    /// <summary>失败后多久才允许自动重试同一个 URL（原来是永久记住，NSFW 的图一次 403 就永远取不到）。</summary>
    public const int FailureCooldownSeconds = 600;

    /// <summary>**停滞**上限（毫秒）：多久没有新字节就判定卡住（不是"总时长"）。
    /// 每读到数据就重置 —— 见 FetchLimits.ImageStallMs 里的踩坑记录。</summary>
    public const int StallMs = FetchLimits.ImageStallMs;

    /// <summary>一张图的整条任务上限（毫秒）：直连 + 通道加起来超过它就必须给出失败原因，绝不无限等。</summary>
    public const int JobTimeoutMs = FetchLimits.ImageJobMs;
}

/// <summary>
/// 「同一张图被多个调用点先后请求」时的任务登记（纯逻辑，可离线自测）。
///
/// 为什么需要它（这是一个真实缺陷的根因）：
///   列表里的缩略图只想**直连**（快，而且不能把浏览器通道占满 —— 一页 15 张）；
///   详情面板里的预览图**允许回退到浏览器通道**（NSFW 的图直连必然 403）。
///   但两者经常是**同一张图**（站点的卡片缩略图就是第一张预览图）。
///   原来的实现是「已经在下发中就静默放弃」——于是后到的、真正需要通道的那个请求
///   什么也没做就返回了，详情面板的图永远出不来，界面上的进度条一直转（用户报的就是这个）。
///
/// 正确语义：
///   · 后到的请求**参与**已有任务（不重复下发）；
///   · 但它可以给正在跑的任务**升级权限**（允许回退通道）——任务在决定要不要回退时再读这个标记，
///     而不是在启动时一次性定死。
/// </summary>
public sealed class ImageJobState
{
    public enum Ask
    {
        /// <summary>没有在跑的任务 → 调用方应当启动一个。</summary>
        Begin,
        /// <summary>已有任务在跑 → 只管读结果，别重复下发。</summary>
        Join,
    }

    private sealed class Job
    {
        public bool ChannelAllowed { get; set; }
    }

    private readonly Dictionary<string, Job> _jobs = new(StringComparer.Ordinal);

    /// <summary>登记一次请求。返回 Begin = 请开始下载；Join = 已经有任务在跑。</summary>
    public Ask Request(string url, bool allowChannel)
    {
        var key = url ?? string.Empty;
        if (_jobs.TryGetValue(key, out var running))
        {
            // ⭐ 关键：不重复下发，但把「允许走通道」这个权限补上
            if (allowChannel) running.ChannelAllowed = true;
            return Ask.Join;
        }

        _jobs[key] = new Job { ChannelAllowed = allowChannel };
        return Ask.Begin;
    }

    /// <summary>这个任务现在允许回退到浏览器通道吗（任务执行到回退那一步时再问）。</summary>
    public bool ChannelAllowed(string url)
        => _jobs.TryGetValue(url ?? string.Empty, out var j) && j.ChannelAllowed;

    /// <summary>任务结束（无论成败）都要调，否则这个 URL 永远不会再被下发。</summary>
    public void Finish(string url) => _jobs.Remove(url ?? string.Empty);

    public bool IsRunning(string url) => _jobs.ContainsKey(url ?? string.Empty);

    public int Count => _jobs.Count;
}
