// 这个文件曾经声明 namespace XmaBrowser.Penumbra / interface CoverContext。
//
// 已废弃：`XmaBrowser.Penumbra` 会**遮住外部的 `Penumbra.Api` 根命名空间**，
// 导致 `Penumbra.Api.IpcSubscribers.*` 全部解析失败（实测报 CS0234）。
// 接口已移到 `XmaBrowser.Cover.CoverContext`（见 ../Cover/CoverContext.cs）。
//
// 这里刻意不再声明任何命名空间与类型 —— 文件留着只是为了让「曾用过这个路径」的人看得到原因。
// 可以直接删除本文件，不影响编译。
