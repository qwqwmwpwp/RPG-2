using RPG4.PlayerIntent;
using RPG4.Backpack.Equipment;

namespace RPG4.PlayerMovement
{
    /// <summary>状态机的结构引用；行为数据通过该角色的分类只读视图访问。</summary>
    internal sealed class PlayerContext
    {
        // 唯一可写状态由每角色逻辑上下文创建；输入端只提交设备无关帧。
        internal Player Owner { get; set; }
        internal PlayerBlackboard Blackboard { get; } = new PlayerBlackboard();
        internal IPlayerReadState ReadState => Blackboard;
        internal PlayerStateData Data { get; }
        internal PlayerContentData Content => Data.Content;
        internal PlayerIntentManager Intents { get; set; }
        internal PlayerEquipment Equipment { get; set; }
        internal PlayerWorldAdapter World { get; set; }
        internal Weapon Weapon => Equipment != null ? Equipment.CurrentWeapon : null;
        internal bool CanUseWeapon => ReadState.Weapon.CanUseWeapon && Weapon != null;
        internal PlayerContext(PlayerStateData data)
        {
            Data = data;
        }
    }
}
