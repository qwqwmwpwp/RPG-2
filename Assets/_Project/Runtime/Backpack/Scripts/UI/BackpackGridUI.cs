using TMPro;
using UnityEngine;
using RPG4.Backpack.Core;
using RPG4.Backpack.Inventory;
using RPG4.UI;

namespace RPG4.Backpack.UI
{
    /// <summary>玩家背包窗口：确定数据来源，协调容器、交互会话与输入状态。</summary>
    [DisallowMultipleComponent]
    public class BackpackGridUI : UIBase
    {
        [SerializeField] private BackpackContainerUI containerUI;
        [SerializeField] private BackpackContainerUI equipmentView;
        [SerializeField] private BackpackInteractionController interaction;
        [SerializeField] private TMP_Text hudHint;
        [SerializeField] private string openKeyHint;
        [SerializeField] private bool usePrefabLayout;
        [SerializeField, Min(0.05f)] private float bindingCheckInterval = 0.25f;

        private readonly InventoryInputScope inputScope = new InventoryInputScope();
        private IBackpackView preparedInventory;
        private IBackpackView boundInventory;
        private object boundEventSource;
        private IBackpackView boundEquipment;
        private object boundEquipmentEventSource;
        private RectTransform windowRect;
        private RectTransform containerRect;
        private float nextBindingCheck;
        private bool configurationWarned;
        private bool hasSessionSources;
        private IBackpackView sessionInventory, sessionEquipment;

        public BackpackContainerUI ContainerUI => containerUI;
        public BackpackContainerUI EquipmentView => equipmentView;
        public BackpackInteractionController Interaction => interaction;
        public IBackpackView Inventory => boundInventory;

        /// <summary>Explicit online sources remain prepared while the window is closed; no offline fallback.</summary>
        public bool BindSessionSources(IBackpackView inventory, IBackpackView equipment)
        {
            if (inventory == null || equipment == null || !inventory.IsReady || !equipment.IsReady
                || inventory.EventSource == null || equipment.EventSource == null || !EnsureDisplay())
            { UnbindSessionSources(); return false; }
            hasSessionSources = true;
            sessionInventory = inventory; sessionEquipment = equipment;
            BindInventory(inventory);
            return IsSessionBoundTo(inventory, equipment);
        }

        public bool IsSessionBoundTo(IBackpackView inventory, IBackpackView equipment) => hasSessionSources
            && inventory != null && equipment != null && inventory.IsReady && equipment.IsReady
            && inventory.EventSource != null && equipment.EventSource != null
            && ReferenceEquals(sessionInventory, inventory) && ReferenceEquals(sessionEquipment, equipment)
            && ReferenceEquals(boundInventory, inventory) && ReferenceEquals(boundEquipment, equipment)
            && ReferenceEquals(boundEventSource, inventory.EventSource) && ReferenceEquals(boundEquipmentEventSource, equipment.EventSource)
            && containerUI != null && equipmentView != null && containerUI != equipmentView
            && containerUI.isActiveAndEnabled && equipmentView.isActiveAndEnabled
            && containerUI.Columns > 0 && containerUI.Rows > 0 && equipmentView.Columns > 0 && equipmentView.Rows > 0
            && ReferenceEquals(containerUI.Inventory, inventory) && ReferenceEquals(equipmentView.Inventory, equipment);

        public void UnbindSessionSources()
        {
            Close();
            hasSessionSources = false; sessionInventory = sessionEquipment = null;
            ClearBoundSources();
        }

        private IBackpackView ResolveInventory() => hasSessionSources ? sessionInventory : LocalBackpackSourceResolver.Resolve();

        private string OpenKeyLabel
        {
            get
            {
                if (!string.IsNullOrEmpty(openKeyHint)) return openKeyHint;
                InventoryUIInputRouter router = Manager != null ? Manager.GetComponent<InventoryUIInputRouter>() : null;
                return router != null ? router.ToggleKeyLabel : "未设置";
            }
        }

        protected override void Awake()
        {
            base.Awake();
            windowRect = GetComponent<RectTransform>();
            if (containerUI != null)
            {
                containerRect = (RectTransform)containerUI.transform;
                containerUI.SetPresentationVisible(false);
            }
            if (equipmentView != null) equipmentView.SetPresentationVisible(false);
        }

        private void Start()
        {
            RefreshClosedHint();
        }

        private void Update()
        {
            // 关闭时只按需更新提示，不绑定库存或维护交互会话。
            if (!IsOpen && hudHint == null) return;
            if (Time.unscaledTime >= nextBindingCheck)
            {
                nextBindingCheck = Time.unscaledTime + Mathf.Max(0.05f, bindingCheckInterval);
                if (!IsOpen)
                {
                    RefreshClosedHint();
                    return;
                }

                IBackpackView source = ResolveInventory();
                if (source == null)
                {
                    Close();
                    return;
                }
                BindInventory(source);
            }
            if (!IsOpen) return;
            inputScope.SetSource(Inventory);
            SyncWindowSize();
        }

