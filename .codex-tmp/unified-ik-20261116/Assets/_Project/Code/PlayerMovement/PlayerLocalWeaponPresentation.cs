using RPG4.PlayerIntent;

namespace RPG4.PlayerMovement
{
    /// <summary>Local-only projection. These values never enter authoritative simulation or input replay.</summary>
    public readonly struct PlayerLocalWeaponPresentation
    {
        public readonly bool Active, Reloading, IsAiming;
        public readonly ulong WeaponId, ShotSequence, ShotCueSequence, RequestId;
        public readonly uint Ordinal;
        public readonly WeaponActionSnapshot Action;
        public readonly UpperBodyDemand Demand;
        public readonly float ReloadPhase, ShotDuration, FadeDuration, ReloadDuration;

        internal PlayerLocalWeaponPresentation(bool active, ulong weaponId, ulong sequence, ulong requestId,
            uint ordinal, WeaponActionSnapshot action, UpperBodyDemand demand, bool reloading, float reloadPhase,
            float shotDuration, float fadeDuration, float reloadDuration, bool aiming)
        {
            Active = active; WeaponId = weaponId; ShotSequence = ShotCueSequence = sequence;
            RequestId = requestId; Ordinal = ordinal; Action = action; Demand = demand;
            Reloading = reloading; ReloadPhase = reloadPhase; ShotDuration = shotDuration;
            FadeDuration = fadeDuration; ReloadDuration = reloadDuration; IsAiming = aiming;
        }
    }

    public readonly struct PlayerAnticipatedShot
    {
        public readonly ulong RequestId, WeaponId, CueSequence;
        public readonly uint Ordinal;
        internal PlayerAnticipatedShot(ulong requestId, uint ordinal, ulong weaponId, ulong cueSequence)
        { RequestId = requestId; Ordinal = ordinal; WeaponId = weaponId; CueSequence = cueSequence; }
    }
}
