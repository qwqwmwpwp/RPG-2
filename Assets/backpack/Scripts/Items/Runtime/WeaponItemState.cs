using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using RPG4.Backpack.Core;

namespace RPG4.Backpack.Items
{
    public sealed class MagazineItemState
    {
        private readonly RuntimeItem owner;
        private readonly List<BackpackTag> rounds = new List<BackpackTag>();
        private readonly ReadOnlyCollection<BackpackTag> readOnlyRounds;
        public IReadOnlyList<BackpackTag> Rounds => readOnlyRounds;
        public int RoundCount => rounds.Count;
        public int Capacity => Definition.Capacity;
        public MagazineDefinitionFragment Definition => owner.GetFragment<MagazineDefinitionFragment>();

        internal MagazineItemState(RuntimeItem owner, bool initialize)
        {
            this.owner = owner;
            readOnlyRounds = rounds.AsReadOnly();
            if (initialize)
                for (int i = 0; i < Definition.InitialRoundCount; i++) rounds.Add(Definition.InitialAmmoType);
        }

        public bool CanAcceptAmmo(BackpackTag itemType, ItemManifestCatalog catalog)
        {
            return TryResolveAmmo(itemType, catalog, out AmmoDefinitionFragment ammo)
                && ammo.AmmoType.MatchesTagExact(Definition.AmmoType);
        }

        internal static bool TryResolveAmmo(BackpackTag itemType, ItemManifestCatalog catalog, out AmmoDefinitionFragment ammo)
        {
            ammo = null;
            if (catalog == null || !catalog.TryGet(itemType, out ItemManifest manifest)) return false;
            ammo = manifest.GetFragment<AmmoDefinitionFragment>();
            if (ammo == null) return false;
            try { ammo.ValidateConfiguration(nameof(AmmoDefinitionFragment)); }
            catch (InvalidOperationException) { ammo = null; return false; }
            return true;
        }

        internal BackpackTag RemoveFirst(bool notify = true)
        {
            BackpackTag ammo = rounds[0];
            rounds.RemoveAt(0);
            if (notify) owner.NotifyStateChanged();
            return ammo;
        }

        internal void AddRounds(BackpackTag ammo, int count)
        {
            for (int i = 0; i < count; i++) rounds.Add(ammo);
        }

        internal bool TryRestore(IReadOnlyList<BackpackTag> values, ItemManifestCatalog catalog, out string error)
        {
            error = null;
            if (values == null || values.Count > Capacity) { error = "弹匣弹数超过容量。"; return false; }
            for (int i = 0; i < values.Count; i++)
                if (!CanAcceptAmmo(values[i], catalog)) { error = "弹匣包含未知或不兼容弹种。"; return false; }
            rounds.Clear();
            for (int i = 0; i < values.Count; i++) rounds.Add(values[i]);
            return true;
        }
    }

    public sealed class WeaponItemState
    {
        private readonly RuntimeItem owner;
        private readonly Dictionary<WeaponAttachmentSlot, RuntimeItem> attachments = new Dictionary<WeaponAttachmentSlot, RuntimeItem>();
        private readonly ReadOnlyDictionary<WeaponAttachmentSlot, RuntimeItem> readOnlyAttachments;
        public WeaponDefinitionFragment Definition => owner.GetFragment<WeaponDefinitionFragment>();
        public float Durability { get; private set; }
        public BackpackTag ChamberAmmo { get; private set; }
        public ulong ShotSequence { get; private set; }
        public RuntimeItem Magazine { get; private set; }
        public IReadOnlyDictionary<WeaponAttachmentSlot, RuntimeItem> Attachments => readOnlyAttachments;

        internal WeaponItemState(RuntimeItem owner)
        {
            this.owner = owner;
            Durability = Definition.MaxDurability;
            readOnlyAttachments = new ReadOnlyDictionary<WeaponAttachmentSlot, RuntimeItem>(attachments);
        }

        public bool IsCompatibleMagazine(RuntimeItem magazine, ItemManifestCatalog catalog)
        {
            MagazineItemState state = magazine?.MagazineState;
            if (state == null || !state.Definition.MagazineType.MatchesTagExact(Definition.MagazineType)
                || !state.Definition.AmmoType.MatchesTagExact(Definition.AmmoType)) return false;
            foreach (BackpackTag round in state.Rounds) if (!state.CanAcceptAmmo(round, catalog)) return false;
            return true;
        }

