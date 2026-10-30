using UnityEngine;
using UnityEngine.InputSystem;
using RPG4.Backpack.Core;
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

        public string ToggleKeyLabel => toggleAction != null ? toggleAction.GetBindingDisplayString() : "未设置";

        private void Awake() => coordinator = GetComponent<InventoryUICoordinator>();

        private void OnEnable() => BindInputActions();

        private void OnDisable() => UnbindInputActions();

        private void OnDestroy() => UnbindInputActions();

        private void Update()
        {
            BindInputActions();
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
            }
            bool panelActive = inspectionPanel != null && inspectionPanel.IsFor(window);
            bool panelPointer = panelActive && inspectionPanel.BlocksInventoryPointer(pointer);
            if (panelActive && !panelPointer && InputManager.HasPointer && Keyboard.current != null && Keyboard.current.fKey.wasPressedThisFrame)
                inspectionPanel.InspectAtPointer(pointer);
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
            toggleAction.performed += OnToggle;
            primaryAction.performed += OnPrimaryClick;
            secondaryAction.performed += OnSecondaryClick;
            inputWarningLogged = false;
        }

        private void UnbindInputActions()
        {
            if (toggleAction != null) toggleAction.performed -= OnToggle;
            if (primaryAction != null) primaryAction.performed -= OnPrimaryClick;
            if (secondaryAction != null) secondaryAction.performed -= OnSecondaryClick;
            inputHost = null;
            inspectionWindow = null;
            inspectionPanel = null;
            toggleAction = primaryAction = secondaryAction = null;
            clickWindow = null;
            primaryClick = secondaryClick = false;
            quickTransferClick = chooseSplitQuantityClick = false;
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
