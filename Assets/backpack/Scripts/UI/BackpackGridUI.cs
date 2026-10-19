using System;
using System.Collections.Generic;
using Mirror;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using RPG4.Backpack.Core;
using RPG4.Backpack.Inventory;
using RPG4.Backpack.Network;
using RPG4.Backpack.Runtime;
using RPG4.Input;

namespace RPG4.Backpack.UI
{
    /// <summary>玩家背包窗口：保留场景配置、玩家来源解析与输入开关；显示委托给 BackpackContainerUI，携带交互由控制器处理。</summary>
    [DisallowMultipleComponent]
    public class BackpackGridUI : MonoBehaviour
    {
        #region 序列化字段

        public GameObject slotPrefab; // 格子预制体（需带 Image）。
        public GameObject itemPrefab; // 物品框预制体（根节点需挂 BackpackItemUI）。
        public List<BackpackItemUI> itemUIList; // 运行时活跃的物品框登记表（代码独占维护，勿在场景中预拖）。
        [Header("网格")]
        public int initialColumns = 4; // 未绑定数据源前的占位列数；绑定后按数据源的配置尺寸重建。
        public int initialRows = 5; // 未绑定数据源前的占位行数；绑定后按数据源的配置尺寸重建。
        public float cellSize = 100f; // 单格边长（像素）；面板与物品框尺寸都按它换算。
        public Vector2 slotSpacing; // 格子之间的间距（x 横向、y 纵向）。
        public RectTransform slotLayer; // 格子图层；有 GridLayoutGroup 时由其排布，否则脚本手工摆放。
        public RectTransform itemLayer; // 预制体中接线的物品图层。

        [Header("界面")]
        public TMP_Text hudHint; // 关闭背包时的操作提示。
        public TMP_Text overflowText; // 放不下的物品提示（窗口打开时显示）。
        public TMP_FontAsset uiFont; // 脚本运行时创建的格子 / 提示文本所用字体；留空用 TMP 默认字体（物品框文本由预制体自带字体）。

        public float bindingCheckInterval = 0.25f; // 本地玩家背包的重查周期（秒）。

        [Header("输入")]
        public string openKeyHint; // 关闭态提示里显示的按键名（仅文案）；留空时取开关键的实际绑定显示名，实际按键绑定在 InputActions.inputactions 的 backpack 映射上。

        [Header("交互")]
        public RectTransform cursorLayer; // 预制体中接线的携带预览图层。
        public BackpackInteractionController interaction; // 交互控制层；随本组件的数据源一起绑定 / 解绑，留空则只有网格表现。

        #endregion

        #region 输入状态

        /// <summary>背包输入动作的订阅句柄：三个动作、跨帧订阅状态与宿主缺失告警位；订阅 / 退订成对，回调用同一委托实例增删。</summary>
        private sealed class InputBinding
        {
            public InputAction toggle; // 开关动作（backpack/BackPackUI）。
            public InputAction primaryClick; // 左键动作（backpack/leftButton）。
            public InputAction secondaryClick; // 右键动作（backpack/rightButton）。
            public bool bound; // 是否已订阅；宿主缺失时保持 false，由 Update 逐帧重试。
            public bool warned; // 宿主缺失的警告是否已输出（只在状态翻转时打一条，避免逐帧刷屏）。

            private Action<InputAction.CallbackContext> toggleHandler; // 已注册的开关回调。
            private Action<InputAction.CallbackContext> primaryHandler; // 已注册的左键回调。
            private Action<InputAction.CallbackContext> secondaryHandler; // 已注册的右键回调。

            /// <summary>订阅宿主提供的三个动作；宿主缺失时返回 false（先退订，保证重复绑定不会叠加订阅）。</summary>
            public bool TryBind(
                InputManager host,
                Action<InputAction.CallbackContext> onToggle,
                Action<InputAction.CallbackContext> onPrimary,
                Action<InputAction.CallbackContext> onSecondary)
            {
                Unbind();
                if (host == null) return false;

                InputActions.BackpackActions actions = host.backpackInput;
                toggle = actions.BackPackUI;
                primaryClick = actions.leftButton;
                secondaryClick = actions.rightButton;

                // 映射的启用与释放由宿主独占，这里只增删回调。
                toggleHandler = onToggle;
                primaryHandler = onPrimary;
                secondaryHandler = onSecondary;
                toggle.performed += toggleHandler;
                primaryClick.performed += primaryHandler;
                secondaryClick.performed += secondaryHandler;
                bound = true;
                return true;
            }

            /// <summary>退订输入动作；映射的启用与释放由宿主独占，此处不触碰映射状态。</summary>
            public void Unbind()
            {
                if (toggle != null && toggleHandler != null) toggle.performed -= toggleHandler;
                if (primaryClick != null && primaryHandler != null) primaryClick.performed -= primaryHandler;
                if (secondaryClick != null && secondaryHandler != null) secondaryClick.performed -= secondaryHandler;

                toggle = null;
                primaryClick = null;
                secondaryClick = null;
                toggleHandler = null;
                primaryHandler = null;
                secondaryHandler = null;
                bound = false;
            }
        }

