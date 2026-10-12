using System;

namespace XmaBrowser.Ui;

/// <summary>
/// 界面缩放（字体 + 控件一起缩）的取值规则。纯逻辑，可离线自测。
///
/// 只作用于**本插件的窗口**：ImGui 的 <c>FontGlobalScale</c> 是全局的，改它会连带影响
/// Dalamud 自己的界面和其它插件（用户会看到别处也变形），所以坚决不碰；这里用
/// <c>SetWindowFontScale</c> + 缩放 style 间距/内边距 + 缩放自己写的像素常量来实现。
/// 想要真正的「全局」缩放，那是 Dalamud 的设置（/xlsettings → 界面）。
/// </summary>
public static class UiScale
{
    public const float Min = 0.70f;
    public const float Max = 1.50f;
    public const float Default = 1.00f;

    /// <summary>浏览页左右分栏的默认比例（左 = 结果列表占的宽度比例）。</summary>
    public const float DefaultSplit = 0.50f;
    public const float MinSplit = 0.25f;
    public const float MaxSplit = 0.75f;

    public static float Clamp(float value)
        => float.IsNaN(value) || float.IsInfinity(value) ? Default : Math.Clamp(value, Min, Max);

    public static float ClampSplit(float value)
        => float.IsNaN(value) || float.IsInfinity(value) ? DefaultSplit : Math.Clamp(value, MinSplit, MaxSplit);

    /// <summary>给 UI 显示的百分比（如 "110%"）。</summary>
    public static string Describe(float value) => $"{MathF.Round(Clamp(value) * 100f):0}%";
}
