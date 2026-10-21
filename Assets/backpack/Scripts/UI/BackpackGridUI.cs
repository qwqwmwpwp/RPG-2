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
        [SerializeField, Min(0.05f)] private float bindingCheckInterval = 0.25f;

        private readonly InventoryInputScope inputScope = new InventoryInputScope();
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
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            nextBindingCheck = 0f;
        }

        private void Update()
        {
            if (Time.unscaledTime >= nextBindingCheck)
            {
                nextBindingCheck = Time.unscaledTime + Mathf.Max(0.05f, bindingCheckInterval);
                BindInventory(LocalBackpackSourceResolver.Resolve());
            }
            if (IsOpen) inputScope.SetSource(Inventory);
            SyncWindowSize();
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            Unbind();
        }

        protected override void OnDestroy()
        {
            Unbind();
            base.OnDestroy();
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
            if (windowRect != null && containerRect != null) windowRect.sizeDelta = containerRect.sizeDelta;
        }

        private void BindInventory(IBackpackView source)
        {
            if (!EnsureDisplay()) return;
            object eventSource = source?.EventSource;
            if (ReferenceEquals(boundInventory, source))
            {
                if (ReferenceEquals(boundEventSource, eventSource)) return;
                if (interaction != null) interaction.EndSession();
                containerUI.BindInventory(source);
                boundEventSource = eventSource;
                if (IsOpen && interaction != null) interaction.BeginSession(containerUI);
                return;
            }

            Unbind();
            if (source == null) return;
            boundInventory = source;
            boundEventSource = eventSource;
            containerUI.BindInventory(source);
            SetHint(OpenKeyLabel + "   /   打开背包");
        }

        private void Unbind()
        {
            Close();
            if (interaction != null) interaction.EndSession();
            inputScope.Release();
            boundInventory = null;
            boundEventSource = null;
            if (containerUI != null) containerUI.UnbindInventory();
            SetHint("等待本地玩家 · " + OpenKeyLabel + " 打开背包");
        }

        protected override bool CanOpen()
        {
            BindInventory(LocalBackpackSourceResolver.Resolve());
            return Inventory != null && EnsureDisplay() && containerUI.Columns > 0 && containerUI.Rows > 0;
        }

        protected override void OnOpened()
        {
            containerUI.Redraw();
            SyncWindowSize();
            if (interaction != null) interaction.BeginSession(containerUI);
            inputScope.Capture(Inventory);
            containerUI.SetPresentationVisible(true);
            if (hudHint != null) hudHint.gameObject.SetActive(false);
        }

        protected override void OnClosed()
        {
            if (interaction != null) interaction.EndSession();
            inputScope.Release();
            if (containerUI != null) containerUI.SetPresentationVisible(false);
            if (hudHint != null) hudHint.gameObject.SetActive(true);
        }

        private void SetHint(string text)
        {
            if (hudHint == null) return;
            hudHint.text = text;
            hudHint.gameObject.SetActive(!IsOpen);
        }
    }
}
