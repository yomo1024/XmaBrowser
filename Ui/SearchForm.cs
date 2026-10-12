using System;
using System.Collections.Generic;
using System.Linq;
using XmaBrowser.Site;

namespace XmaBrowser.Ui;

/// <summary>
/// 「高级搜索」表单：把站点支持的检索条件都摊开，留空 = 不看这个条件。
///
/// 刻意用**公开字段**而不是属性：这些值要直接喂给 ImGui 的 InputText（需要 ref）。
/// </summary>
public sealed class SearchForm
{
    public static readonly (string Label, string Value)[] Sorts =
    [
        ("最新发布", "time_published"),
        ("最近更新", "time_edited"),
        ("今日最多浏览", "views_today"),
    ];

    public string Text = string.Empty;      // 关键词（站点 basic_text）
    public string Name = string.Empty;
    public string Author = string.Empty;
    public string Tags = string.Empty;
    public string Races = string.Empty;
    public string Genders = string.Empty;
    public string Affects = string.Empty;
    public string Types = string.Empty;     // 类型 id，逗号分隔
    public int SortIndex;
    public bool OnlyDtCompatible = true;

    /// <summary>NSFW 三态：0=排除（默认） 1=混合（都显示） 2=只看 NSFW。语义见 SearchQuery.NsfwMode。</summary>
    public int NsfwMode;

    /// <summary>把「关键词」这一栏当标签精确检索。</summary>
    public bool KeywordAsTag;

    public static string SortLabel(int i) => Sorts[Math.Clamp(i, 0, Sorts.Length - 1)].Label;

    public SearchQuery ToQuery(int page) => new()
    {
        Page = page,
        OnlyDtCompatible = OnlyDtCompatible,
        NsfwMode = NsfwMode,
        SortBy = Sorts[Math.Clamp(SortIndex, 0, Sorts.Length - 1)].Value,
        SortOrder = "desc",
        Text = KeywordAsTag ? string.Empty : Trim(Text),
        Tags = KeywordAsTag ? Trim(Text) : Trim(Tags),
        Name = Trim(Name),
        Author = Trim(Author),
        Races = Trim(Races),
        Genders = Trim(Genders),
        Affects = Trim(Affects),
        Types = ParseTypes(Types),
    };

    /// <summary>哪些条件在用（界面显示「筛选中」）。</summary>
    public IEnumerable<string> ActiveConditions()
    {
        if (Trim(Text).Length > 0) yield return (KeywordAsTag ? "标签:" : "关键词:") + Trim(Text);
        if (Trim(Name).Length > 0) yield return "名称:" + Trim(Name);
        if (Trim(Author).Length > 0) yield return "作者:" + Trim(Author);
        if (Trim(Tags).Length > 0) yield return "标签:" + Trim(Tags);
        if (Trim(Races).Length > 0) yield return "种族:" + Trim(Races);
        if (Trim(Genders).Length > 0) yield return "性别:" + Trim(Genders);
        if (Trim(Affects).Length > 0) yield return "影响/替换:" + Trim(Affects);
        if (Trim(Types).Length > 0) yield return "类型:" + Trim(Types);
    }

    public bool HasAdvanced =>
        Trim(Name).Length > 0 || Trim(Author).Length > 0 || Trim(Tags).Length > 0 ||
        Trim(Races).Length > 0 || Trim(Genders).Length > 0 || Trim(Affects).Length > 0 ||
        Trim(Types).Length > 0;

    public void Reset()
    {
        Text = Name = Author = Tags = Races = Genders = Affects = Types = string.Empty;
        SortIndex = 0;
        OnlyDtCompatible = true;
        NsfwMode = 0;
        KeywordAsTag = false;
    }

    private static string Trim(string? s) => (s ?? string.Empty).Trim();

    private static List<int>? ParseTypes(string raw)
    {
        var ids = (raw ?? string.Empty).Split([',', '，', ' ', ';'], StringSplitOptions.RemoveEmptyEntries)
                     .Select(x => int.TryParse(x.Trim(), out var v) ? v : -1)
                     .Where(v => v > 0)
                     .Distinct()
                     .ToList();
        return ids.Count > 0 ? ids : null;
    }
}
