using RPG4.PlayerIntent;
using RPG4.Backpack.Equipment;

namespace RPG4.PlayerMovement
{
    /// <summary>状态机的结构引用；行为数据通过该角色的分类只读视图访问。</summary>
    internal sealed class PlayerContext
    {
        internal IPlayerReadState ReadState { get; }
        internal PlayerStateData Data { get; }
        internal PlayerContentData Content => Data.Content;
        internal PlayerIntentManager Intents { get; set; }
        internal PlayerEquipment Equipment { get; set; }
        internal Weapon Weapon => Equipment != null ? Equipment.CurrentWeapon : null;
        internal bool AnimatorIsAvailable => Content.Animator != null
            && Content.Animator.runtimeAnimatorController != null && Content.Animator.isActiveAndEnabled;
        internal bool CanUseWeapon => ReadState.Weapon.CanUseWeapon && Weapon != null;
        internal PlayerContext(IPlayerReadState readState, PlayerStateData data)
        {
            ReadState = readState;
            Data = data;
        }
    }
}
