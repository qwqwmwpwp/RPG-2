using System.Collections.Generic;
using UnityEngine;
using RPG4.Backpack.Core;
using RPG4.Backpack.Items;
using RPG4.Interaction;

namespace RPG4.Backpack.Runtime
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(BackpackInventory))]
    public sealed class ChestInventory : MonoBehaviour, IInteractable
    {
        #region 字段与属性
        private BackpackInventory inventory;
        public BackpackInventory Inventory => inventory != null ? inventory : inventory = GetComponent<BackpackInventory>();
        #endregion

        #region 生命周期
        private void Awake() => inventory = GetComponent<BackpackInventory>();

        private void Start()
        {
            if (fillCatalogOnStart) FillFromCatalogForTest();
        }
        #endregion

        public bool CanInteract(PlayerInteraction player) =>
            isActiveAndEnabled && player != null && Inventory.isActiveAndEnabled && Inventory.IsInitialized;

        public void Interact(PlayerInteraction player)
        {
            if (CanInteract(player)) OpenUI();
        }

        /// <summary>供外部交互调用，请求显示本箱子的背包；箱子不持有 UI 引用。</summary>
        [ContextMenu("交互/打开箱子背包")]
        public void OpenUI() => EventBus.Global.Publish(CmdChannels.OpenChestUI, this, this);

        #region 测试物品生成
        private const int TestStackCount = 5;

        [Header("测试")]
        [Tooltip("勾选后 Start 时把目录中的模板各填入一份测试堆叠（仅调试用，正式场景请取消勾选）。")]
        [SerializeField] private bool fillCatalogOnStart;

        [ContextMenu("测试/从目录填入物品")]
        private void FillFromCatalogForTest()
        {
            if (!Inventory.IsInitialized)
            {
                BackpackLog.Warn(LogModule.Inventory, "箱子背包尚未初始化（组件未执行 Awake）。");
                return;
            }

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
                if (Inventory.TryAddItem(manifest, stackCount, out _, out string error)) added++;
                else BackpackLog.Warn(LogModule.Inventory, $"{name} 测试填入 {manifest.ItemType} 失败：{error}");
            }

            BackpackLog.Info(LogModule.Inventory, $"{name} 测试填入完成：成功 {added}/{manifests.Count}，当前物品 {Inventory.ItemCount} 件。");
        }
        #endregion
    }
}
