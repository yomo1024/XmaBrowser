using System;
using System.Collections.Generic;
using XmaBrowser.Site;

namespace XmaBrowser.Ui;

/// <summary>
/// 流式（向下滚动加载更多）的状态机 —— 纯逻辑，可离线自测。
///
/// 为什么单独抽出来：无限滚动真正的难点不在"滚动"，而在**什么时候该停**、以及**别把重复内容当成新内容**：
///   · 站点在翻页期间有人发新 mod 时，相邻两页会重复几条；
///   · NSFW 混合/只看 NSFW 这类结果集，翻到深处可能整页都是已见过的；
///   · 用户快速滚动会连触发好几次请求。
/// 这些判据写进界面代码里就没法验证；放这里就能用真实数据离线断言。
///
/// 它只负责"记账"（见过哪些、下一页码、该不该停），不碰界面、不碰网络。
/// </summary>
public sealed class StreamState
{
    /// <summary>连续多少页「一条新的都没有」就停下来（防站点在末尾反复给同一批）。</summary>
    public const int MaxNoProgressStrikes = 2;

    /// <summary>页号硬上限：站点总共才三千多页，翻到这个数说明判据出问题了，直接停。</summary>
    public const int MaxPage = 4000;

    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    /// <summary>这一轮流式加载是从第几页开始的（顶部跳页会重置它）。</summary>
    public int StartPage { get; private set; } = 1;

    /// <summary>下一次该请求的页号。</summary>
    public int NextPage { get; private set; } = 1;

    /// <summary>累计已经加载进来的条目数（含去重后丢弃的？不含 —— 只算真正新增的）。</summary>
    public int Loaded { get; private set; }

    /// <summary>站点声明的总数（0 = 还不知道）。</summary>
    public int Total { get; private set; }

    /// <summary>已经取过几页。</summary>
    public int PagesFetched { get; private set; }

    public bool End { get; private set; }
    public string EndReason { get; private set; } = string.Empty;
    public int NoProgressStrikes { get; private set; }

    /// <summary>一次追加的结果：真正要去界面里追加的条目 + 是否到头了。</summary>
    public sealed record AppendResult(IReadOnlyList<SearchHit> NewItems, bool End, string Reason);

    /// <summary>开始新一轮流式加载（新搜索、或顶部跳到某一页）。</summary>
    public void Reset(int startPage, int totalKnown = 0)
    {
        StartPage = Math.Max(1, startPage);
        NextPage = StartPage;
        Loaded = 0;
        Total = Math.Max(0, totalKnown);
        PagesFetched = 0;
        End = false;
        EndReason = string.Empty;
        NoProgressStrikes = 0;
        _seen.Clear();
    }

    /// <summary>
    /// 追加一页结果。返回"真正的新条目"，并把终止判据一并更新。
    /// 注意：**没到头的正常情况也可能返回 0 条新增**（整页重复），这时不该把列表清空或者死循环。
    /// </summary>
    public AppendResult Append(IReadOnlyList<SearchHit> pageItems, int totalMods)
    {
        if (End) return new AppendResult([], true, EndReason);

        pageItems ??= [];
        PagesFetched++;
        if (totalMods > 0) Total = totalMods;

        var fresh = new List<SearchHit>(pageItems.Count);
        foreach (var hit in pageItems)
        {
            // 没有 ModId 的条目本来也用不了（详情/原页面链接都靠它），直接跳过
            if (hit is null || hit.ModId.Length == 0) continue;
            if (_seen.Add(hit.ModId)) fresh.Add(hit);
        }
        Loaded += fresh.Count;

        // ① 站点给了空页：说明真的到底了
        if (pageItems.Count == 0)
            return Stop("站点没有更多了（这一页是空的）", fresh);

        // ② 整页都是见过的：给它几次机会，连续几页如此就当到底
        if (fresh.Count == 0)
        {
            NoProgressStrikes++;
            if (NoProgressStrikes >= MaxNoProgressStrikes)
                return Stop($"连续 {NoProgressStrikes} 页都是已看过的内容，先停在这里", fresh);

            NextPage++;
            return new AppendResult(fresh, false, string.Empty);
        }

        NoProgressStrikes = 0;
        NextPage++;

        // ③ 已经加载到站点声明的总数
        if (Total > 0 && Loaded >= Total)
            return Stop($"已经到站点总数（{Total} 条）了", fresh);

        // ④ 页号硬上限（判据出问题时的保险，别把站点翻穿）
        if (NextPage > MaxPage)
            return Stop("翻得太深了，先停在这里", fresh);

        return new AppendResult(fresh, false, string.Empty);
    }

    /// <summary>
    /// 收尾并标记「到头了」。
    ///
    /// ⚠⚠ **必须把这一页真正的新条目一起带回去**（<paramref name="fresh"/>）。
    /// 曾经这里返回空列表，导致：站点只有 1 条结果时，第一页就满足 `Loaded >= Total` → 走这里 →
    /// 刚解析出来的条目被丢掉 → 首屏 `_hits` 变成空 → **列表一片空白**。
    /// 也就是说「结果不足一页（≤15 条）的搜索」全都会显示不出来（搜具体 Mod 名必中）。
    /// 「到头了」表示**不用再翻下一页**，绝不表示「这一页不要了」。
    /// </summary>
    private AppendResult Stop(string reason, IReadOnlyList<SearchHit> fresh)
    {
        End = true;
        EndReason = reason;
        return new AppendResult(fresh, true, reason);
    }

    /// <summary>
    /// 把「已有 + 新增」拼成一个**新列表**返回，绝不原地改传进来的那个。
    ///
    /// ⚠ 这是为了防一个真实崩溃：渲染线程每帧都在 foreach 结果列表，
    /// 而后台线程拿到新一页时会往里追加 —— 原地 AddRange 会抛
    /// "Collection was modified; enumeration operation may not execute"。
    /// 换成「另建一个新列表 + 整体换引用」（引用赋值是原子的，
    /// 正在被枚举的旧列表完全不受影响），零锁、零中断。
    /// </summary>
    public static List<SearchHit> Merge(IReadOnlyList<SearchHit> existing, IReadOnlyList<SearchHit> added)
    {
        var merged = new List<SearchHit>((existing?.Count ?? 0) + (added?.Count ?? 0));
        if (existing is not null)
            foreach (var h in existing) merged.Add(h);
        if (added is not null)
            foreach (var h in added) merged.Add(h);
        return merged;
    }

    /// <summary>目前累计加载了多少条（界面显示用）。</summary>
    public string ProgressText => Total > 0 ? $"{Loaded} / {Total}" : Loaded.ToString();
}
