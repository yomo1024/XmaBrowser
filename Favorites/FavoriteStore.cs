using System;
using System.Collections.Generic;
using System.Linq;
using XmaBrowser.Site;

namespace XmaBrowser.Favorites;

/// <summary>
/// 收藏的一条 Mod。**存够离线浏览的信息**（标题/作者/类型/缩略图 URL），
/// 这样在游戏里点开「收藏」页不必联网也能把列表画出来。
/// </summary>
[Serializable]
public sealed class FavoriteMod
{
    public string ModId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string ThumbUrl { get; set; } = string.Empty;
    public DateTime AddedUtc { get; set; }
}

/// <summary>
/// 关注的一位作者。
/// <see cref="SeenModIds"/> 是「上次看他作品时见过的 modId」，
/// 靠它算「自上次以来新增了哪几条」（站点搜索不能按日期过滤，所以用 id 差集，稳）。
/// </summary>
[Serializable]
public sealed class FollowedAuthor
{
    public string Name { get; set; } = string.Empty;
    public DateTime AddedUtc { get; set; }
    public DateTime LastCheckedUtc { get; set; }
    public List<string> SeenModIds { get; set; } = [];
    public List<string> NewModIds { get; set; } = [];
}

/// <summary>
/// 收藏 / 关注 的纯逻辑（不碰 Dalamud、不碰网络，能在无游戏环境自测）。
///
/// 关于「关注作者」的搜索语义（实测）：
/// 站点的 <c>author=</c> 是**大小写不敏感的前缀/子串匹配**
/// （author=Sadanoya → 3 条全是这位；author=Sada → 8 条含同前缀的其它作者；
///  不存在的名字 → 0 条）。所以「看他作品」用精确作者名搜，结果就是他的作品。
/// </summary>
public static class FavoriteStore
{
    public const int MaxFavorites = 500;
    public const int MaxAuthors = 200;
    public const int MaxSeenIds = 300;

    // ------------------------------------------------------------ 收藏

    public static bool SameId(string? a, string? b) => string.Equals(a, b, StringComparison.Ordinal);

    public static bool SameName(string? a, string? b)
        => string.Equals((a ?? string.Empty).Trim(), (b ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

    public static int IndexOfFavorite(IList<FavoriteMod> list, string? modId)
    {
        for (var i = 0; i < list.Count; i++)
            if (SameId(list[i].ModId, modId)) return i;
        return -1;
    }

    public static bool IsFavorite(IList<FavoriteMod> list, string? modId)
        => !string.IsNullOrEmpty(modId) && IndexOfFavorite(list, modId) >= 0;

    /// <summary>切换收藏。返回切换后的状态：true = 现在已收藏。</summary>
    public static bool Toggle(IList<FavoriteMod> list, string modId, string title, string author,
                              string type, string thumbUrl)
    {
        var idx = IndexOfFavorite(list, modId);
        if (idx >= 0)
        {
            list.RemoveAt(idx);
            return false;
        }

        list.Insert(0, new FavoriteMod
        {
            ModId = modId,
            Title = title,
            Author = author,
            Type = type,
            ThumbUrl = thumbUrl,
            AddedUtc = DateTime.UtcNow,
        });

        Trim(list);
        return true;
    }

    public static bool Unfavorite(IList<FavoriteMod> list, string? modId)
    {
        var idx = IndexOfFavorite(list, modId);
        if (idx < 0) return false;
        list.RemoveAt(idx);
        return true;
    }

    /// <summary>超上限时丢掉最老的（列表前面是新的，所以从尾部丢）。</summary>
    public static int Trim(IList<FavoriteMod> list)
    {
        var removed = 0;
        while (list.Count > MaxFavorites)
        {
            list.RemoveAt(list.Count - 1);
            removed++;
        }
        return removed;
    }

    // ------------------------------------------------------------ 关注作者

    public static int IndexOfAuthor(IList<FollowedAuthor> list, string? name)
    {
        for (var i = 0; i < list.Count; i++)
            if (SameName(list[i].Name, name)) return i;
        return -1;
    }

    public static bool IsFollowing(IList<FollowedAuthor> list, string? name)
        => !string.IsNullOrWhiteSpace(name) && IndexOfAuthor(list, name) >= 0;

    /// <summary>切换关注。返回切换后的状态：true = 现在已关注。</summary>
    public static bool ToggleAuthor(IList<FollowedAuthor> list, string name)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length == 0) return false;

        var idx = IndexOfAuthor(list, name);
        if (idx >= 0)
        {
            list.RemoveAt(idx);
            return false;
        }

        list.Insert(0, new FollowedAuthor { Name = name, AddedUtc = DateTime.UtcNow });
        while (list.Count > MaxAuthors) list.RemoveAt(list.Count - 1);
        return true;
    }

    public static bool Unfollow(IList<FollowedAuthor> list, string? name)
    {
        var idx = IndexOfAuthor(list, name);
        if (idx < 0) return false;
        list.RemoveAt(idx);
        return true;
    }

    /// <summary>标记「我刚看过他的作品了」（清掉未读新品标记）。</summary>
    public static void MarkAuthorRead(FollowedAuthor author)
    {
        author.NewModIds.Clear();
        author.LastCheckedUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// 用「这次拿到的 modId 列表」和已见集合比出新品，并把已见集合更新为并集。
    ///
    /// 首次关注（已见集合为空）时**不**把所有条目当新品 —— 否则关注完立刻显示「新增 3 条」很傻；
    /// 这时只把当前这批记成已见。
    /// </summary>
    public static List<string> DiffAndRemember(FollowedAuthor author, IEnumerable<string?> currentIds)
    {
        var current = currentIds.Where(id => !string.IsNullOrEmpty(id))
                                .Select(id => id!)
                                .Distinct(StringComparer.Ordinal)
                                .ToList();

        var firstTime = author.SeenModIds.Count == 0;
        var fresh = firstTime
            ? []
            : current.Where(id => !author.SeenModIds.Contains(id, StringComparer.Ordinal)).ToList();

        foreach (var id in current)
            if (!author.SeenModIds.Contains(id, StringComparer.Ordinal))
                author.SeenModIds.Add(id);

        if (author.SeenModIds.Count > MaxSeenIds)
            author.SeenModIds.RemoveRange(0, author.SeenModIds.Count - MaxSeenIds);

        author.LastCheckedUtc = DateTime.UtcNow;
        author.NewModIds = fresh;
        return fresh;
    }

    /// <summary>把站点搜到的结果折算成收藏记录（用于「收藏当前这页」之类的批量操作）。</summary>
    public static FavoriteMod FromHit(SearchHit hit) => new()
    {
        ModId = hit.ModId,
        Title = hit.Title,
        Author = hit.Author,
        Type = hit.Type,
        ThumbUrl = hit.ThumbUrl,
        AddedUtc = DateTime.UtcNow,
    };
}
