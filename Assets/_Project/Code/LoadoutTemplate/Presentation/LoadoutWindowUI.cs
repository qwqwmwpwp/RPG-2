using RPG4.Backpack.Core;
using UnityEngine;
using UnityEngine.InputSystem;

namespace RPG4.LoadoutTemplate
{
    /// <summary>常驻的窗口输入入口。窗口地址由 UI 创建，打开时通过事件请求角色连接。</summary>
    [DisallowMultipleComponent]
    public sealed class LoadoutWindowUI : MonoBehaviour
    {
        [SerializeField] private LoadoutTemplateUI bindings;
        [SerializeField] private GameObject windowRoot;
        [Tooltip("接线战利品 UI 根节点。打开战利品来源时显示，普通打开背包时隐藏。")]
        [SerializeField] private GameObject lootRoot;
        [SerializeField] private LootWindowUI lootBindings;
        internal LootWindowUI LootBindings => lootBindings;
        [SerializeField] private bool initiallyOpen;
        [SerializeField] private bool showCursor = true;
        private EventSubscription windowSubscription, stateSubscription;
        private object windowSource;
        private bool isOpen, cursorCaptured;
        private bool networkManaged, inputAllowed = true;
        internal void SetNetworkInput(bool allowed)
        {
            if (networkManaged && inputAllowed == allowed) return;
            networkManaged = true; inputAllowed = allowed;
            if (!allowed) ApplyOpen(false);
        }
        private bool savedCursorVisible;
        private CursorLockMode savedCursorLock;

        public bool IsOpen => isOpen;
        public bool IsLootOpen => isOpen && lootRoot != null && lootRoot.activeSelf;

        internal bool ValidateNetworkConfiguration(LoadoutTemplateUI expected, out string error)
        {
            error = null;
            if (expected == null || bindings != expected || windowRoot == null || transform.IsChildOf(windowRoot.transform)
                || !windowRoot.transform.IsChildOf(expected.transform))
            { error = "网络背包窗口须接线同一 UI 管理器和独立显隐根。"; return false; }
            return true;
        }

        private void OnEnable()
        {
            if (windowRoot == null || bindings == null)
            { Debug.LogWarning("请配置背包窗口根和 UI 绑定管理器。", this); return; }
            if (transform.IsChildOf(windowRoot.transform))
            { Debug.LogWarning("背包窗口监听器需要放在显隐根之外的常驻对象上。", this); return; }
            windowSource = bindings.WindowSource;
            windowSubscription = EventBus.Global.Subscribe<bool>(LoadoutChannels.WindowRequested,
                windowSource, this, ApplyOpen);
            stateSubscription = EventBus.Global.Subscribe(LoadoutChannels.WindowStateRequested,
                windowSource, this, PublishState);
            ApplyOpen(initiallyOpen);
        }

        private void Update()
        {
            if (inputAllowed && windowSource != null && Keyboard.current != null && Keyboard.current.tabKey.wasPressedThisFrame)
                RequestOpen(!isOpen);
        }

        public void OpenBackpack() => RequestOpen(true);

        /// <summary>显示背包和战利品面板，供本地或联机战利品会话绑定内容。</summary>
        internal void ShowLootWindow()
        {
            if (isActiveAndEnabled && windowSource != null && lootRoot != null) ApplyWindowState(true, true);
        }

        public void CloseLoot()
        {
            if (lootRoot != null) lootRoot.SetActive(false);
        }

        public void Close() => RequestOpen(false);

        private void RequestOpen(bool open)
        {
            if (isActiveAndEnabled && windowSource != null)
                EventBus.Global.Publish(LoadoutChannels.WindowRequested, windowSource, open);
        }

        private void OnDisable()
        {
            windowSubscription.Dispose();
            stateSubscription.Dispose();
            if (windowSource != null) ApplyOpen(false);
            RestoreCursor();
            windowSource = null;
        }

        private void ApplyOpen(bool open) => ApplyWindowState(open, false);

        private void ApplyWindowState(bool open, bool showLoot)
        {
            if (windowRoot == null || (networkManaged && (bindings == null || transform.IsChildOf(windowRoot.transform)
                || !windowRoot.transform.IsChildOf(bindings.transform))))
            { isOpen = false; RestoreCursor(); return; }
            open = open && inputAllowed;
            isOpen = open;
            // 先设置子面板，避免普通打开背包时战利品 UI 短暂触发启用回调。
            if (lootRoot != null) lootRoot.SetActive(open && showLoot);
            windowRoot.SetActive(open);
            if (open && showCursor && !cursorCaptured)
            {
                savedCursorVisible = Cursor.visible;
                savedCursorLock = Cursor.lockState;
                cursorCaptured = true;
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            if (!open) RestoreCursor();
            PublishState();
        }

        private void PublishState()
        {
            if (windowSource != null) EventBus.Global.Publish(LoadoutChannels.WindowChanged, windowSource, isOpen);
        }

        private void RestoreCursor()
        {
            if (!cursorCaptured) return;
            Cursor.lockState = savedCursorLock;
            Cursor.visible = savedCursorVisible;
            cursorCaptured = false;
        }
    }
}
