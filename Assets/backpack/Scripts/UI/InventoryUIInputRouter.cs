using UnityEngine;
using UnityEngine.InputSystem;
using RPG4.Backpack.Core;
using RPG4.Input;
using RPG4.UI;

namespace RPG4.Backpack.UI
{
    /// <summary>库存界面的唯一输入入口，挂在 UIManager 上；窗口更新来源后，向当前会话转发本帧输入。</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(UIManager))]
    [DefaultExecutionOrder(50)]
    public sealed class InventoryUIInputRouter : MonoBehaviour
    {
        private UIManager manager;
        private InputManager inputHost;
        private InputAction toggleAction;
        private InputAction primaryAction;
        private InputAction secondaryAction;
        private UIBase clickWindow;
        private int clickSessionVersion;
        private bool primaryClick;
        private bool secondaryClick;
        private bool inputWarningLogged;
        private int lastToggleFrame = -1;

        public string ToggleKeyLabel => toggleAction != null ? toggleAction.GetBindingDisplayString() : "未设置";

        private void Awake() => manager = GetComponent<UIManager>();

        private void OnEnable() => BindInputActions();

        private void OnDisable() => UnbindInputActions();

        private void OnDestroy() => UnbindInputActions();

        private void Update()
        {
            BindInputActions();
            bool left = primaryClick;
            bool right = secondaryClick;
            UIBase clicked = clickWindow;
            int clickedSession = clickSessionVersion;
            primaryClick = secondaryClick = false;
            clickWindow = null;
            if (inputHost == null || !TryGetActiveWindow(out UIBase window, out BackpackInteractionController interaction)) return;
            if (interaction == null) return;
            InputActions.BackpackActions actions = inputHost.backpackInput;
            interaction.ReceiveInput(new BackpackInteractionController.SessionInput(
                actions.pointerPosition.ReadValue<Vector2>(), InputManager.HasPointer,
                clicked == window && clickedSession == interaction.SessionVersion && left,
                clicked == window && clickedSession == interaction.SessionVersion && right,
                actions.cancel.WasPressedThisFrame()));
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
            toggleAction = primaryAction = secondaryAction = null;
            clickWindow = null;
            primaryClick = secondaryClick = false;
        }

        private void OnToggle(InputAction.CallbackContext context)
        {
            if (manager == null || !manager.isActiveAndEnabled || lastToggleFrame == Time.frameCount) return;
            lastToggleFrame = Time.frameCount;
            clickWindow = null;
            primaryClick = secondaryClick = false;
            if (TryGetActiveWindow(out UIBase window, out _)) window.Close();
            else manager.Toggle<BackpackGridUI>();
        }

        private void OnPrimaryClick(InputAction.CallbackContext context) => QueueClick(true);

        private void OnSecondaryClick(InputAction.CallbackContext context) => QueueClick(false);

        private void QueueClick(bool primary)
        {
            if (!TryGetActiveWindow(out UIBase window, out BackpackInteractionController interaction)
                || interaction == null || !interaction.IsSessionActive) return;
            if (clickWindow != window || clickSessionVersion != interaction.SessionVersion)
            {
                primaryClick = secondaryClick = false;
                clickWindow = window;
                clickSessionVersion = interaction.SessionVersion;
            }
            if (primary) primaryClick = true;
            else secondaryClick = true;
        }

        private bool TryGetActiveWindow(out UIBase window, out BackpackInteractionController interaction)
        {
            window = null;
            interaction = null;
            if (manager == null || !manager.isActiveAndEnabled) return false;
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
