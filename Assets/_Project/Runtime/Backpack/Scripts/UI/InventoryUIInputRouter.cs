using UnityEngine;
using UnityEngine.InputSystem;
using RPG4.Backpack.Core;
using RPG4.Backpack.Inventory;
using RPG4.Backpack.Items;
using TMPro;
using UnityEngine.EventSystems;
using RPG4.Input;
using RPG4.UI;

namespace RPG4.Backpack.UI
{
    /// <summary>库存输入入口：采集输入，将开关键交给协调器，并向当前会话转发本帧输入。</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(InventoryUICoordinator))]
    [DefaultExecutionOrder(50)]
    public sealed class InventoryUIInputRouter : MonoBehaviour
    {
        private InventoryUICoordinator coordinator;
        private InputManager inputHost;
        private InputAction toggleAction;
        private InputAction useAction;
        private static InventoryUIInputRouter activeRouter;
        private static int nextConsumableRequest;
        private int pendingConsumable;
        private ItemId pendingConsumableItem;
        private IBackpackView pendingConsumableSource;
        private UIBase consumableWindow;
        private BackpackInteractionController consumableInteraction;
        private int consumableSessionVersion;
        private EventSubscription consumableResultSubscription;
        private float consumableDeadline;
        private string consumableFeedback;
        private float consumableFeedbackUntil;
        public static bool IsConsumablePending => activeRouter != null && activeRouter.pendingConsumable != 0;
        public static string ConsumableFeedback => activeRouter != null && Time.unscaledTime < activeRouter.consumableFeedbackUntil
            ? activeRouter.consumableFeedback : string.Empty;
        private InputAction primaryAction;
        private InputAction secondaryAction;
        private UIBase clickWindow;
        private int clickSessionVersion;
        private bool primaryClick;
        private bool secondaryClick;
        private bool quickTransferClick;
        private bool chooseSplitQuantityClick;
        private bool inputWarningLogged;
        private UIBase inspectionWindow;
        private WeaponInspectionPanel inspectionPanel;
        private CharacterOverviewUI overview;

        public string ToggleKeyLabel => toggleAction != null ? toggleAction.GetBindingDisplayString() : "未设置";

        private void Awake() => coordinator = GetComponent<InventoryUICoordinator>();

        private void OnEnable() { activeRouter = this; BindInputActions(); }

        private void OnDisable() { UnbindInputActions(); ClearConsumableRequest(); if (activeRouter == this) activeRouter = null; }

        private void OnDestroy() { UnbindInputActions(); ClearConsumableRequest(); if (activeRouter == this) activeRouter = null; }

        private void Update()
        {
            BindInputActions();
            if (pendingConsumable != 0 && (!ReferenceEquals(LocalBackpackSourceResolver.Resolve(), pendingConsumableSource)
                || pendingConsumableSource == null || !pendingConsumableSource.IsReady)) ClearConsumableRequest();
            if (pendingConsumable != 0 && Time.unscaledTime >= consumableDeadline)
            {
                ClearConsumableRequest();
                ShowConsumableFeedback("等待用药确认超时，请以实际状态为准");
            }
            bool left = primaryClick;
            bool right = secondaryClick;
            bool quickTransfer = quickTransferClick;
            bool chooseSplitQuantity = chooseSplitQuantityClick;
            UIBase clicked = clickWindow;
            int clickedSession = clickSessionVersion;
            primaryClick = secondaryClick = false;
            quickTransferClick = chooseSplitQuantityClick = false;
            clickWindow = null;
            if (inputHost == null || coordinator == null
                || !coordinator.TryGetActiveSession(out UIBase window, out BackpackInteractionController interaction)) return;
            if (interaction == null) return;
            InputActions.BackpackActions actions = inputHost.backpackInput;
            Vector2 pointer = actions.pointerPosition.ReadValue<Vector2>();
            if (inspectionWindow != window || inspectionPanel == null)
            {
                inspectionWindow = window;
                inspectionPanel = window.GetComponentInChildren<WeaponInspectionPanel>(true);
                overview = window.GetComponent<CharacterOverviewUI>();
            }
            bool panelActive = inspectionPanel != null && inspectionPanel.IsFor(window);
            bool panelPointer = panelActive && inspectionPanel.BlocksInventoryPointer(pointer);
            if (!panelPointer && InputManager.HasPointer && Keyboard.current != null && Keyboard.current.fKey.wasPressedThisFrame)
            {
                if (overview != null) overview.InspectAtPointer(pointer);
                else if (panelActive) inspectionPanel.InspectAtPointer(pointer);
            }
            interaction.ReceiveInput(new BackpackInteractionController.SessionInput(
                pointer, InputManager.HasPointer && !panelPointer,
                !panelPointer && clicked == window && clickedSession == interaction.SessionVersion && left,
                !panelPointer && clicked == window && clickedSession == interaction.SessionVersion && right,
                actions.cancel.WasPressedThisFrame(), !panelPointer && quickTransfer, !panelPointer && chooseSplitQuantity));
        }

