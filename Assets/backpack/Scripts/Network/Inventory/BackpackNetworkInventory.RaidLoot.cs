using System;
using RPG4.Backpack.Core;
using RPG4.Backpack.Inventory;
using RPG4.Backpack.Items;

namespace RPG4.Backpack.Network
{
    public sealed partial class BackpackNetworkInventory
    {
        private InventoryHostCore preparedLootCore;
        /// <summary>Unpublished server corpse storage. Configured before Spawn; no client owner is assigned.</summary>
        public InventoryContainer PrepareUnspawnedLootContainer(int gridRows, int gridColumns, ItemManifestCatalog itemCatalog)
        {
            if (netId != 0 || !Mirror.NetworkServer.active) throw new InvalidOperationException("Loot must be prepared before Spawn.");
            if (gridRows < 1 || gridColumns < 1 || (long)gridRows * gridColumns > InventoryContainer.MaxSlotCount)
                throw new ArgumentOutOfRangeException(nameof(gridRows));
            preparedLootCore = new InventoryHostCore(gridRows, gridColumns, LogModule.Network, name) { Catalog = itemCatalog };
            catalog = itemCatalog;
            return preparedLootCore.Container;
        }
    }
}