        /// <summary>打开背包前的输入与光标状态快照，关闭时原样还原。</summary>
        private struct WindowInputSnapshot
        {
            private bool inputWasEnabled; // 采集时的输入组件启用状态。
            private bool cursorWasVisible; // 采集时的光标可见状态。
            private CursorLockMode cursorWasLocked; // 采集时的光标锁定状态。

            /// <summary>采集当前输入与光标状态。</summary>
            public WindowInputSnapshot(PlayerInput playerInput)
            {
                inputWasEnabled = playerInput != null && playerInput.enabled;
                cursorWasVisible = Cursor.visible;
                cursorWasLocked = Cursor.lockState;
            }

            /// <summary>按采集值暂停本地玩家输入（原本就未启用时不写回）。</summary>
            public void SuspendInput(PlayerInput playerInput)
            {
                if (playerInput != null && inputWasEnabled) playerInput.enabled = false;
            }

            /// <summary>还原采集时的输入与光标状态。</summary>
            public void Restore(PlayerInput playerInput)
            {
                if (playerInput != null) playerInput.enabled = inputWasEnabled;
                Cursor.lockState = cursorWasLocked;
                Cursor.visible = cursorWasVisible;
            }
        }

        #endregion

        private readonly InputBinding binding = new InputBinding();
        private BackpackContainerUI containerUI;
        private IBackpackView boundInventory;
        private object boundEventSource;
        private PlayerInput playerInput;
        private WindowInputSnapshot windowSnapshot;
        private bool isOpen;
        private float nextBindingCheck;

        public BackpackContainerUI ContainerUI => containerUI;
        public int Columns => containerUI != null ? containerUI.Columns : 0;
        public int Rows => containerUI != null ? containerUI.Rows : 0;
        public IBackpackView Inventory => boundInventory;
        public bool IsOpen => isOpen;

        private BackpackContainerUI Display
        {
            get
            {
                EnsureDisplay();
                return containerUI;
            }
        }

        private string OpenKeyLabel
        {
            get
            {
                if (!string.IsNullOrEmpty(openKeyHint)) return openKeyHint;
                return binding.toggle != null ? binding.toggle.GetBindingDisplayString() : "未设置";
            }
        }

        #region 生命周期与来源

        private void Awake()
        {
            EnsureDisplay();
            containerUI.SetPresentationVisible(false);
        }

        private void Start() => Display.EnsureInitialized();

        private void OnEnable()
        {
            nextBindingCheck = 0f;
            SetWindowVisible(false);
            BindInputActions();
        }

        private void Update()
        {
            if (InputManager.Instance == null)
            {
                if (binding.bound) UnbindInputActions();
            }
            else if (!binding.bound) BindInputActions();

            if (Time.unscaledTime >= nextBindingCheck)
            {
                nextBindingCheck = Time.unscaledTime + Mathf.Max(0.05f, bindingCheckInterval);
                BindInventory(ResolveSource());
            }
        }

        private void OnDisable()
        {
            UnbindInputActions();
            Unbind();
        }

        private void OnDestroy() => Unbind();

        /// <summary>旧 Inspector 字段仍为兼容壳的配置入口，无需改已有场景和预制体。</summary>
        private void EnsureDisplay()
        {
            if (containerUI != null) return;
            containerUI = GetComponent<BackpackContainerUI>();
            if (containerUI == null) containerUI = gameObject.AddComponent<BackpackContainerUI>();
            if (itemUIList == null) itemUIList = new List<BackpackItemUI>();
            containerUI.slotPrefab = slotPrefab;
            containerUI.itemPrefab = itemPrefab;
            containerUI.itemUIList = itemUIList;
            containerUI.initialColumns = initialColumns;
            containerUI.initialRows = initialRows;
            containerUI.cellSize = cellSize;
            containerUI.slotSpacing = slotSpacing;
            containerUI.slotLayer = slotLayer;
            containerUI.itemLayer = itemLayer;
            containerUI.overflowText = overflowText;
            containerUI.uiFont = uiFont;
            containerUI.cursorLayer = cursorLayer;
        }

        private static IBackpackView ResolveSource()
        {
            NetworkIdentity localIdentity = NetworkClient.localPlayer;
            if (localIdentity != null)
            {
                BackpackNetworkInventory network = localIdentity.GetComponent<BackpackNetworkInventory>();
                if (network != null) return network;
            }

            return BackpackRegistry.TryGetPrimary(out BackpackInventory backpack) ? backpack : null;
        }

