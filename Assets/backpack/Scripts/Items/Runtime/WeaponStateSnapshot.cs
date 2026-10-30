using System;
using System.Collections.Generic;
using RPG4.Backpack.Core;

namespace RPG4.Backpack.Items
{
    public sealed class MagazineStateSnapshot : IEquatable<MagazineStateSnapshot>
    {
        public IReadOnlyList<BackpackTag> Rounds { get; }
        public MagazineStateSnapshot(IEnumerable<BackpackTag> rounds)
        {
            if (rounds == null) throw new ArgumentNullException(nameof(rounds));
            var copy = new List<BackpackTag>();
            foreach (BackpackTag round in rounds)
            {
                if (copy.Count >= 256) throw new ArgumentException("弹匣状态超过256发。");
                WeaponDefinitionValidation.Tag(round, false, nameof(rounds));
                copy.Add(round);
            }
            Rounds = copy.AsReadOnly();
        }
        public bool Equals(MagazineStateSnapshot other)
        {
            if (other == null || Rounds.Count != other.Rounds.Count) return false;
            for (int i = 0; i < Rounds.Count; i++) if (!Rounds[i].Equals(other.Rounds[i])) return false;
            return true;
        }
        public override bool Equals(object obj) => Equals(obj as MagazineStateSnapshot);
        public override int GetHashCode()
        {
            int hash = 17;
            unchecked { foreach (BackpackTag round in Rounds) hash = hash * 31 + round.GetHashCode(); }
            return hash;
        }
    }

    public readonly struct AttachmentStateSnapshot
    {
        public WeaponAttachmentSlot Slot { get; }
        public RuntimeItemSnapshot Item { get; }
        public AttachmentStateSnapshot(WeaponAttachmentSlot slot, RuntimeItemSnapshot item) { Slot = slot; Item = item; }
    }

    public sealed class WeaponStateSnapshot : IEquatable<WeaponStateSnapshot>
    {
        public float Durability { get; }
        public BackpackTag ChamberAmmo { get; }
        public ulong ShotSequence { get; }
        public RuntimeItemSnapshot? Magazine { get; }
        public IReadOnlyList<AttachmentStateSnapshot> Attachments { get; }
        public WeaponStateSnapshot(float durability, BackpackTag chamberAmmo, ulong shotSequence,
            RuntimeItemSnapshot? magazine, IEnumerable<AttachmentStateSnapshot> attachments)
        {
            WeaponDefinitionValidation.Number(durability, 0f, nameof(durability));
            WeaponDefinitionValidation.Tag(chamberAmmo, true, nameof(chamberAmmo));
            if (attachments == null) throw new ArgumentNullException(nameof(attachments));
            var copy = new List<AttachmentStateSnapshot>();
            foreach (AttachmentStateSnapshot attachment in attachments)
            {
                if (copy.Count >= 1 || attachment.Slot != WeaponAttachmentSlot.Muzzle)
                    throw new ArgumentException("配件槽非法或重复。");
                copy.Add(attachment);
            }
            Durability = durability; ChamberAmmo = chamberAmmo; ShotSequence = shotSequence;
            Magazine = magazine; Attachments = copy.AsReadOnly();
        }
        public bool Equals(WeaponStateSnapshot other)
        {
            if (other == null || !Durability.Equals(other.Durability) || !ChamberAmmo.Equals(other.ChamberAmmo)
                || ShotSequence != other.ShotSequence || !Nullable.Equals(Magazine, other.Magazine)
                || Attachments.Count != other.Attachments.Count) return false;
            for (int i = 0; i < Attachments.Count; i++)
                if (Attachments[i].Slot != other.Attachments[i].Slot || !Attachments[i].Item.Equals(other.Attachments[i].Item)) return false;
            return true;
        }
        public override bool Equals(object obj) => Equals(obj as WeaponStateSnapshot);
        public override int GetHashCode()
        {
            unchecked
            {
                int hash = Durability.GetHashCode() * 31 + ChamberAmmo.GetHashCode();
                hash = hash * 31 + ShotSequence.GetHashCode(); hash = hash * 31 + Magazine.GetHashCode();
                foreach (AttachmentStateSnapshot attachment in Attachments) hash = hash * 31 + attachment.Item.GetHashCode();
                return hash;
            }
        }
    }
}
