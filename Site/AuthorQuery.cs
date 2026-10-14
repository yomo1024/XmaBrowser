using System;
using System.Text;

namespace XmaBrowser.Site;

/// <summary>
/// 站点 `author=` 参数的清洗规则 —— **实测出来的，别凭感觉改**。
///
/// 真站点实测（同一个作者「Helina's Bakery」的几种写法）：
///
/// | 查询 | 结果 |
/// |---|---|
/// | `author=Helina's Bakery`（ASCII 撇号） | **0 条** |
/// | `author=Helina&amp;#39;s Bakery`（HTML 实体） | 0 条 |
/// | `author=Helina&amp;#39;s Bakery`（小写实体） | 0 条 |
/// | **`author=Helina s Bakery`（撇号换成空格）** | **15 条 ✓** |
/// | `author=Helina`（前缀） | 15 条 |
///
/// 也就是说：**撇号会把这条路上的匹配彻底弄坏**，连 HTML 实体写法也救不回来
/// （站点会先把实体解码回撇号，然后栽在同一个地方）。而作者名里带撇号并不罕见。
///
/// ⚠⚠ **只清洗 author，绝对不要清洗 name** —— 标题侧的行为是**反的**：
///   · `name=Kitten's Rapture` → 1 条 ✓（正常工作）
///   · `name=Kitten s Rapture` → 0 条 ✗（把撇号换空格反而弄坏）
/// 标题里的撇号是标题本身的一部分，站点处理得好好的。所以这两个参数的清洗规则不能混。
/// </summary>
public static class AuthorQuery
{
    /// <summary>
    /// 会把站点匹配弄坏的字符：ASCII 撇号、弯撇号（左右）、双引号（直/左右）、反引号。
    /// 用户从网页复制作者名时，撇号多半是弯的（U+2019），所以两种都要认。
    /// </summary>
    private static readonly char[] Bad = ['\'', '\u2018', '\u2019', '"', '\u201C', '\u201D', '`'];

    /// <summary>
    /// 清洗作者查询串：坏字符换空格 → 折叠连续空格 → 去首尾空格。
    /// 空串 / 全是坏字符时返回空串（调用方据此**不发**这个参数，而不是发一个必然 0 条的值）。
    /// </summary>
    public static string Sanitize(string? raw)
    {
        var s = raw ?? string.Empty;
        if (s.Length == 0) return string.Empty;

        var sb = new StringBuilder(s.Length);
        var pendingSpace = false;

        foreach (var ch in s)
        {
            if (Array.IndexOf(Bad, ch) >= 0 || char.IsWhiteSpace(ch))
            {
                pendingSpace = sb.Length > 0;      // 开头的空白直接丢掉
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    /// <summary>清洗后还有内容吗（决定要不要发 author= 参数）。</summary>
    public static bool HasContent(string? raw) => Sanitize(raw).Length > 0;

    /// <summary>清洗前后的差异说明（没变化返回空串）—— 用于写进日志，别让用户以为「查询被改了」。</summary>
    public static string DescribeIfChanged(string? raw)
    {
        var original = (raw ?? string.Empty).Trim();
        var clean = Sanitize(raw);
        return original.Length > 0 && !string.Equals(clean, original, StringComparison.Ordinal)
            ? $"{original} → {clean}"
            : string.Empty;
    }
}
