using System;

namespace XmaBrowser.Site;

/// <summary>
/// 「直连现在不好使」的短期记忆。
///
/// 实测背景（本机真实数据）：站点偶尔（或持续）对直连做 Cloudflare 人机校验
/// （页面标题 <c>Just a moment...</c>），此时**每次搜索都要先失败一次直连**（实测 1.0 / 1.0 / 2.2 秒）
/// 才落到浏览器通道（实测 2.8–3.0 秒），白等一两秒非常明显。
///
/// 所以被挑战过一次就记一笔：接下来这段时间里，NSFW 模式/需要通道的场景**直接用通道**，
/// 不再先撞一次直连；冷却期过了再让直连试。
///
/// 纯逻辑、无依赖，可离线自测。
/// </summary>
public sealed class DirectBlockMemory
{
    public static readonly TimeSpan DefaultCooldown = TimeSpan.FromMinutes(5);

    private readonly TimeSpan _cooldown;
    private DateTime _blockedUntil = DateTime.MinValue;

    public DirectBlockMemory(TimeSpan? cooldown = null)
        => _cooldown = cooldown ?? DefaultCooldown;

    /// <summary>记一笔「直连刚被拦」（从 <paramref name="now"/> 起冷却）。</summary>
    public void MarkBlocked(DateTime now) => _blockedUntil = now + _cooldown;

    /// <summary>直连成功过 → 不用再绕路了。</summary>
    public void MarkHealthy() => _blockedUntil = DateTime.MinValue;

    public bool IsBlocked(DateTime now) => now < _blockedUntil;

    public TimeSpan Remaining(DateTime now) => IsBlocked(now) ? _blockedUntil - now : TimeSpan.Zero;

    /// <summary>给界面/日志用的一句话（不在冷却期返回空串）。</summary>
    public string Describe(DateTime now)
    {
        var remain = Remaining(now);
        return remain <= TimeSpan.Zero
            ? string.Empty
            : $"直连刚被站点人机校验拦过，接下来 {Math.Ceiling(remain.TotalMinutes):0} 分钟内直接走浏览器通道";
    }
}
