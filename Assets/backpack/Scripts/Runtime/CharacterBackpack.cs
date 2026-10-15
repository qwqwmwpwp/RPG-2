using System.Collections.Generic;
using UnityEngine;
using RPG4.Backpack.Core;
using RPG4.Backpack.Inventory;
using RPG4.Backpack.Items;

namespace RPG4.Backpack.Runtime
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(BackpackInventory))]
    public sealed class CharacterBackpack : MonoBehaviour
    {
        #region 角色归属
        [SerializeField] private bool isLocalPlayer = true;
        private BackpackInventory inventory;

        public BackpackInventory Inventory => inventory != null ? inventory : inventory = GetComponent<BackpackInventory>();

        public bool IsLocalPlayer
        {
            get => isLocalPlayer;
            set
            {
                if (isLocalPlayer == value) return;
                isLocalPlayer = value;
                if (!isActiveAndEnabled) return;
                if (value) BackpackRegistry.Register(this);
                else BackpackRegistry.Unregister(this);
            }
        }
        private void Awake() => inventory = GetComponent<BackpackInventory>();
        private void OnEnable() => BackpackRegistry.Register(this);
        private void OnDisable() => BackpackRegistry.Unregister(this);

        private void Start()
        {
            if (!EnsureInventory(out _)) return;
            BackpackLog.Info(LogModule.Inventory, $"角色背包就绪：{name}，尺寸 {Inventory.Rows} × {Inventory.Columns}。");
            if (fillCatalogOnStart) FillFromCatalogForTest();
        }
        #endregion

        #region 测试辅助
        private const int TestStackCount = 5;
        private readonly List<RuntimeItem> testItems = new List<RuntimeItem>();

        [Header("测试")]
        [Tooltip("勾选后 Start 时把目录中的模板各填入一份测试堆叠（仅调试用，正式场景请取消勾选）。")]
        [SerializeField] private bool fillCatalogOnStart;

        public bool TryValidateMapping(out string error)
        {
            InventoryContainer container = Inventory.Container;
            if (container != null) return container.TryValidateMapping(out error);
            error = "背包尚未初始化（组件未执行 Awake）。";
            return false;
        }

        private bool EnsureInventory(out string error)
        {
            error = Inventory.IsInitialized ? null : "背包尚未初始化（组件未执行 Awake）。";
            if (error == null) return true;
            BackpackLog.Warn(LogModule.Inventory, error);
            return false;
        }

        [ContextMenu("测试/从目录填入物品")]
        private void FillFromCatalogForTest()
        {
            if (!EnsureInventory(out string error)) return;
            if (Inventory.Catalog == null)
            {
                BackpackLog.Warn(LogModule.Inventory, $"{name} 未配置物品目录（catalog），无法填入测试物品。");
                return;
            }

            IReadOnlyList<ItemManifest> manifests = Inventory.Catalog.Manifests;
            if (manifests.Count == 0)
            {
                BackpackLog.Warn(LogModule.Inventory, $"{name} 的物品目录为空，没有可填入的模板。");
                return;
            }

            int added = 0;
            for (int i = 0; i < manifests.Count; i++)
            {
                ItemManifest manifest = manifests[i];
                if (manifest == null) continue;
                StackableFragment stackable = manifest.GetFragment<StackableFragment>();
                int stackCount = stackable == null ? 1 : Mathf.Min(TestStackCount, stackable.MaxStackSize);
                if (Inventory.TryAddItem(manifest, stackCount, out _, out error)) added++;
                else BackpackLog.Warn(LogModule.Inventory, $"{name} 测试填入 {manifest.ItemType} 失败：{error}");
            }

            BackpackLog.Info(LogModule.Inventory, $"{name} 测试填入完成：成功 {added}/{manifests.Count}，当前物品 {Inventory.ItemCount} 件。");
        }

        [ContextMenu("测试/清空背包")]
        private void ClearForTest()
        {
            if (!EnsureInventory(out string error)) return;
            Inventory.CopyItems(testItems);
            int removed = 0;
            for (int i = 0; i < testItems.Count; i++)
            {
                RuntimeItem item = testItems[i];
                if (Inventory.TryRemoveItem(item, out error)) removed++;
                else BackpackLog.Warn(LogModule.Inventory, $"{name} 测试清空 {item.ItemType} 失败：{error}");
            }

            BackpackLog.Info(LogModule.Inventory, $"{name} 测试清空完成：移除 {removed} 件，剩余 {Inventory.ItemCount} 件。");
        }

        [ContextMenu("测试/拆分首个可堆叠物品")]
        private void SplitFirstStackableForTest()
        {
            if (!EnsureInventory(out string error)) return;
            Inventory.CopyItems(testItems);
            for (int i = 0; i < testItems.Count; i++)
            {
                RuntimeItem item = testItems[i];
                if (!item.IsStackable || item.StackCount < 2) continue;
                int splitCount = item.StackCount / 2;
                if (Inventory.TrySplitStack(item, splitCount, out ItemId newItemId, out error))
                    BackpackLog.Info(LogModule.Inventory, $"{name} 测试拆分成功：{item.ItemType} 拆出 {splitCount} 件（新标识 {newItemId}），源剩 {item.StackCount} 件。");
                else BackpackLog.Warn(LogModule.Inventory, $"{name} 测试拆分失败：{error}");
                return;
            }

            BackpackLog.Warn(LogModule.Inventory, $"{name} 没有可拆分的堆叠（需堆叠数 ≥ 2），请先填入测试物品。");
        }

        [ContextMenu("测试/打印背包内容")]
        private void LogContentsForTest()
        {
            if (!EnsureInventory(out _)) return;
            Inventory.CopyItems(testItems);
            BackpackLog.Info(LogModule.Inventory, $"{name} 背包内容：物品 {Inventory.ItemCount} 件 / 槽位 {Inventory.SlotCount} 格，已占 {Inventory.OccupiedCellCount} 格 / 空闲 {Inventory.EmptyCellCount} 格。");
            for (int i = 0; i < testItems.Count; i++)
            {
                RuntimeItem item = testItems[i];
                Inventory.TryGetPlacement(item, out InventoryPlacement placement);
                BackpackLog.Info(LogModule.Inventory, $"  [{i}] {item.ItemType} × {item.StackCount}，占格 {placement.Dimensions}，上左索引 {placement.UpperLeftIndex}。");
            }
        }
        #endregion
    }
}