        private void BindInputActions()
        {
            InputManager host = InputManager.Instance;
            if (host != null && host == inputHost && toggleAction != null) return;
            UnbindInputActions();
            if (host == null)
            {
                if (!inputWarningLogged)
                {
                    inputWarningLogged = true;
                    BackpackLog.Warn(LogModule.UI, "场景里没有 InputManager，库存界面收不到输入。");
                }
                return;
            }
            inputHost = host;
            InputActions.BackpackActions actions = host.backpackInput;
            toggleAction = actions.BackPackUI;
            primaryAction = actions.leftButton;
            secondaryAction = actions.rightButton;
            useAction = actions.useConsumable;
            toggleAction.performed += OnToggle;
            useAction.performed += OnUseConsumable;
            primaryAction.performed += OnPrimaryClick;
            secondaryAction.performed += OnSecondaryClick;
            inputWarningLogged = false;
        }

        private void UnbindInputActions()
        {
            if (toggleAction != null) toggleAction.performed -= OnToggle;
            if (useAction != null) useAction.performed -= OnUseConsumable;
            if (primaryAction != null) primaryAction.performed -= OnPrimaryClick;
            if (secondaryAction != null) secondaryAction.performed -= OnSecondaryClick;
            inputHost = null;
            inspectionWindow = null;
            inspectionPanel = null;
            overview = null;
            toggleAction = primaryAction = secondaryAction = useAction = null;
            clickWindow = null;
            primaryClick = secondaryClick = false;
            quickTransferClick = chooseSplitQuantityClick = false;
        }

        private void OnUseConsumable(InputAction.CallbackContext context)
        {
            if (!Application.isFocused || Time.timeScale <= 0f || Cursor.lockState != CursorLockMode.Locked
                || (UIManager.Instance != null && UIManager.Instance.BlocksGameplayInput)) return;
            GameObject selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            if (selected != null && selected.GetComponent<TMP_InputField>() != null) return;
            IBackpackView inventory = LocalBackpackSourceResolver.Resolve();
            Component owner = inventory as Component;
            RPG4.Input.PlayerInput playerInput = owner != null ? owner.GetComponent<RPG4.Input.PlayerInput>() : null;
            // Consumable use is a local gameplay action and must remain available with no weapon equipped.
            if (playerInput == null || !playerInput.UsesLocalInput || !playerInput.LocalGameplayInputAllowed
                || !playerInput.isActiveAndEnabled) return;
            RuntimeItem item = ConsumableQuickUse.Resolve(inventory, out _);
            if (item == null) { ShowConsumableFeedback("快捷药品已用完 · 在背包中选择药品"); return; }
            BeginConsumable(inventory, item.ItemId, null);
        }

        public static void RequestConsumable(IBackpackView inventory, ItemId itemId, UIBase window)
        {
            if (activeRouter != null) activeRouter.BeginConsumable(inventory, itemId, window);
        }

