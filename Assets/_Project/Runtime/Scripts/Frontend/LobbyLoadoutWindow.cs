using System;
using RPG4.Backpack.Inventory;
using RPG4.Backpack.UI;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
namespace RPG4.Frontend
{
    [DefaultExecutionOrder(50)]
    public sealed class LobbyLoadoutWindow : FrontendWindow
    {
        [SerializeField] private BackpackContainerUI backpack, equipment, stash;
        [SerializeField] private BackpackInteractionController interaction;
        [SerializeField] private Button closeButton;
        public event Action Closed;
        public void Bind(IBackpackView pack, IBackpackView gear, IBackpackView storage)
        {
            bool changed = !ReferenceEquals(backpack.Inventory, pack) || !ReferenceEquals(equipment.Inventory, gear) || !ReferenceEquals(stash.Inventory, storage);
            if (changed) interaction.EndSession();
            backpack.BindInventory(pack); equipment.BindInventory(gear); stash.BindInventory(storage);
            if (changed && IsOpen) BeginInteraction();
        }
        protected override void Awake() { base.Awake(); closeButton.onClick.AddListener(Close); CloseRequested += Close; }
        protected override bool CanOpen() => backpack.Inventory != null && backpack.Inventory.IsReady
            && equipment.Inventory != null && equipment.Inventory.IsReady && stash.Inventory != null && stash.Inventory.IsReady;
        protected override void OnOpened() { base.OnOpened(); BeginInteraction(); }
        private void BeginInteraction()
        {
            backpack.SetPresentationVisible(true); equipment.SetPresentationVisible(true); stash.SetPresentationVisible(true);
            interaction.BeginSession(backpack, equipment, stash);
            interaction.SetInspectableContainers(backpack, equipment, stash);
            interaction.SetQuickTransferPair(backpack, stash, "存入仓库", "放入背包");
        }
        protected override void OnClosed() { interaction.EndSession(); base.OnClosed(); Closed?.Invoke(); }
        protected override void Update()
        {
            if (!CanProcessWindowInput) return;
            var mouse = Mouse.current; var keyboard = Keyboard.current;
            bool cancel = keyboard != null && keyboard.escapeKey.wasPressedThisFrame;
            // Escape first cancels an active drag/split; a second press closes the window.
            if (!cancel || !interaction.IsCarrying && !interaction.IsChoosingSplitQuantity) base.Update();
            if (!IsOpen) return;
            bool shift = keyboard != null && keyboard.shiftKey.isPressed;
            interaction.ReceiveInput(new BackpackInteractionController.SessionInput(
                mouse != null ? mouse.position.ReadValue() : Vector2.zero, mouse != null,
                mouse != null && mouse.leftButton.wasPressedThisFrame, mouse != null && mouse.rightButton.wasPressedThisFrame,
                cancel, shift && mouse != null && mouse.leftButton.wasPressedThisFrame,
                shift && mouse != null && mouse.rightButton.wasPressedThisFrame));
        }
        protected override void OnDestroy() { CloseRequested -= Close; if (closeButton != null) closeButton.onClick.RemoveListener(Close); base.OnDestroy(); }
    }
}
