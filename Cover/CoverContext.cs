namespace XmaBrowser.Cover;

/// <summary>
/// 封面绘制只需要 Penumbra 的 mod 根目录 —— 抽成窄接口，避免 Cover 模块反向依赖 PenumbraBridge 的具体类型。
/// PenumbraBridge 实现它。
///
/// 注意：**不要**把命名空间叫成 XmaBrowser.Penumbra —— 那会遮住外部的 Penumbra.Api 根命名空间，
/// 让 `Penumbra.Api.IpcSubscribers.…` 全部解析失败（已实测踩过）。
/// </summary>
public interface CoverContext
{
    string ModDirectory { get; }
}