        private bool EnsureDisplay()
        {
            if (containerUI != null && equipmentView != null && containerUI != equipmentView)
                return containerUI.EnsureInitialized() && equipmentView.EnsureInitialized();
            if (!configurationWarned)
            {
                configurationWarned = true;
                BackpackLog.Warn(LogModule.UI, "请在背包窗口中接线不同的背包网格与装备槽视图。");
            }
            return false;
        }

        private void SyncWindowSize()
        {
            if (usePrefabLayout) return;
            if (windowRect != null && containerRect != null && windowRect.sizeDelta != containerRect.sizeDelta)
                windowRect.sizeDelta = containerRect.sizeDelta;
        }

        private void BindInventory(IBackpackView source, bool beginInteraction = true)
        {
            object eventSource = source?.EventSource;
            IBackpackView equipment = hasSessionSources ? sessionEquipment : LocalBackpackSourceResolver.ResolveEquipment(source);
            object equipmentEventSource = equipment?.EventSource;
            if (ReferenceEquals(boundInventory, source) && ReferenceEquals(boundEventSource, eventSource)
                && ReferenceEquals(boundEquipment, equipment) && ReferenceEquals(boundEquipmentEventSource, equipmentEventSource)) return;

            // 改绑只结束旧交互，不关闭窗口或释放当前输入状态。
            if (interaction != null) interaction.EndSession();
            boundInventory = source;
            boundEventSource = eventSource;
            boundEquipment = equipment;
            boundEquipmentEventSource = equipmentEventSource;
            containerUI.BindInventory(source);
            equipmentView.BindInventory(equipment);
            if (beginInteraction && IsOpen && containerUI.IsReady && equipmentView.IsReady) BeginInteraction();
        }

        private void BeginInteraction()
        {
            if (interaction != null)
            {
                if (boundEquipment != null)
                {
                    interaction.BeginSession(containerUI, equipmentView);
                    interaction.SetQuickTransferPair(containerUI, equipmentView, "快速装备", "快速卸装", replaceSecondSingleSlot: true);
                }
                else interaction.BeginSession(containerUI);
            }
        }

        protected override bool CanOpen()
        {
            preparedInventory = null;
            IBackpackView source = ResolveInventory();
            if (source == null || !source.IsReady || source.EventSource == null || !EnsureDisplay()
                || hasSessionSources && (sessionEquipment == null || !sessionEquipment.IsReady || sessionEquipment.EventSource == null)
                || containerUI.Columns < 1 || containerUI.Rows < 1) return false;
            preparedInventory = source;
            return true;
        }

        protected override void OnOpened()
        {
            IBackpackView source = preparedInventory;
            preparedInventory = null;
            BindInventory(source, beginInteraction: false);
            nextBindingCheck = Time.unscaledTime + Mathf.Max(0.05f, bindingCheckInterval);
            containerUI.SetPresentationVisible(true);
            equipmentView.SetPresentationVisible(true);
            BeginInteraction();
            SyncWindowSize();
            inputScope.Capture(Inventory);
            if (hudHint != null) hudHint.gameObject.SetActive(false);
        }

        protected override void OnClosed()
        {
            if (containerUI != null) containerUI.SetPresentationVisible(false);
            if (equipmentView != null) equipmentView.SetPresentationVisible(false);
            if (interaction != null) interaction.EndSession();
            inputScope.Release();
            preparedInventory = null;
            if (!hasSessionSources) ClearBoundSources();
            nextBindingCheck = 0f;
            RefreshClosedHint();
        }

        private void ClearBoundSources()
        {
            preparedInventory = boundInventory = null;
            boundEventSource = null;
            boundEquipment = null;
            boundEquipmentEventSource = null;
            if (containerUI != null)
            {
                containerUI.UnbindInventory();
            }
            if (equipmentView != null)
            {
                equipmentView.UnbindInventory();
            }
        }

        private void RefreshClosedHint()
        {
            if (hudHint == null || IsOpen) return;
            SetHint(ResolveInventory() != null
                ? OpenKeyLabel + "   /   打开背包"
                : "等待本地玩家 · " + OpenKeyLabel + " 打开背包");
        }

        private void SetHint(string text)
        {
            if (hudHint == null) return;
            if (hudHint.text != text) hudHint.text = text;
            if (hudHint.gameObject.activeSelf == IsOpen) hudHint.gameObject.SetActive(!IsOpen);
        }
    }
}
