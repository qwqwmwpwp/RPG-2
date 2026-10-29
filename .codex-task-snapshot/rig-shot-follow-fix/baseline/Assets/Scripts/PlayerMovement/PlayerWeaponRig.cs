using RPG4.PlayerIntent;
using UnityEngine;
using UnityEngine.Animations.Rigging;

namespace RPG4.PlayerMovement
{
    /// <summary>玩家武器的挂点事件适配；握持许可由黑板决定，手臂只由 Rig 求解。</summary>
    [DisallowMultipleComponent]
    public sealed class PlayerWeaponRig : MonoBehaviour
    {
        [SerializeField] private MultiParentConstraint weaponSocket;
        [SerializeField] private TwoBoneIKConstraint supportHand;
        private PlayerBlackboard blackboard;
        private bool wasHolding;
        private bool wasReloading;
        private float eventGripWeight = 1f;
        private int gripFrame = -1;

        internal void Initialize(PlayerBlackboard sharedBlackboard)
        {
            blackboard = sharedBlackboard;
            SelectSocket(0);
            ApplyPresentation();
        }
        internal bool HasGrip => supportHand != null && supportHand.weight > 0f;
        internal void ApplyPresentation()
        {
            bool holding = blackboard != null && blackboard.SupportHandWeight > 0f;
            bool reloading = blackboard != null && blackboard.Reloading;
            if (holding && (!wasHolding || wasReloading))
            {
                SelectSocket(0);
                eventGripWeight = 1f;
            }
            wasHolding = holding;
            wasReloading = reloading;
            if (supportHand == null) return;
            float target = isActiveAndEnabled && holding ? blackboard.SupportHandWeight * eventGripWeight : 0f;
            if (target <= 0f)
            {
                supportHand.weight = 0f;
                gripFrame = -1;
            }
            else if (gripFrame != Time.frameCount)
            {
                // 延用原握持混合速度；同帧动画事件不重复推进，换弹和输入中断立即释放。
                supportHand.weight = Mathf.Lerp(supportHand.weight, target, Mathf.Clamp01(Time.deltaTime * 15f));
                gripFrame = Time.frameCount;
            }
        }
        public void SwitchSocket(AnimationEvent animationEvent)
        {
            if (animationEvent != null) SwitchSocketByString(animationEvent.stringParameter);
        }
        public void SwitchSocketByString(string commands)
        {
            if (string.IsNullOrEmpty(commands)) return;
            foreach (string raw in commands.Split(','))
            {
                switch (raw.Trim())
                {
                    case "To_Hand_R_Socket": SelectSocket(0); break;
                    case "To_Put_Socket_Rifle": SelectSocket(1); break;
                    case "To_add_weapon_r": SelectSocket(2); break;
                    case "IK_OFF_Left_Handle": eventGripWeight = 0f; break;
                    case "IK_ON_Left_Handle": eventGripWeight = 1f; break;
                }
            }
            ApplyPresentation();
        }
        private void SelectSocket(int selected)
        {
            if (weaponSocket == null) return;
            var sources = weaponSocket.data.sourceObjects;
            if (selected < 0 || selected >= sources.Count) return;
            for (int i = 0; i < sources.Count; i++) sources.SetWeight(i, i == selected ? 1f : 0f);
            weaponSocket.data.sourceObjects = sources;
        }
        internal void ReleaseGrip()
        {
            if (supportHand != null) supportHand.weight = 0f;
            wasHolding = wasReloading = false;
            eventGripWeight = 1f;
            gripFrame = -1;
        }
        private void OnDisable() => ReleaseGrip();
    }
}
