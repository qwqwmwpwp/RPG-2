using System;
using RPG4.Backpack.Core;
using RPG4.Backpack.Items;
using RPG4.LoadoutTemplate;
using UnityEngine;

namespace RPG4.Networking
{
    public sealed partial class NetworkPlayerController
    {
        [Serializable] private sealed class PublicEquipmentWire
        {
            public int schema = 1, count;
            public ulong id;
            public string type, payload;
        }

        private RoleLoadoutController publicEquipmentRole;
        private RuntimeItem lastPublicWeapon;
        private int lastPublicRevision;
        private bool publicWeaponCaptured;

        private void BindPublicEquipment()
        {
            UnbindPublicEquipment();
            publicEquipmentRole = loadout?.Role;
            if (publicEquipmentRole != null)
            {
                publicEquipmentRole.Committed += OnEquipmentCommitted;
                publicEquipmentRole.Changed += OnEquipmentChanged;
            }
            PublishPublicEquipment();
        }

        private void UnbindPublicEquipment()
        {
            if (!ReferenceEquals(publicEquipmentRole, null))
            {
                publicEquipmentRole.Committed -= OnEquipmentCommitted;
                publicEquipmentRole.Changed -= OnEquipmentChanged;
            }
            publicEquipmentRole = null;
            lastPublicWeapon = null;
            publicWeaponCaptured = false;
        }

        private void OnEquipmentCommitted(LoadoutCommitRecord commit)
        {
            if (publicEquipmentRole != null && publicEquipmentRole.AffectsEquippedWeapon(commit)) PublishPublicEquipment();
        }

        private void OnEquipmentChanged()
        {
            if (publicEquipmentRole != null && !publicEquipmentRole.IsPublishingCommit) PublishPublicEquipment();
        }

        private void PublishPublicEquipment()
        {
            if (!serverStarted) return;
            RuntimeItem item = null;
            publicEquipmentRole?.TryGetCurrentWeapon(out item, out _);
            int revision = item?.Revision ?? 0;
            if (publicWeaponCaptured && ReferenceEquals(item, lastPublicWeapon) && revision == lastPublicRevision) return;

            string state = string.Empty;
            if (item != null
                && RuntimeItemSnapshotCodec.TryCapture(item, out RuntimeItemSnapshot snapshot, out _)
                && RuntimeItemSnapshotCodec.TryEncodeSnapshot(snapshot, out byte[] payload, out _) && payload.Length <= 49152)
            {
                state = JsonUtility.ToJson(new PublicEquipmentWire { id = item.ItemId.Value, type = item.ItemType.Name,
                    count = item.StackCount, payload = Convert.ToBase64String(payload) });
                if (state.Length > 65536) state = string.Empty;
            }
            lastPublicWeapon = item;
            lastPublicRevision = revision;
            publicWeaponCaptured = item == null || state.Length != 0;
            if (!string.Equals(publicEquipmentState, state, StringComparison.Ordinal)) publicEquipmentState = state;
        }
        private void OnPublicEquipmentChanged(string previousState, string nextState) => ApplyPublicEquipment(nextState);
        private void ApplyPublicEquipment(string state)
        {
            if (serverStarted || player == null) return;
            RuntimeItem item = null;
            var catalog = loadout?.Role?.ManifestCatalog;
            if (!string.IsNullOrEmpty(state) && state.Length <= 65536 && catalog != null)
            {
                try
                {
                    PublicEquipmentWire wire = JsonUtility.FromJson<PublicEquipmentWire>(state);
                    if (wire != null && wire.schema == 1 && wire.id != 0 && wire.count == 1
                        && !string.IsNullOrEmpty(wire.type) && wire.type.Length <= RuntimeItemSnapshot.MaxItemTypeLength
                        && !string.IsNullOrEmpty(wire.payload) && wire.payload.Length <= 65536)
                    {
                        var type = new BackpackTag(wire.type);
                        byte[] bytes = Convert.FromBase64String(wire.payload);
                        if (catalog.TryGet(type, out ItemManifest manifest)
                            && RuntimeItemSnapshotCodec.TryDecodeSnapshot(new ItemId(wire.id), type, wire.count, bytes,
                                out RuntimeItemSnapshot snapshot, out _)
                            && manifest.TryRestoreRuntimeItem(snapshot, catalog, out RuntimeItem restored, out _)
                            && restored.WeaponState != null) item = restored;
                    }
                }
                catch (ArgumentException) { }
                catch (FormatException) { }
            }
            player.Equipment?.ApplyReplicatedEquipment(item);
        }
    }
}
