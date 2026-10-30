namespace RPG4.Backpack.Core
{
    // 事件频道键：键格式「域.对象.动作」（Inv.* 状态通知、Cmd.* 意图命令、Ui.* 控件事件）；键只能来自本文件，业务代码禁止裸字符串字面量；Publish 与 Subscribe 的泛型实参必须与各常量注释中的载荷签名一致，不一致由 EventBus 记 Warn 并跳过。

    /// <summary>数据层 → UI 状态广播：既成事实通知，UI 只做表现同步。</summary>
    public static class InvChannels
    {
        public const string ItemAdded = "Inv.Item.Added"; // 新增物品条目。载荷 (item)。

        public const string StackChanged = "Inv.Stack.Changed"; // 堆叠数量变化。载荷 (SlotAvailabilityResult)，值传递。

        public const string ItemRemoved = "Inv.ItemRemoved"; // 移除物品条目。载荷 (item)；M2 移除路径发送。

        public const string ItemsChanged = "Inv.Items.Changed"; // (ItemId[] ids)，紧邻 Changed 前发布；null 表示全量失效，接收方只读取快照。

        public const string Changed = "Inv.Changed"; // 粗粒度「数据已变」脏信号，无载荷；表现层收到后置脏标记、帧末合并刷新一次，不区分具体变了什么。

        public const string OperationCompleted = "Inv.Operation.Completed"; // 完整库存操作结果。载荷 (InventoryOperationResult)，source 为来源库存的事件键。

        public const string WeaponOperationCompleted = "Inv.Weapon.OperationCompleted"; // (WeaponOperationResult)，玩家背包 EventSource。
        public const string WeaponHudChanged = "Inv.Weapon.HudChanged"; // (WeaponHudSnapshot)，玩家背包 EventSource。
        public const string WeaponActionChanged = "Inv.Weapon.ActionChanged"; // 无载荷，source 为 Weapon 实例；玩家适配器合并为只读 HUD 时间轴。
        public const string WeaponInspectionChanged = "Inv.Weapon.InspectionChanged"; // (WeaponInspectionSnapshot)，玩家背包 EventSource。
    }

    /// <summary>交互意图命令：发送方只声明意图，不关心谁处理；命令不保证成功，库存操作结果由 <see cref="InvChannels"/> 的状态广播回传。</summary>
    public static class CmdChannels
    {
        public const string OpenChestUI = "Cmd.Chest.OpenUI"; // 请求打开箱子窗口。载荷 (ChestInventory)，source 为发起交互的箱子；库存 UI 协调器按通配来源订阅。

        public const string InventoryOperation = "Cmd.Inventory.Operation"; // 完整库存意图。载荷 (InventoryOperation)。

        public const string WeaponOperation = "Cmd.Weapon.Operation"; // (WeaponOperation)，玩家背包 EventSource，单处理者执行。
        public const string WeaponSnapshot = "Cmd.Weapon.Snapshot"; // (WeaponSnapshotRequest)，玩家背包 EventSource，None 取消检查。

        public const string DropItem = "Cmd.Item.Drop"; // 丢弃物品。载荷 (item, int stackCount)。

        public const string ConsumeItem = "Cmd.Item.Consume"; // 消耗物品。载荷 (item, int stackCount)，数量范围 1 .. 当前堆叠数。

        public const string EquipSlotClicked = "Cmd.EquipSlot.Clicked"; // 装备槽点击。载荷 (itemToEquip, itemToUnequip)。
    }

    /// <summary>UI 内部事件：发送方以自身组件实例作 source key，取代父子控件互相持引用；不经过数据层。</summary>
    public static class UiChannels
    {
        public const string WeaponCandidateSelected = "Ui.Weapon.CandidateSelected"; // (int rowIndex)，source 为 WeaponInspectionPanel。
        /// <summary>鼠标键编码：只传「哪个键被按下」，不传 UI 事件对象自身。</summary>
        public static class MouseButton
        {
            public const int Left = 0; // 左键。

            public const int Right = 1; // 右键。

            public const int Other = 2; // 其他键。
        }

        public const string GridSlotClicked = "Ui.GridSlot.Clicked"; // 网格格子点击。载荷 (int tileIndex, int mouseButton)，取值见 MouseButton。

        public const string EquippedGridSlotClicked = "Ui.EquippedGridSlot.Clicked"; // 已装备槽点击。载荷 (gridSlot, BackpackTag equipmentType)。

        public const string PopupSplit = "Ui.Popup.Split"; // 右键弹窗拆分。载荷 (int index, int amount)。
    }

    // 未启用的频道（用到再加，避免死键）：Inv.NoRoom / Inv.PickupPrompt（M4 拾取提示）/ Inv.Equipment.Changed（M7 装备状态）/ Ui.GridSlotHovered 等。
}
