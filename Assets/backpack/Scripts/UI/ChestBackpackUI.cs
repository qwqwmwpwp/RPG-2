using Mirror;
using UnityEngine;
using UnityEngine.InputSystem;
using RPG4.Backpack.Core;
using RPG4.Backpack.Inventory;
using RPG4.Backpack.Network;
using RPG4.Backpack.Runtime;
using RPG4.Input;

namespace RPG4.Backpack.UI
{
    /// <summary>箱子双栏窗口：左栏绑定玩家、右栏绑定指定箱子；两个显示组件共用转移与拆分会话，窗口统一管理输入与光标。</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(RectTransform), typeof(CanvasGroup), typeof(ChestBackpackUIData))]
    [DefaultExecutionOrder(10)]
    public sealed class ChestBackpackUI : MonoBehaviour
    {
        [SerializeField] private ChestBackpackUIData ui;

        [Header("背包来源")]
        [Tooltip("留空时寻找本地玩家背包；指定后始终使用这个玩家背包。")]
        [SerializeField] private BackpackInventory playerInventory;
        [Tooltip("初始箱子；也可由后续箱子交互调用 Open(chest) 指定。")]
        [SerializeField] private ChestInventory defaultChest;
        [SerializeField] private bool resolveLocalPlayer = true;
        [SerializeField] private bool openOnStart = true;
        [SerializeField, Min(0.05f)] private float bindingCheckInterval = 0.25f;

        [Header("双栏布局")]
        [SerializeField] private Vector2 padding = new Vector2(24f, 24f);
        [SerializeField, Min(0f)] private float paneSpacing = 48f;
        [SerializeField, Min(0f)] private float titleHeight = 40f;

        [Header("标题文本")]
        [Tooltip("使用中文标题时，请在标题的 TextMeshProUGUI 组件上指定包含中文字形的字体。")]
        [SerializeField] private string playerTitleText = "Player Backpack";
        [SerializeField] private string chestTitleText = "Chest Backpack";
        [SerializeField] private string waitingPlayerTitleText = "Player Backpack (waiting)";
        [SerializeField] private string unboundChestTitleText = "Chest Backpack (unbound)";

        private RectTransform windowRect;
        private Vector2 lastPlayerSize;
        private Vector2 lastChestSize;
        private bool layoutDirty = true;
        private bool configurationWarned;
        private float nextBindingCheck;
        private InputManager inputHost;
        private InputAction primaryAction;
        private InputAction secondaryAction;
        private InputAction closeAction;
        private bool inputCaptured;
        private PlayerInput suspendedPlayer;
        private bool playerWasEnabled;
        private bool cursorWasVisible;
        private CursorLockMode cursorWasLocked;
        private static int lastClosedFrame = -1;

        public static ChestBackpackUI ActiveWindow { get; private set; }
        // 同一帧关闭后仍拦截旧背包回调，避免同一次开关键又打开单栏窗口。
        public static bool BlocksPlayerBackpackInput => ActiveWindow != null || lastClosedFrame == Time.frameCount;

        public bool IsOpen { get; private set; }
        public BackpackContainerUI PlayerView => ui != null ? ui.playerView : null;
        public BackpackContainerUI ChestView => ui != null ? ui.chestView : null;
        public IBackpackView PlayerInventory => PlayerView != null ? PlayerView.Inventory : null;
        public IBackpackView ChestInventory => ChestView != null ? ChestView.Inventory : null;

        private void Awake()
        {
            EnsureInitialized();
            SetVisible(false);
        }

        private void Start()
        {
            if (openOnStart) Open(defaultChest);
        }

        private void OnEnable()
        {
            nextBindingCheck = 0f;
            if (!IsOpen) return;
            RefreshBindings();
        }

        private void Update()
        {
            if (!IsOpen) return;
            BindInputActions();
            if (Time.unscaledTime >= nextBindingCheck)
            {
                nextBindingCheck = Time.unscaledTime + Mathf.Max(0.05f, bindingCheckInterval);
                RefreshBindings();
            }
            SuspendPlayerInput();
        }

        private void LateUpdate()
        {
            if (IsOpen) ArrangeViews();
        }

        private void OnDisable() => Close();
        private void OnDestroy() => Close();

