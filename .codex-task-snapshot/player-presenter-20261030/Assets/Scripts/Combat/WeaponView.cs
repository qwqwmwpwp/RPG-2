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

        [Header("Optional magazine model")]
        [SerializeField] private GameObject magazineModelPrefab;

        private RuntimeItem boundItem;
        private ItemId magazineItemId;
        private ItemId muzzleAttachmentItemId;
        private GameObject magazineModel;
        private GameObject muzzleAttachmentModel;
        private WeaponAttachmentView activeAttachmentView;
        private ulong lastPlayedSequence;

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
            ClearModels();
            boundItem = item;
            lastPlayedSequence = item.WeaponState.ShotSequence;
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
            RuntimeItem magazine = item.WeaponState.Magazine;
            ItemId newMagazineId = magazine != null ? magazine.ItemId : ItemId.None;
            if (newMagazineId != magazineItemId)
            {
                DestroyModel(ref magazineModel);
                magazineItemId = newMagazineId;
                if (newMagazineId.IsValid && magazineModelPrefab != null && magazineSocket != null)
                    magazineModel = InstantiateAtSocket(magazineModelPrefab, magazineSocket);
            }

            item.WeaponState.Attachments.TryGetValue(WeaponAttachmentSlot.Muzzle, out RuntimeItem muzzleAttachment);
            ItemId newAttachmentId = muzzleAttachment != null ? muzzleAttachment.ItemId : ItemId.None;
            if (newAttachmentId != muzzleAttachmentItemId)
            {
                DestroyModel(ref muzzleAttachmentModel);
                muzzleAttachmentItemId = newAttachmentId;
                activeAttachmentView = null;

                GameObject model = muzzleAttachment?.GetFragment<AttachmentDefinitionFragment>()?.Model;
                if (newAttachmentId.IsValid && model != null && muzzle != null)
                {
                    muzzleAttachmentModel = InstantiateAtSocket(model, muzzle);
                    activeAttachmentView = muzzleAttachmentModel.GetComponentInChildren<WeaponAttachmentView>(true);
                }
            }
        }

        /// <summary>播放一次新射击的表现；重复或已同步的序号不会补播。</summary>
        public void PlayShot(WeaponShot shot)
        {
            if (boundItem == null || shot.WeaponId != boundItem.ItemId || shot.Sequence <= lastPlayedSequence)
                return;

            lastPlayedSequence = shot.Sequence;
            try
            {
                RestartParticle(muzzleFlash, Muzzle);
                RestartParticle(shellEjectionEffect, shellEjectionPoint);
                if (shotAudioSource != null && shotAudioClip != null)
                    shotAudioSource.PlayOneShot(shotAudioClip);
            }
            catch (System.Exception exception)
            {
                Debug.LogException(exception, this);
            }
        }

        public void Unbind()
        {
            if (boundItem != null) boundItem.Changed -= OnItemChanged;
            boundItem = null;
            lastPlayedSequence = 0;
            if (muzzleFlash != null) muzzleFlash.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            if (shellEjectionEffect != null) shellEjectionEffect.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            if (shotAudioSource != null) shotAudioSource.Stop();
            ClearModels();
        }

        private void OnItemChanged(RuntimeItem item) => ApplyItemState(item);

        private void ClearModels()
        {
            DestroyModel(ref magazineModel);
            DestroyModel(ref muzzleAttachmentModel);
            activeAttachmentView = null;
            magazineItemId = ItemId.None;
            muzzleAttachmentItemId = ItemId.None;
        }

        private static GameObject InstantiateAtSocket(GameObject prefab, Transform socket)
        {
            GameObject instance = Instantiate(prefab, socket, false);
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one;
            return instance;
        }

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

        private static void DestroyModel(ref GameObject model)
        {
            if (model == null) return;
            model.SetActive(false);
            if (Application.isPlaying) Destroy(model);
            else DestroyImmediate(model);
            model = null;
        }

        private void OnDestroy()
        {
            if (boundItem != null) boundItem.Changed -= OnItemChanged;
        }
    }
}
