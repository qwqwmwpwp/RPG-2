using UnityEngine;
using RPG4.Backpack.Core;
using RPG4.Backpack.Runtime;
using RPG4.UI;

namespace RPG4.Backpack.UI
{
    /// <summary>库存窗口入口：接收展示请求、选择窗口，并为输入路由提供当前活动会话。</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(UIManager))]
    [DefaultExecutionOrder(-800)]
    public sealed class InventoryUICoordinator : MonoBehaviour
    {
        private UIManager manager;
        private EventSubscription openChestSubscription;
        private int lastToggleFrame = -1;

        private bool CanControlWindows => isActiveAndEnabled && manager != null && manager.isActiveAndEnabled;

        private void Awake() => manager = GetComponent<UIManager>();

        private void OnEnable()
        {
            openChestSubscription = EventBus.Global.Subscribe<ChestInventory>(
                CmdChannels.OpenChestUI, EventBus.AnySource, this, OnOpenChestRequested);
        }

        private void OnDisable()
        {
            openChestSubscription.Dispose();
            openChestSubscription = default;
        }

        public void ToggleInventory()
        {
            if (!CanControlWindows || lastToggleFrame == Time.frameCount) return;
            lastToggleFrame = Time.frameCount;
            if (TryGetActiveSession(out UIBase window, out _)) manager.Close(window);
            else manager.Open<BackpackGridUI>();
        }

        public bool OpenChest(ChestInventory chest)
        {
            if (!CanControlWindows || chest == null) return false;
            ChestBackpackUI window = manager.Get<ChestBackpackUI>();
            return window != null && window.Open(chest);
        }

        private void OnOpenChestRequested(ChestInventory chest) => OpenChest(chest);

        /// <summary>返回当前打开的库存窗口；交互组件或库存尚未就绪时仍允许关闭窗口。</summary>
        public bool TryGetActiveSession(out UIBase window, out BackpackInteractionController interaction)
        {
            window = null;
            interaction = null;
            if (!CanControlWindows) return false;
            ChestBackpackUI chest = manager.Get<ChestBackpackUI>();
            if (chest != null && chest.IsOpen)
            {
                window = chest;
                interaction = chest.Interaction;
                return true;
            }

            BackpackGridUI backpack = manager.Get<BackpackGridUI>();
            if (backpack == null || !backpack.IsOpen) return false;
            window = backpack;
            interaction = backpack.Interaction;
            return true;
        }
    }
}