        /// <summary>显示指定箱子的双栏背包；来源尚未初始化时显示占位格并等待就绪。传 null 可查看未绑定箱子的基础布局。</summary>
        public bool Open(ChestInventory chest)
        {
            defaultChest = chest;
            if (!EnsureInitialized()) return false;

            if (ActiveWindow != null && ActiveWindow != this) ActiveWindow.Close();
            if (!IsOpen)
            {
                // 先还原旧单栏窗口的快照，再采集箱子窗口的原始状态。
                foreach (BackpackGridUI window in FindObjectsOfType<BackpackGridUI>()) window.Close();
            }
            ui.interaction.CancelInteraction();

            enabled = true;
            if (!gameObject.activeSelf) gameObject.SetActive(true);
            IsOpen = true;
            ActiveWindow = this;
            nextBindingCheck = 0f;
            RefreshBindings();
            ui.interaction.BindContainers(ui.playerView, ui.chestView);
            ui.interaction.enabled = true;
            CaptureInput();
            BindInputActions();
            SetVisible(true);
            ArrangeViews();
            return true;
        }

        /// <summary>显式指定玩家与箱子，左右来源不会随鼠标位置或其他背包窗口改变。</summary>
        public bool Open(BackpackInventory player, ChestInventory chest)
        {
            playerInventory = player;
            return Open(chest);
        }

        /// <summary>隐藏窗口并释放双方显示订阅；保留格子配置，再次 Open 时重新获取与显示库存。</summary>
        public void Close()
        {
            if (ui != null && ui.interaction != null)
            {
                ui.interaction.Unbind();
                ui.interaction.enabled = false;
            }
            UnbindInputActions();
            IsOpen = false;
            if (ui != null && ui.playerView != null)
            {
                ui.playerView.UnbindInventory();
                ui.playerView.SetPresentationVisible(false);
            }

            if (ui != null && ui.chestView != null)
            {
                ui.chestView.UnbindInventory();
                ui.chestView.SetPresentationVisible(false);
            }

            SetVisible(false);
            RestoreInput();
            if (ActiveWindow == this)
            {
                ActiveWindow = null;
                lastClosedFrame = Time.frameCount;
            }
        }

        private void BindInputActions()
        {
            InputManager host = InputManager.Instance;
            if (host == null)
            {
                UnbindInputActions();
                return;
            }
            if (inputHost == host && primaryAction != null) return;
            UnbindInputActions();
            inputHost = host;
            InputActions.BackpackActions actions = host.backpackInput;
            primaryAction = actions.leftButton;
            secondaryAction = actions.rightButton;
            closeAction = actions.BackPackUI;
            primaryAction.performed += OnPrimaryClick;
            secondaryAction.performed += OnSecondaryClick;
            closeAction.performed += OnCloseAction;
        }

        private void UnbindInputActions()
        {
            if (primaryAction != null) primaryAction.performed -= OnPrimaryClick;
            if (secondaryAction != null) secondaryAction.performed -= OnSecondaryClick;
            if (closeAction != null) closeAction.performed -= OnCloseAction;
            primaryAction = null;
            secondaryAction = null;
            closeAction = null;
            inputHost = null;
        }

        private void OnPrimaryClick(InputAction.CallbackContext context)
        {
            if (IsOpen && ActiveWindow == this && context.performed) ui.interaction.PushClick(UiChannels.MouseButton.Left);
        }

        private void OnSecondaryClick(InputAction.CallbackContext context)
        {
            if (IsOpen && ActiveWindow == this && context.performed) ui.interaction.PushClick(UiChannels.MouseButton.Right);
        }

        private void OnCloseAction(InputAction.CallbackContext context)
        {
            if (IsOpen && ActiveWindow == this && context.performed) Close();
        }

