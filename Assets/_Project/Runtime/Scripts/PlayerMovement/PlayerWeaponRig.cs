using RPG4.Combat;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Animations.Rigging;

namespace RPG4.PlayerMovement
{
    /// <summary>玩家武器的挂点事件适配；握持目标由桥接提交，手臂只由 Rig 求解。</summary>
    [DisallowMultipleComponent]
    public sealed class PlayerWeaponRig : MonoBehaviour
    {
        [SerializeField] private MultiParentConstraint weaponSocket;
        [SerializeField] private TwoBoneIKConstraint supportHand;
        [SerializeField] private Transform equipmentCarrier;
        private GameObject equipmentModel;
        private Transform equipmentGrip;
        private bool equipmentPrepared;
        private PlayerPoseTarget poseTarget;
        private bool wasHolding;
        private bool wasReloading;
        private float eventGripWeight = 1f;
        private int gripFrame = -1;

        internal bool CanBindEquipment => equipmentCarrier != null && supportHand != null
            && supportHand.data.target != null;
        internal bool HasEquipment => equipmentModel != null;
        internal void PrepareEquipment()
        {
            if (equipmentPrepared || equipmentCarrier == null) return;
            equipmentPrepared = true;
            // 保留已绑定的根和 IK target，只隐藏原有模型。
            foreach (Renderer renderer in equipmentCarrier.GetComponentsInChildren<Renderer>(true))
                renderer.enabled = false;
        }
        internal bool CanPrepareEquipment(GameObject prefab, out string error)
        {
            error = null;
            if (!CanBindEquipment) error = "角色未配置稳定武器挂点与左手目标。";
            else if (prefab == null) error = "物品缺少装备模型。";
            else
            {
                WeaponView view = prefab.GetComponentInChildren<WeaponView>(true);
                if (view == null || view.Muzzle == null)
                {
                    error = "武器模型缺少 WeaponView 或枪口挂点。";
                    return false;
                }
                if (view.SupportHandGrip != null) return true;
                foreach (Transform candidate in prefab.GetComponentsInChildren<Transform>(true))
                    if (candidate.name == "L_Hand_Position") return true;
                error = "武器模型缺少 L_Hand_Position 握点。";
            }
            return false;
        }
        internal bool TryPrepareEquipment(GameObject prefab, out GameObject model, out string error)
        {
            model = null;
            if (!CanPrepareEquipment(prefab, out error)) return false;
            try
            {
                // 非激活的候选根让克隆在确认阶段不触发 OnEnable；Changed 后才启用。
                model = new GameObject(prefab.name + " (Equipment)");
                model.SetActive(false);
                model.transform.SetParent(equipmentCarrier, false);
                GameObject instance = Instantiate(prefab, model.transform, false);
                instance.transform.localPosition = Vector3.zero;
                instance.transform.localRotation = Quaternion.identity;
                instance.SetActive(true);
                foreach (ParentConstraint constraint in model.GetComponentsInChildren<ParentConstraint>(true))
                    constraint.enabled = false;
                model.transform.localPosition = Vector3.zero;
                model.transform.localRotation = Quaternion.identity;
                WeaponView view = model.GetComponentInChildren<WeaponView>(true);
                if (view != null && view.SupportHandGrip != null) return true;
                foreach (Transform candidate in model.GetComponentsInChildren<Transform>(true))
                    if (candidate.name == "L_Hand_Position") return true;
                error = "武器模型缺少 L_Hand_Position 握点。";
            }
            catch (System.Exception exception) { error = exception.Message; }
            if (model != null) Destroy(model);
            model = null;
            return false;
        }
        internal void BindEquipment(GameObject preparedModel)
        {
            PrepareEquipment();
            UnbindEquipment();
            equipmentModel = preparedModel;
            WeaponView view = equipmentModel.GetComponentInChildren<WeaponView>(true);
            equipmentGrip = view != null ? view.SupportHandGrip : null;
            if (equipmentGrip == null)
                foreach (Transform candidate in equipmentModel.GetComponentsInChildren<Transform>(true))
                    if (candidate.name == "L_Hand_Position") { equipmentGrip = candidate; break; }
            equipmentModel.SetActive(true);
            UpdateGripTarget();
        }
        internal void UnbindEquipment()
        {
            ReleaseGrip();
            equipmentGrip = null;
            if (equipmentModel == null) return;
            equipmentModel.SetActive(false);
            Destroy(equipmentModel);
            equipmentModel = null;
        }
        private void UpdateGripTarget()
        {
            if (equipmentGrip == null || supportHand == null || supportHand.data.target == null) return;
            Transform target = supportHand.data.target;
            // target 是稳定 carrier 的子节点；在动画流求值前提交模型握点的局部姿态。
            target.SetPositionAndRotation(equipmentGrip.position, equipmentGrip.rotation);
        }

        internal void Initialize() => SelectSocket(0);
        internal void RestoreReloadPose(AnimationClip clip, float phase)
        {
            SwitchSocketByString("To_Hand_R_Socket,IK_ON_Left_Handle");
            if (clip == null) return;
            float elapsed = Mathf.Clamp01(phase) * clip.length;
            // 从素材事件恢复被跳过的挂点；只重建表现，不重放换弹事务。
            foreach (AnimationEvent entry in clip.events)
                if (entry.time <= elapsed && (entry.functionName == "SwitchSocket"
                    || entry.functionName == "SwitchSocketByString"))
                    SwitchSocketByString(entry.stringParameter);
        }
        internal void SetVisible(bool visible)
        {
            if (equipmentModel != null) equipmentModel.SetActive(visible);
        }
        internal bool HasGrip => supportHand != null && supportHand.weight > 0f;
        internal void ApplyPresentation(PlayerPoseTarget target, float deltaTime, int frameId)
        {
            poseTarget = target;
            ApplyGrip(deltaTime, frameId);
        }
        private void ApplyGrip(float deltaTime, int frameId)
        {
            UpdateGripTarget();
            bool holding = poseTarget.SupportHandWeight > 0f;
            bool reloading = poseTarget.Reloading;
            // 瞄准/射击期间由状态持有右手挂点；换弹时才允许动画事件接管其他挂点。
            if (holding) SelectSocket(0);
            if (holding && (!wasHolding || wasReloading))
            {
                eventGripWeight = 1f;
            }
            wasHolding = holding;
            wasReloading = reloading;
            if (supportHand == null) return;
            float target = isActiveAndEnabled && holding ? poseTarget.SupportHandWeight * eventGripWeight : 0f;
            if (target <= 0f)
            {
                supportHand.weight = 0f;
                gripFrame = -1;
            }
            else if (gripFrame != frameId)
            {
                // 延用原握持混合速度；同帧动画事件不重复推进，换弹和输入中断立即释放。
                supportHand.weight = Mathf.Lerp(supportHand.weight, target, Mathf.Clamp01(deltaTime * 15f));
                if (deltaTime > 0f) gripFrame = frameId;
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
            ApplyGrip(Time.deltaTime, Time.frameCount);
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
            poseTarget = default;
            if (supportHand != null) supportHand.weight = 0f;
            wasHolding = wasReloading = false;
            eventGripWeight = 1f;
            gripFrame = -1;
        }
        private void OnDisable() => ReleaseGrip();
    }
}
