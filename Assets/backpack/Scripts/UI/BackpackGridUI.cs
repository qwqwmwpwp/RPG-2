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
        [SerializeField] private BackpackInteractionController interaction;
        [SerializeField] private TMP_Text hudHint;
        [SerializeField] private string openKeyHint;
        [SerializeField] private bool usePrefabLayout;
        [SerializeField, Min(0.05f)] private float bindingCheckInterval = 0.25f;

        private readonly InventoryInputScope inputScope = new InventoryInputScope();
        private IBackpackView preparedInventory;
        private IBackpackView boundInventory;
        private object boundEventSource;
        private RectTransform windowRect;
        private RectTransform containerRect;
        private float nextBindingCheck;
        private bool configurationWarned;

        public BackpackContainerUI ContainerUI => containerUI;
        public BackpackInteractionController Interaction => interaction;
        public IBackpackView Inventory => boundInventory;

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
        }

        private void Start()
        {
            if (EnsureDisplay()) SyncWindowSize();
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

                IBackpackView source = LocalBackpackSourceResolver.Resolve();
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
            if (containerUI != null) return containerUI.EnsureInitialized();
            if (!configurationWarned)
            {
                configurationWarned = true;
                BackpackLog.Warn(LogModule.UI, "请在背包窗口中接线 BackpackContainerUI 网格预制体实例。");
            }
            return false;
        }

        private void SyncWindowSize()
        {
            if (usePrefabLayout) return;
            if (windowRect != null && containerRect != null) windowRect.sizeDelta = containerRect.sizeDelta;
        }

        private void BindInventory(IBackpackView source)
        {
            object eventSource = source?.EventSource;
            if (ReferenceEquals(boundInventory, source) && ReferenceEquals(boundEventSource, eventSource)) return;

            // 改绑只结束旧交互，不关闭窗口或释放当前输入状态。
            if (interaction != null) interaction.EndSession();
            boundInventory = source;
            boundEventSource = eventSource;
            containerUI.BindInventory(source);
            if (interaction != null) interaction.BeginSession(containerUI);
        }

        protected override bool CanOpen()
        {
            preparedInventory = null;
            IBackpackView source = LocalBackpackSourceResolver.Resolve();
            if (source == null || !source.IsReady || source.EventSource == null || !EnsureDisplay()
                || containerUI.Columns < 1 || containerUI.Rows < 1) return false;
            preparedInventory = source;
            return true;
        }

        protected override void OnOpened()
        {
            IBackpackView source = preparedInventory;
            preparedInventory = null;
            BindInventory(source);
            nextBindingCheck = Time.unscaledTime + Mathf.Max(0.05f, bindingCheckInterval);
            SyncWindowSize();
            inputScope.Capture(Inventory);
            containerUI.SetPresentationVisible(true);
            if (hudHint != null) hudHint.gameObject.SetActive(false);
        }

        protected override void OnClosed()
        {
            if (interaction != null) interaction.EndSession();
            inputScope.Release();
            preparedInventory = boundInventory = null;
            boundEventSource = null;
            if (containerUI != null)
            {
                containerUI.UnbindInventory();
                containerUI.SetPresentationVisible(false);
            }
            nextBindingCheck = 0f;
            RefreshClosedHint();
        }

        private void RefreshClosedHint()
        {
            if (hudHint == null || IsOpen) return;
            SetHint(LocalBackpackSourceResolver.Resolve() != null
                ? OpenKeyLabel + "   /   打开背包"
                : "等待本地玩家 · " + OpenKeyLabel + " 打开背包");
        }

        private void SetHint(string text)
        {
            if (hudHint == null) return;
            hudHint.text = text;
            hudHint.gameObject.SetActive(!IsOpen);
        }
    }
}
