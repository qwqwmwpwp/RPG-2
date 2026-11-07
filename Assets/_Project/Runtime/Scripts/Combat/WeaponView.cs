using RPG4.Backpack.Equipment;
using RPG4.Backpack.Items;
using UnityEngine;

namespace RPG4.Combat
{
    /// <summary>项目一个运行时武器物品的场景表现；状态仍由 RuntimeItem 持有。</summary>
    [DisallowMultipleComponent]
    public sealed class WeaponView : MonoBehaviour
    {
        [Header("Weapon mount points")]
        [SerializeField] private Transform muzzle;
        [SerializeField] private Transform supportHandGrip;
        [SerializeField] private Transform shellEjectionPoint;
        [SerializeField] private Transform magazineSocket;

        [Header("Optional shot effects")]
        [SerializeField] private ParticleSystem muzzleFlash;
        [SerializeField] private ParticleSystem shellEjectionEffect;
        [SerializeField] private AudioSource shotAudioSource;
        [SerializeField] private AudioClip shotAudioClip;

        [Header("Installed item models")]
        [SerializeField] private ItemAssemblyModelView assemblyModelView;

        private RuntimeItem boundItem;
        private WeaponAttachmentView activeAttachmentView;
        private ulong lastPlayedSequence;
        private ulong lastAnticipatedRequest;
        private uint lastAnticipatedOrdinal;
        private bool hasAnticipatedShot;

        public Transform Muzzle => activeAttachmentView != null && activeAttachmentView.MuzzleOverride != null
            ? activeAttachmentView.MuzzleOverride : muzzle;
        public Transform SupportHandGrip => supportHandGrip;
        public Transform ShellEjectionPoint => shellEjectionPoint;
        public Transform MagazineSocket => magazineSocket;

        /// <summary>绑定时跳过物品历史序号，避免旧的射击记录在重新装备时补播。</summary>
        public void Bind(RuntimeItem item)
        {
            if (item == null || item.WeaponState == null)
                throw new System.ArgumentException("WeaponView 需要绑定带武器状态的运行时物品。", nameof(item));

            if (boundItem != null) boundItem.Changed -= OnItemChanged;
            if (assemblyModelView == null) assemblyModelView = GetComponent<ItemAssemblyModelView>();
            assemblyModelView?.Clear();
            activeAttachmentView = null;
            boundItem = item;
            lastPlayedSequence = item.WeaponState.ShotSequence;
            hasAnticipatedShot = false;
            boundItem.Changed += OnItemChanged;
            ApplyItemState(item);
        }

        /// <summary>仅结构身份变化时重建弹匣或配件模型。</summary>
        public void ApplyItemState(RuntimeItem item)
        {
            if (item == null || item.WeaponState == null) return;
            if (boundItem == null || boundItem.ItemId != item.ItemId)
            {
                Bind(item);
                return;
            }

            if (!object.ReferenceEquals(boundItem, item))
            {
                boundItem.Changed -= OnItemChanged;
                boundItem = item;
                lastPlayedSequence = System.Math.Max(lastPlayedSequence, item.WeaponState.ShotSequence);
                boundItem.Changed += OnItemChanged;
            }

            try
            {
                ApplyItemStateModels(item);
            }
            catch (System.Exception exception)
            {
                Debug.LogException(exception, this);
            }
        }

        private void ApplyItemStateModels(RuntimeItem item)
        {
            if (assemblyModelView == null) assemblyModelView = GetComponent<ItemAssemblyModelView>();
            assemblyModelView?.Bind(item);
            RuntimeItem muzzleAttachment = item.Assembly.GetChild(ItemAssemblySlots.Muzzle);
            GameObject model = muzzleAttachment != null ? assemblyModelView?.GetModel(muzzleAttachment.ItemId) : null;
            activeAttachmentView = model != null ? model.GetComponentInChildren<WeaponAttachmentView>(true) : null;
        }

        /// <summary>播放一次新射击的表现；重复或已同步的序号不会补播。</summary>
        public void PlayShot(WeaponShot shot)
            => PlayShot(shot.WeaponId, shot.Sequence);

        /// <summary>桥接层按黑板事件提交；不需要持有可变武器或完整战斗对象。</summary>
        public void PlayShot(ItemId weaponId, ulong sequence)
        {
            if (boundItem == null || weaponId != boundItem.ItemId || sequence <= lastPlayedSequence)
                return;

            lastPlayedSequence = sequence;
            PlayShotEffects();
        }

        /// <summary>本地输入的即时表现使用独立身份，不推进服务器射击序号游标。</summary>
        public bool PlayAnticipatedShot(ItemId weaponId, ulong requestId, uint ordinal)
        {
            if (boundItem == null || weaponId != boundItem.ItemId || hasAnticipatedShot
                && (requestId < lastAnticipatedRequest || requestId == lastAnticipatedRequest && ordinal <= lastAnticipatedOrdinal))
                return false;
            hasAnticipatedShot = true;
            lastAnticipatedRequest = requestId;
            lastAnticipatedOrdinal = ordinal;
            return PlayShotEffects();
        }

        public void ResetAnticipatedShots() => hasAnticipatedShot = false;

        private bool PlayShotEffects()
        {
            try
            {
                RestartParticle(muzzleFlash, Muzzle);
                RestartParticle(shellEjectionEffect, shellEjectionPoint);
                if (shotAudioSource != null && shotAudioClip != null)
                    shotAudioSource.PlayOneShot(shotAudioClip);
                return true;
            }
            catch (System.Exception exception)
            {
                Debug.LogException(exception, this);
                return false;
            }
        }

        public void Unbind()
        {
            if (boundItem != null) boundItem.Changed -= OnItemChanged;
            boundItem = null;
            lastPlayedSequence = 0;
            hasAnticipatedShot = false;
            if (muzzleFlash != null) muzzleFlash.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            if (shellEjectionEffect != null) shellEjectionEffect.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            if (shotAudioSource != null) shotAudioSource.Stop();
            if (assemblyModelView != null) assemblyModelView.Clear();
            activeAttachmentView = null;
        }

        private void OnItemChanged(RuntimeItem item) => ApplyItemState(item);

        private static void RestartParticle(ParticleSystem effect, Transform point)
        {
            if (effect == null) return;
            if (point != null)
                effect.transform.SetPositionAndRotation(point.position, point.rotation);
            ParticleSystem.MainModule main = effect.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            effect.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            effect.Play(true);
        }

        private void OnDestroy()
        {
            if (boundItem != null) boundItem.Changed -= OnItemChanged;
        }
    }
}
