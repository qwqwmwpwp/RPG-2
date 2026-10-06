using System;

namespace RPG4.Backpack.Core
{
    /// <summary>日志模块分类（位掩码）：与 <see cref="LogSettings.EnabledModules"/> 配合按模块开关日志，模块划分与程序集分层一致。</summary>
    [Flags]
    public enum LogModule
    {
        None = 0, // 无模块（等价于全部关闭）。

        Inventory = 1 << 0, // 库存系统。

        Equipment = 1 << 1, // 装备系统。

        UI = 1 << 2, // 用户界面。

        Network = 1 << 3, // 网络复制。

        Interaction = 1 << 4, // 交互 / 拾取。

        Combat = 1 << 5, // 战斗系统。

        SaveLoad = 1 << 6, // 存档 / 读档。

        All = ~0, // 全部模块。
    }
}
