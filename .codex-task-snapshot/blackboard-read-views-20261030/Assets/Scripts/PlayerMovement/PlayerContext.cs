using RPG4.PlayerIntent;
using RPG4.Backpack.Equipment;

namespace RPG4.PlayerMovement
{
    /// <summary>状态机的结构引用；行为数据统一从该角色黑板读取。</summary>
    internal sealed class PlayerContext
    {
        internal PlayerBlackboard Blackboard { get; }
        internal PlayerStateData Data { get; }
        internal PlayerContentData Content => Data.Content;
        internal PlayerIntentManager Intents { get; set; }
        internal PlayerEquipment Equipment { get; set; }
        internal Weapon Weapon => Equipment != null ? Equipment.CurrentWeapon : null;
        internal UpperBodyDemand WeaponDemand => Blackboard.WeaponDemand;
        internal LocomotionPlan Plan => Blackboard.Plan;
        internal bool AnimatorIsAvailable => Content.Animator != null
            && Content.Animator.runtimeAnimatorController != null && Content.Animator.isActiveAndEnabled;
        internal bool CanControl => Blackboard.CanMove;
        internal bool CanUseWeapon => Blackboard.CanUseWeapon && Weapon != null;
        internal PlayerContext(PlayerBlackboard blackboard, PlayerStateData data)
        {
            Blackboard = blackboard;
            Data = data;
        }
    }
}