        private void CaptureInput()
        {
            if (!inputCaptured)
            {
                cursorWasVisible = Cursor.visible;
                cursorWasLocked = Cursor.lockState;
                inputCaptured = true;
            }
            SuspendPlayerInput();
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void SuspendPlayerInput()
        {
            Component source = PlayerInventory as Component;
            PlayerInput player = source != null ? source.GetComponent<PlayerInput>() : null;
            if (suspendedPlayer == player) return;
            if (suspendedPlayer != null) suspendedPlayer.enabled = playerWasEnabled;
            suspendedPlayer = player;
            playerWasEnabled = player != null && player.enabled;
            if (player != null) player.enabled = false;
        }

        private void RestoreInput()
        {
            if (!inputCaptured) return;
            if (suspendedPlayer != null) suspendedPlayer.enabled = playerWasEnabled;
            suspendedPlayer = null;
            Cursor.lockState = cursorWasLocked;
            Cursor.visible = cursorWasVisible;
            inputCaptured = false;
        }

        /// <summary>重新获取双方数据源；这里只解析玩家来源，箱子始终由 defaultChest 或 Open 参数指定。</summary>
        public void RefreshBindings()
        {
            if (!IsOpen || !EnsureInitialized()) return;

            IBackpackView player = ResolvePlayer();
            BackpackInventory chest = defaultChest != null ? defaultChest.Inventory : null;
            if (chest != null && !chest.IsInitialized) chest = null;

            ui.playerView.BindInventory(player);
            ui.chestView.BindInventory(chest);
            ui.playerView.SetPresentationVisible(true);
            ui.chestView.SetPresentationVisible(true);
            ui.playerTitle.text = player != null ? playerTitleText : waitingPlayerTitleText;
            ui.chestTitle.text = chest != null ? chestTitleText : unboundChestTitleText;
            layoutDirty = true;
        }

        private IBackpackView ResolvePlayer()
        {
            // 接口的普通 null 判断不识别已销毁的 Unity 组件，显式来源先用具体组件判断存活。
            if (!ReferenceEquals(playerInventory, null))
            {
                return playerInventory != null && playerInventory.IsInitialized ? playerInventory : null;
            }

            if (!resolveLocalPlayer) return null;

            NetworkIdentity localIdentity = NetworkClient.localPlayer;
            if (localIdentity != null)
            {
                BackpackNetworkInventory network = localIdentity.GetComponent<BackpackNetworkInventory>();
                if (network != null && network.EventSource != null) return network;
            }

            return BackpackRegistry.TryGetPrimary(out BackpackInventory backpack) && backpack.IsInitialized
                ? backpack
                : null;
        }

        private bool EnsureInitialized()
        {
            if (windowRect == null) windowRect = GetComponent<RectTransform>();
            if (ui == null) ui = GetComponent<ChestBackpackUIData>();
            if (ui == null || ui.windowGroup == null || ui.playerView == null || ui.chestView == null
                || ui.playerView == ui.chestView || ui.interaction == null
                || ui.playerTitle == null || ui.chestTitle == null || ui.cursorLayer == null
                || ui.playerView.cursorLayer != ui.cursorLayer || ui.chestView.cursorLayer != ui.cursorLayer)
            {
                if (!configurationWarned)
                {
                    configurationWarned = true;
                    BackpackLog.Warn(LogModule.UI, "请在预制体的 ChestBackpackUIData 中接线窗口、两个不同的容器、交互组件、标题和共用携带预览层。");
                }
                return false;
            }

            configurationWarned = false;
            if (!ui.playerView.EnsureInitialized() || !ui.chestView.EnsureInitialized()) return false;
            if (!IsOpen) ui.interaction.enabled = false;
            return true;
        }

        private void ArrangeViews()
        {
            if (ui.playerView == null || ui.chestView == null || windowRect == null) return;
            RectTransform playerRect = ui.playerView.GetComponent<RectTransform>();
            RectTransform chestRect = ui.chestView.GetComponent<RectTransform>();
            Vector2 playerSize = playerRect.sizeDelta;
            Vector2 chestSize = chestRect.sizeDelta;
            if (!layoutDirty && playerSize == lastPlayerSize && chestSize == lastChestSize) return;

            layoutDirty = false;
            lastPlayerSize = playerSize;
            lastChestSize = chestSize;
            float left = Mathf.Max(0f, padding.x);
            float top = Mathf.Max(0f, padding.y);
            float spacing = Mathf.Max(0f, paneSpacing);
            float header = Mathf.Max(0f, titleHeight);
            float rightX = left + playerSize.x + spacing;

            PlaceRect(playerRect, new Vector2(left, -top - header));
            PlaceRect(chestRect, new Vector2(rightX, -top - header));
            PlaceRect(ui.playerTitle.rectTransform, new Vector2(left, -top));
            PlaceRect(ui.chestTitle.rectTransform, new Vector2(rightX, -top));
            ui.playerTitle.rectTransform.sizeDelta = new Vector2(playerSize.x, header);
            ui.chestTitle.rectTransform.sizeDelta = new Vector2(chestSize.x, header);
            windowRect.sizeDelta = new Vector2(
                left * 2f + playerSize.x + spacing + chestSize.x,
                top * 2f + header + Mathf.Max(playerSize.y, chestSize.y));
            ui.cursorLayer.SetAsLastSibling();
        }

        private static void PlaceRect(RectTransform rect, Vector2 position)
        {
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = position;
        }

        private void SetVisible(bool visible)
        {
            if (ui == null || ui.windowGroup == null) return;
            ui.windowGroup.alpha = visible ? 1f : 0f;
            ui.windowGroup.interactable = false;
            ui.windowGroup.blocksRaycasts = false;
        }
    }
}
