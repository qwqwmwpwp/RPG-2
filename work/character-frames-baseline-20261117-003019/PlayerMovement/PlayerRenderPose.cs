using UnityEngine;

namespace RPG4.PlayerMovement
{
    /// <summary>物理纠错立即完成；只为本地可见子树保存和衰减纠错差值。</summary>
    internal sealed class PlayerRenderPose
    {
        private readonly Transform owner, visual;
        private readonly OwnerCorrectionPresentationData settings;
        private readonly Vector3 neutralPosition;
        private readonly Quaternion neutralRotation;
        private Vector3 positionOffset, beforePosition, beforeOwnerPosition;
        private float yawOffset, beforeYaw, beforeOwnerYaw;
        private bool capturing;
        private int advancedFrame = -1;
        internal bool Available => visual != null && visual != owner && visual.parent == owner;

        internal PlayerRenderPose(Transform owner, Transform visual, OwnerCorrectionPresentationData settings)
        {
            this.owner = owner; this.visual = visual; this.settings = settings;
            if (!Available) return;
            neutralPosition = visual.localPosition;
            neutralRotation = visual.localRotation;
        }

        internal void BeforeReconcile()
        {
            if (!Available || capturing) return;
            beforePosition = visual.position;
            beforeYaw = visual.eulerAngles.y;
            beforeOwnerPosition = owner.position;
            beforeOwnerYaw = owner.eulerAngles.y;
            capturing = true;
        }

        internal bool AfterReconcile(bool discontinuity)
        {
            if (!Available) return false;
            if (!capturing) { Reset(); return true; }
            capturing = false;
            Vector3 correction = owner.position - beforeOwnerPosition;
            float correctionYaw = Mathf.DeltaAngle(beforeOwnerYaw, owner.eulerAngles.y);
            positionOffset = beforePosition - owner.TransformPoint(neutralPosition);
            yawOffset = Mathf.DeltaAngle((owner.rotation * neutralRotation).eulerAngles.y, beforeYaw);
            bool reset = discontinuity || settings.HalfLife <= 0f
                || correction.sqrMagnitude > settings.MaximumDistance * settings.MaximumDistance
                || positionOffset.sqrMagnitude > settings.MaximumDistance * settings.MaximumDistance
                || Mathf.Abs(correctionYaw) > settings.MaximumYaw || Mathf.Abs(yawOffset) > settings.MaximumYaw;
            if (reset) Reset();
            else Apply();
            return reset;
        }

        internal void Advance(float deltaTime)
        {
            if (!Available) return;
            if (deltaTime > 0f && advancedFrame != Time.frameCount)
            {
                advancedFrame = Time.frameCount;
                float retain = settings.HalfLife <= 0f ? 0f
                    : Mathf.Exp(-Mathf.Log(2f) * deltaTime / settings.HalfLife);
                positionOffset *= retain;
                yawOffset *= retain;
                if (positionOffset.sqrMagnitude < 0.00000001f) positionOffset = Vector3.zero;
                if (Mathf.Abs(yawOffset) < 0.001f) yawOffset = 0f;
            }
            Apply();
        }

        internal void Reset()
        {
            positionOffset = Vector3.zero;
            yawOffset = 0f;
            capturing = false;
            advancedFrame = -1;
            Apply();
        }

        private void Apply()
        {
            if (!Available) return;
            visual.localPosition = neutralPosition + owner.InverseTransformVector(positionOffset);
            visual.localRotation = Quaternion.Euler(0f, yawOffset, 0f) * neutralRotation;
        }
    }
}
