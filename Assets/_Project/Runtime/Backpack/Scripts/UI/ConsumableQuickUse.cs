using System.Collections.Generic;
using RPG4.Backpack.Inventory;
using RPG4.Backpack.Items;
using UnityEngine;

namespace RPG4.Backpack.UI
{
    /// <summary>本地快捷药品只保存类型名；每次从当前权威投影中按 ItemId 选择一份。</summary>
    public static class ConsumableQuickUse
    {
        private const string PreferenceKey = "RPG4.UI.QuickConsumable";
        public const string DefaultType = "GameItems.Consumables.Potions.Red.Small";
        private static readonly List<RuntimeItem> items = new List<RuntimeItem>();
        public static string SelectedType => PlayerPrefs.GetString(PreferenceKey, DefaultType);
        public static bool IsUsable(RuntimeItem item) => item != null && item.StackCount > 0
            && item.GetFragment<ConsumableUseDefinitionFragment>() != null;
        public static void Select(RuntimeItem item)
        {
            if (!IsUsable(item)) return;
            PlayerPrefs.SetString(PreferenceKey, item.ItemType.Name);
            PlayerPrefs.Save();
        }
        public static RuntimeItem Find(IBackpackView inventory, ItemId id)
        {
            if (inventory == null || !inventory.IsReady) return null;
            inventory.CopyItems(items);
            foreach (RuntimeItem item in items) if (item.ItemId == id) return item;
            return null;
        }
        public static RuntimeItem Resolve(IBackpackView inventory, out long total)
        {
            total = 0;
            if (inventory == null || !inventory.IsReady) return null;
            inventory.CopyItems(items);
            RuntimeItem first = null;
            string type = SelectedType;
            foreach (RuntimeItem item in items)
            {
                if (!IsUsable(item) || item.ItemType.Name != type) continue;
                total += item.StackCount;
                if (first == null || item.ItemId < first.ItemId) first = item;
            }
            return first;
        }
    }
}
