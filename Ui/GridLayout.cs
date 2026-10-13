using System;
using System.Numerics;

namespace XmaBrowser.Ui;

/// <summary>
/// 界面尺寸计算（纯函数，可离线自测）。
///
/// 为什么要单独抽出来：这次踩的 bug 就是「图片画多大、卡片留多高」全靠手写常量猜 ——
/// 图片宽写死 `cardW - 12`，而**带边框的子窗口内部宽度**实际是
/// `cardW - 2*WindowPadding.X - 2*ChildBorderSize`（缩放下约 cardW - 18），
/// 于是图片横向溢出 → 每张卡都长出滚动条；卡片高又写死 `imgH + 50`，
/// 装不下「图片 + 一行按钮 + 一行文字」→ 再来一条纵向滚动条。
///
/// 这类"算尺寸"的逻辑抽到这里，并且能用断言把不变量钉死：
///   ① 画出来的图片**绝不超过取景框**（超出了就一定会出滚动条）；
///   ② 保持宽高比（不变形）；
///   ③ 退化输入（0/负/NaN）也给出有限、正的尺寸，绝不能把界面搞崩。
/// </summary>
public static class GridLayout
{
    /// <summary>缩略图取景框的高宽比（统一比例 → 所有卡片一样高，网格才整齐）。</summary>
    public const float ThumbAspect = 0.56f;

    /// <summary>几个像素的富余：宁可高一点，也不要为了差 1px 又长出滚动条。</summary>
    public const float Slack = 3f;

    /// <summary>缩略图取景框的宽高比上限之外的兜底最小值（防止卡片被拖到极窄时算出 0）。</summary>
    private const float MinThumbW = 24f;

    /// <summary>子窗口内部可用宽度的扣减量：内边距 + 边框。</summary>
    public static float InnerPadX(float windowPadX, float borderSize)
        => MathF.Max(0f, Safe(windowPadX, 0f) * 2f + Safe(borderSize, 0f) * 2f);

    /// <summary>子窗口内部可用高度的扣减量。</summary>
    public static float InnerPadY(float windowPadY, float borderSize)
        => InnerPadX(windowPadY, borderSize);

    /// <summary>缩略图取景框（宽 × 高）。宽度**永远不超过**卡片宽度。</summary>
    public static Vector2 ThumbBox(float cardW, float innerPadX)
    {
        var w = MathF.Max(MinThumbW, Safe(cardW, MinThumbW) - MathF.Max(0f, Safe(innerPadX, 0f)));
        return new Vector2(w, w * ThumbAspect);
    }

    /// <summary>
    /// 按容器等比缩放（contain）：结果**两个方向都不超过容器**，且保持宽高比。
    /// 退化输入返回容器尺寸本身，保证调用方拿到的永远是有限正数。
    /// </summary>
    public static Vector2 Fit(Vector2 src, float maxW, float maxH)
    {
        var boxW = Safe(maxW, 1f);
        var boxH = Safe(maxH, 1f);
        if (!IsUsable(src.X) || !IsUsable(src.Y)) return new Vector2(boxW, boxH);

        var k = MathF.Min(boxW / src.X, boxH / src.Y);
        if (!IsUsable(k)) return new Vector2(boxW, boxH);
        return new Vector2(src.X * k, src.Y * k);
    }

    /// <summary>
    /// 卡片总高 = 内部上下留白 + 取景框 + 一行按钮 + 一行文字 + 富余。
    /// 必须**覆盖**内容，否则卡片内部的滚动条又会回来。
    /// </summary>
    public static float CardHeight(float innerPadY, float thumbH, float frameRowH, float textRowH)
        => MathF.Max(0f, Safe(innerPadY, 0f)) + MathF.Max(0f, Safe(thumbH, 0f)) +
           MathF.Max(0f, Safe(frameRowH, 0f)) + MathF.Max(0f, Safe(textRowH, 0f)) + Slack;

    /// <summary>
    /// 给「宽度为 widgetW 的控件」算它需要的**列宽**：控件宽 + 单元格两侧内边距。
    ///
    /// 这条规则今天被踩了两次：ImGui 表格里**列宽 ≠ 内容区宽度**，
    /// 内容区 = 列宽 − 2×CellPadding.X。把 26px 的按钮放进 30px 的列、
    /// 68px 的图放进 74px 的列，内容区其实只有 22 / 66 —— 结果就是溢出与滚动条。
    /// </summary>
    public static float ColumnWidthFor(float widgetW, float cellPadX)
        => MathF.Max(0f, Safe(widgetW, 0f)) + MathF.Max(0f, Safe(cellPadX, 0f));

    private static bool IsUsable(float v) => !float.IsNaN(v) && !float.IsInfinity(v) && v > 0f;

    private static float Safe(float v, float fallback) => IsUsable(v) ? v : fallback;
}