        private void BindInventory(IBackpackView source)
        {
            object eventSource = source?.EventSource;
            if (ReferenceEquals(boundInventory, source))
            {
                if (ReferenceEquals(boundEventSource, eventSource)) return;
                Display.BindInventory(source);
                boundEventSource = eventSource;
                // 同一宿主晚初始化或更换事件键时，补接交互订阅而不关闭窗口。
                if (interaction != null)
                {
                    interaction.Unbind();
                    interaction.Bind(source);
                }

                return;
            }

            Unbind();
            if (source == null) return;
            boundInventory = source;
            boundEventSource = eventSource;
            playerInput = (source as Component)?.GetComponent<PlayerInput>();
            Display.BindInventory(source);
            SetHint(OpenKeyLabel + "   /   打开背包");
            if (interaction != null) interaction.Bind(source);
        }

        private void Unbind()
        {
            if (interaction != null) interaction.Unbind();
            if (isOpen) RestoreInput();
            isOpen = false;
            boundInventory = null;
            boundEventSource = null;
            playerInput = null;
            if (containerUI != null) containerUI.UnbindInventory();
            SetWindowVisible(false);
            SetHint("等待本地玩家 · " + OpenKeyLabel + " 打开背包");
        }

        #endregion

        #region 输入与开关

        /// <summary>订阅输入宿主（InputManager）提供的 backpack 动作：取开关与左右键三个动作并注册回调；宿主缺失时只记一条警告、不做输入回退，界面照常可用，未绑定态由 Update 逐帧重试。</summary>
        private void BindInputActions()
        {
            if (binding.TryBind(InputManager.Instance, OnToggleAction, OnPrimaryClickAction, OnSecondaryClickAction))
            {
                binding.warned = false;
                return;
            }

            if (binding.warned) return;
            binding.warned = true;
            BackpackLog.Warn(LogModule.UI, "场景里没有 InputManager，背包收不到开关与点击。");
        }

        /// <summary>退订输入动作；映射的启用与释放由宿主独占，此处不触碰映射状态。</summary>
        private void UnbindInputActions() => binding.Unbind();

        /// <summary>开关动作（backpack/BackPackUI）：按下即请求切换。</summary>
        private void OnToggleAction(InputAction.CallbackContext context)
        {
            if (context.performed) Toggle();
        }

        /// <summary>左键动作（backpack/leftButton）：转交交互控制层入队。</summary>
        private void OnPrimaryClickAction(InputAction.CallbackContext context)
        {
            if (context.performed) PushClick(UiChannels.MouseButton.Left);
        }

        /// <summary>右键动作（backpack/rightButton）：转交交互控制层入队。</summary>
        private void OnSecondaryClickAction(InputAction.CallbackContext context)
        {
            if (context.performed) PushClick(UiChannels.MouseButton.Right);
        }

        /// <summary>把点击推给交互控制层；背包开关状态由控制层自行判定，此处只做转发。</summary>
        private void PushClick(int mouseButton)
        {
            if (ChestBackpackUI.BlocksPlayerBackpackInput) return;
            if (interaction != null) interaction.PushClick(mouseButton);
        }

        /// <summary>请求切换背包开关（输入动作与外部按钮的意图入口）：窗口、光标与输入都按同一条状态联动。</summary>
        public void Toggle()
        {
            if (ChestBackpackUI.BlocksPlayerBackpackInput) return;
            if (Inventory == null || Columns < 1 || Rows < 1) return;
            ApplyOpenState(!isOpen);
        }

        /// <summary>箱子窗口接管输入前关闭单栏，并取消未提交的携带。</summary>
        public void Close()
        {
            if (interaction != null) interaction.CancelInteraction();
            ApplyOpenState(false);
        }

        /// <summary>应用开关状态：打开时接管光标并暂停本地玩家输入，关闭时还原打开前保存的状态。</summary>
        private void ApplyOpenState(bool open)
        {
            if (isOpen == open) return;
            if (open)
            {
                Display.Redraw(); // 每次打开同步来源尺寸与显示。
                windowSnapshot = new WindowInputSnapshot(playerInput);
                windowSnapshot.SuspendInput(playerInput);
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            else
            {
                RestoreInput();
            }

            isOpen = open;
            SetWindowVisible(open);
            if (hudHint != null) hudHint.gameObject.SetActive(!open);
        }

        /// <summary>还原打开背包前保存的光标与输入状态。</summary>
        private void RestoreInput() => windowSnapshot.Restore(playerInput);

        /// <summary>用 CanvasGroup 显隐窗口：组件自身不做 SetActive，避免脚本挂在窗口上时被停用后无法再监听开关键。</summary>
        private void SetWindowVisible(bool visible)
        {
            GameObject target = gameObject;
            if (!target.TryGetComponent(out CanvasGroup canvasGroup)) canvasGroup = target.AddComponent<CanvasGroup>();
            canvasGroup.alpha = visible ? 1f : 0f;
            canvasGroup.interactable = visible;
            canvasGroup.blocksRaycasts = visible;
            if (containerUI != null) containerUI.SetPresentationVisible(visible);
            if (visible && !target.activeSelf) target.SetActive(true);
        }

        /// <summary>更新关闭态的操作提示。</summary>
        private void SetHint(string text)
        {
            if (hudHint == null) return;
            hudHint.text = text;
            hudHint.gameObject.SetActive(!isOpen);
        }

        #endregion
    }
}