        private void BeginConsumable(IBackpackView inventory, ItemId itemId, UIBase window)
        {
            if (pendingConsumable != 0) return;
            if (inventory == null || !inventory.IsReady || !ReferenceEquals(inventory, LocalBackpackSourceResolver.Resolve())
                || !ConsumableQuickUse.IsUsable(ConsumableQuickUse.Find(inventory, itemId)))
            { ShowConsumableFeedback("药品不在自己的背包中，或已用完"); return; }
            if (!InventoryHostCore.TryGetCommandHost(inventory.EventSource, out InventoryHostCore host))
            { ShowConsumableFeedback("背包尚未就绪"); return; }
            if (host.GetConsumableUseState().Active) { ShowConsumableFeedback("正在用药"); return; }
            pendingConsumable = ++nextConsumableRequest;
            if (pendingConsumable == 0) pendingConsumable = ++nextConsumableRequest;
            pendingConsumableItem = itemId;
            pendingConsumableSource = inventory;
            consumableWindow = window;
            consumableInteraction = window != null ? window.GetComponent<BackpackInteractionController>() : null;
            consumableSessionVersion = consumableInteraction != null ? consumableInteraction.SessionVersion : -1;
            consumableDeadline = Time.unscaledTime + 5f;
            object source = inventory.EventSource;
            consumableResultSubscription = EventBus.Global.Subscribe<ConsumableUseResult>(
                InvChannels.ConsumableOperationCompleted, source, this, OnConsumableResult);
            ShowConsumableFeedback("等待用药确认…");
            EventBus.Global.Publish(CmdChannels.UseConsumable, source, new ConsumableUseOperation(pendingConsumable, itemId));
        }

        private void OnConsumableResult(ConsumableUseResult result)
        {
            if (result.RequestId != pendingConsumable || result.ItemId != pendingConsumableItem
                || !ReferenceEquals(pendingConsumableSource, LocalBackpackSourceResolver.Resolve())) return;
            UIBase window = consumableWindow;
            bool sameSession = consumableInteraction != null && consumableInteraction.IsSessionActive
                && consumableInteraction.SessionVersion == consumableSessionVersion;
            ClearConsumableRequest();
            ShowConsumableFeedback(result.Success ? "用药中 · 开火、换弹或切枪可取消"
                : string.IsNullOrEmpty(result.Error) ? "暂时无法使用药品" : result.Error);
            if (result.Success && sameSession && window != null && window.IsOpen && window.Manager != null) window.Manager.Close(window);
        }

        private void ClearConsumableRequest()
        {
            consumableResultSubscription.Dispose();
            consumableResultSubscription = default;
            pendingConsumable = 0;
            pendingConsumableItem = ItemId.None;
            pendingConsumableSource = null;
            consumableWindow = null;
            consumableInteraction = null;
        }
        private void ShowConsumableFeedback(string value)
        {
            consumableFeedback = value;
            consumableFeedbackUntil = Time.unscaledTime + 4f;
        }

        private void OnToggle(InputAction.CallbackContext context)
        {
            if (coordinator == null || !coordinator.isActiveAndEnabled) return;
            clickWindow = null;
            primaryClick = secondaryClick = false;
            quickTransferClick = chooseSplitQuantityClick = false;
            coordinator.ToggleInventory();
        }

        private void OnPrimaryClick(InputAction.CallbackContext context) => QueueClick(true);

        private void OnSecondaryClick(InputAction.CallbackContext context) => QueueClick(false);

        private void QueueClick(bool primary)
        {
            if (coordinator == null
                || !coordinator.TryGetActiveSession(out UIBase window, out BackpackInteractionController interaction)
                || interaction == null || !interaction.IsSessionActive) return;
            if (clickWindow != window || clickSessionVersion != interaction.SessionVersion)
            {
                primaryClick = secondaryClick = false;
                quickTransferClick = chooseSplitQuantityClick = false;
                clickWindow = window;
                clickSessionVersion = interaction.SessionVersion;
            }
            if (primary)
            {
                primaryClick = true;
                quickTransferClick = Keyboard.current != null && Keyboard.current.ctrlKey.isPressed;
            }
            else
            {
                secondaryClick = true;
                chooseSplitQuantityClick = Keyboard.current != null && Keyboard.current.shiftKey.isPressed;
            }
        }
    }
}