        public bool IsCompatibleAttachment(WeaponAttachmentSlot slot, RuntimeItem attachment)
        {
            AttachmentDefinitionFragment definition = attachment?.GetFragment<AttachmentDefinitionFragment>();
            return definition != null && Definition.AllowsSlot(slot) && definition.Slot == slot
                && (definition.WeaponType.IsEmpty || owner.ItemType.MatchesTagExact(definition.WeaponType));
        }

        private bool TryGetNextAmmo(ItemManifestCatalog catalog, out BackpackTag itemType, out AmmoDefinitionFragment ammo)
        {
            itemType = ChamberAmmo;
            return MagazineItemState.TryResolveAmmo(itemType, catalog, out ammo) && ammo.AmmoType.MatchesTagExact(Definition.AmmoType);
        }

        public bool CanConsumeShot(ItemManifestCatalog catalog)
        {
            return Durability > 0f && Durability >= Definition.DurabilityPerShot && ShotSequence < ulong.MaxValue
                && TryGetNextAmmo(catalog, out _, out _);
        }

        public bool TryConsumeShot(ItemManifestCatalog catalog, out BackpackTag ammoItemType,
            out AmmoDefinitionFragment ammo, out ulong sequence, out string error)
        {
            ammoItemType = default; ammo = null; sequence = ShotSequence;
            if (!CanConsumeShot(catalog) || !TryGetNextAmmo(catalog, out ammoItemType, out ammo))
            { error = "武器损坏、无兼容弹药或射击序号耗尽。"; return false; }

            MagazineItemState magazine = Magazine?.MagazineState;
            ChamberAmmo = default;
            if (magazine != null && magazine.RoundCount > 0) ChamberAmmo = magazine.RemoveFirst(false);
            Durability = Math.Max(0f, Durability - Definition.DurabilityPerShot);
            sequence = ++ShotSequence;
            owner.NotifyStateChanged();
            error = null;
            return true;
        }

        public bool TryChamberRound(ItemManifestCatalog catalog, out string error)
        {
            if (!ChamberAmmo.IsEmpty || Magazine == null || Magazine.MagazineState.RoundCount == 0
                || !MagazineItemState.TryResolveAmmo(Magazine.MagazineState.Rounds[0], catalog, out AmmoDefinitionFragment ammo)
                || !ammo.AmmoType.MatchesTagExact(Definition.AmmoType))
            { error = "已上膛或弹匣没有兼容弹药。"; return false; }
            ChamberAmmo = Magazine.MagazineState.RemoveFirst(false);
            owner.NotifyStateChanged();
            error = null;
            return true;
        }

        internal void SetMagazine(RuntimeItem item)
        {
            Detach(Magazine);
            Magazine = item;
            Attach(item);
        }

        internal void SetAttachment(WeaponAttachmentSlot slot, RuntimeItem item)
        {
            if (attachments.TryGetValue(slot, out RuntimeItem previous)) Detach(previous);
            attachments.Remove(slot);
            if (item != null) { attachments.Add(slot, item); Attach(item); }
        }

        private void Attach(RuntimeItem item)
        {
            if (item == null) return;
            item.ParentItem = owner;
            item.Changed += OnChildChanged;
        }

        private void Detach(RuntimeItem item)
        {
            if (item == null) return;
            item.Changed -= OnChildChanged;
            item.ParentItem = null;
        }

        private void OnChildChanged(RuntimeItem item) => owner.NotifyStateChanged();

        internal bool TryRestore(float durability, BackpackTag chamberAmmo, ulong sequence,
            ItemManifestCatalog catalog, out string error)
        {
            error = null;
            if (float.IsNaN(durability) || float.IsInfinity(durability) || durability < 0f || durability > Definition.MaxDurability)
            { error = "武器耐久非法。"; return false; }
            if (!chamberAmmo.IsEmpty && (!MagazineItemState.TryResolveAmmo(chamberAmmo, catalog, out AmmoDefinitionFragment ammo)
                || !ammo.AmmoType.MatchesTagExact(Definition.AmmoType)))
            { error = "膛内弹种未知或不兼容。"; return false; }
            Durability = durability; ChamberAmmo = chamberAmmo; ShotSequence = sequence;
            return true;
        }
    }
}
