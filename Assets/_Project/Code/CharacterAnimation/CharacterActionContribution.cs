using UnityEngine;

namespace RPG4.CharacterAnimation
{
    /// <summary>A resolved animation contribution, including a fading channel's retained action and actual weight.</summary>
    public readonly struct CharacterActionContribution
    {
        public readonly string Id;
        public readonly float Weight, Phase;
        public readonly bool FadingOut, WeaponRelated, AimParticipant;
        public readonly CharacterActionIdentity Identity;
        private CharacterActionContribution(in CharacterActionContribution source, float weight)
        {
            Id = source.Id; Weight = Mathf.Clamp01(weight); Phase = source.Phase;
            Identity = source.Identity; WeaponRelated = source.WeaponRelated; AimParticipant = source.AimParticipant; FadingOut = true;
        }
        internal CharacterActionContribution Fade(float weight) => new CharacterActionContribution(this, Mathf.Min(Weight, weight));
        public CharacterActionContribution(string id, float weight, float phase, CharacterActionIdentity identity = default,
            bool fadingOut = false, bool weaponRelated = false, bool aimParticipant = false)
        {
            Id = id; Weight = Mathf.Clamp01(weight); Phase = Mathf.Clamp01(phase); Identity = identity;
            FadingOut = fadingOut; WeaponRelated = weaponRelated; AimParticipant = aimParticipant;
        }

    }

}
