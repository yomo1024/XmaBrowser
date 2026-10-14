namespace XmaBrowser.Site;

/// <summary>
/// 文本检索的两种语义 —— **实测出来的，别凭感觉改**。
///
/// | 参数 | 语义 | 实测（真站点，搜「Bellissente, a makeup for Aerin」） |
/// |---|---|---|
/// | `name=` | **标题里每个词都要命中**（token-AND，只搜标题） | **1 条，正是它，排第 1** |
/// | `basic_text=` | **全文模糊**（标题 + 描述，词是松散匹配） | 15 条杂项，**翻到第 3 页都没有它**，换任何排序都一样 |
///
/// 另外实测 `name=` 的匹配行为：
///   · `name=Bellissente`（单个词）→ 1 条（就是它）
///   · `name=Aerin makeup`（换序）→ 2 条（标题里同时含这两个词的）
///   · `name=makeup`（泛词）→ 15 条
/// → 所以「粘一个 mod 全名进去能搜到」靠的是 `name=`，而任务报告里搜不到正是因为走了 `basic_text=`。
///
/// 结论：**主搜索框默认按标题搜**（最常见、也最精确）；想连描述一起搜，由用户显式开关。
/// </summary>
public static class TextSearch
{
    /// <summary>标题检索用的参数名。</summary>
    public const string TitleParam = "name";

    /// <summary>全文检索用的参数名。</summary>
    public const string FullTextParam = "basic_text";

    /// <summary>
    /// 关键词该发哪个参数。默认标题（`name`）；<paramref name="asFullText"/> = true 时才用全文（`basic_text`）。
    /// </summary>
    public static string ParamFor(bool asFullText) => asFullText ? FullTextParam : TitleParam;

    /// <summary>给界面用的一句话说明。</summary>
    public static string Describe(bool asFullText) =>
        asFullText ? "全文搜索（标题 + 描述）" : "标题搜索（只匹配 Mod 名）";
}
