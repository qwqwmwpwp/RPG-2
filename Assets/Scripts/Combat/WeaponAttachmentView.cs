using UnityEngine;

namespace RPG4.Combat
{
    /// <summary>可选的枪口配件发射点覆盖。</summary>
    [DisallowMultipleComponent]
    public sealed class WeaponAttachmentView : MonoBehaviour
    {
        [SerializeField] private Transform muzzleOverride;

        public Transform MuzzleOverride => muzzleOverride;
    }
}
