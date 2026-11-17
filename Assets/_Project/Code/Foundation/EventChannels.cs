namespace RPG4.Backpack.Core
{
    public static class InvChannels
    {
        public const string WeaponHudChanged = "Inv.Weapon.HudChanged";
        public const string WeaponActionChanged = "Inv.Weapon.ActionChanged";
        public const string WeaponOperationResult = "Inv.Weapon.OperationResult";
    }
    public static class CmdChannels
    {
        public const string WeaponSnapshot = "Cmd.Weapon.Snapshot";
        public const string WeaponOperation = "Cmd.Weapon.Operation";
    }
    /// <summary>独立装备模板。控制器端点与交互会话均使用引用作为 source，避免不同窗口串台。</summary>
    public static class LoadoutChannels
    {
        public const string RoleConnectRequested = "Loadout.Role.ConnectRequested"; // (LoadoutRoleConnectRequest)，发现阶段使用 AnySource 订阅并按 OwnerKey 筛选。
        public const string RoleConnected = "Loadout.Role.Connected"; // (LoadoutRoleConnectReply)，source 为请求的 ReplySource。
        public const string RoleDisconnected = "Loadout.Role.Disconnected"; // 无载荷，source 为角色连接提供方的事件地址。
        public const string RoleAvailable = "Loadout.Role.Available"; // (string ownerKey)，发现阶段角色提供方就绪通知。
        public const string DragConnectRequested = "Loadout.Drag.ConnectRequested"; // (LoadoutDragConnectRequest)，发现阶段使用 AnySource 订阅并按 OwnerKey 筛选。
        public const string DragConnected = "Loadout.Drag.Connected"; // (LoadoutDragConnectReply)，source 为请求的 ReplySource。
        public const string SnapshotRequested = "Loadout.Snapshot.Requested"; // (LoadoutSnapshotRequest)，source 为数据端点。
        public const string SnapshotReceived = "Loadout.Snapshot.Received"; // (LoadoutSnapshotReply)，source 为请求的 ReplySource。
        public const string Changed = "Loadout.Changed"; // 无载荷，source 为数据端点。
        public const string OperationRequested = "Loadout.Operation.Requested"; // (LoadoutOperationRequest)，source 为来源数据端点。
        public const string OperationCompleted = "Loadout.Operation.Completed"; // (LoadoutOperationResult)，source 为交互会话。
        public const string Input = "Loadout.Ui.Input"; // (LoadoutInteractionInput)，source 为交互会话。
        public const string Visual = "Loadout.Ui.Visual"; // (LoadoutVisualCommand)，source 为交互会话。
        public const string Target = "Loadout.Ui.Target"; // (LoadoutTargetReport)，source 为交互会话。
        public const string SessionClosed = "Loadout.Session.Closed"; // 无载荷，source 为交互会话。
        public const string WindowRequested = "Loadout.Window.Requested"; // (bool open)，source 为 UI 管理器创建的窗口地址。
        public const string WindowChanged = "Loadout.Window.Changed"; // (bool open)，source 为 UI 管理器创建的窗口地址。
        public const string WindowStateRequested = "Loadout.Window.StateRequested"; // 无载荷，source 为 UI 管理器创建的窗口地址。
    }

}
