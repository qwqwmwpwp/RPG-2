using RPG4.Raid.Domain;
using System;
using System.Collections.Generic;
using Mirror;
using RPG4.Backpack.Core;
using RPG4.Backpack.Inventory;
using RPG4.Backpack.Items;
using RPG4.Backpack.Network;
using RPG4.Backpack.Runtime;
using RPG4.Networking.Contracts;
using UnityEngine;

namespace RPG4.Networking
{
    [DisallowMultipleComponent, RequireComponent(typeof(NetworkIdentity), typeof(BackpackNetworkInventory), typeof(ChestInventory))]
    public sealed class RaidLootBag : MonoBehaviour
    {
        public string BagId { get; private set; }
        public void Prepare(PersistentDropBag bag, ItemManifestCatalog catalog)
        {
            if (bag == null || string.IsNullOrEmpty(bag.BagId) || BagId != null) throw new InvalidOperationException("Invalid loot bag.");
            BagId = bag.BagId;
            var items = new List<RuntimeItem>();
            foreach (PersistentItemData entry in bag.Backpack.Items) items.Add(RaidInventorySnapshots.RestoreItem(entry, catalog));
            foreach (PersistentItemData entry in bag.Equipment.Items) items.Add(RaidInventorySnapshots.RestoreItem(entry, catalog));
            // Dedicated rows preserve every item's physical shape without merging stacks or discarding overflow.
            int columns = 1, rows = 0;
            foreach (RuntimeItem item in items)
            { GridCoord dimensions = InventoryContainer.GetItemDimensions(item); columns = Math.Max(columns, dimensions.X); rows += dimensions.Y; }
            rows = Math.Max(1, rows);
            BackpackNetworkInventory inventory = GetComponent<BackpackNetworkInventory>();
            inventory.ConfigureSharedAccess(3f);
            InventoryContainer container = inventory.PrepareUnspawnedLootContainer(rows, columns, catalog);
            int row = 0;
            foreach (RuntimeItem item in items)
            {
                if (!container.TryPlaceItem(item, row * columns, out string error)) throw new InvalidOperationException(error);
                row += InventoryContainer.GetItemDimensions(item).Y;
            }
        }
    }
}
