using UnityEngine;
using UnityEngine.InputSystem;
using RPG4.Backpack.Inventory;

namespace RPG4.Backpack.UI
{
    /// <summary>一个库存窗口持有的输入状态：打开时捕获，关闭时原样恢复；允许玩家来源晚初始化或更换。</summary>
    internal sealed class InventoryInputScope
    {
        private bool captured;
        private PlayerInput suspendedPlayer;
        private bool playerWasEnabled;
        private bool cursorWasVisible;
        private CursorLockMode cursorWasLocked;

        public void Capture(IBackpackView source)
        {
            if (!captured)
            {
                cursorWasVisible = Cursor.visible;
                cursorWasLocked = Cursor.lockState;
                captured = true;
            }
            SetSource(source);
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        public void SetSource(IBackpackView source)
        {
            if (!captured) return;
            Component owner = source as Component;
            PlayerInput player = owner != null ? owner.GetComponent<PlayerInput>() : null;
            if (suspendedPlayer == player) return;
            if (suspendedPlayer != null) suspendedPlayer.enabled = playerWasEnabled;
            suspendedPlayer = player;
            playerWasEnabled = player != null && player.enabled;
            if (player != null) player.enabled = false;
        }

        public void Release()
        {
            if (!captured) return;
            if (suspendedPlayer != null) suspendedPlayer.enabled = playerWasEnabled;
            suspendedPlayer = null;
            Cursor.lockState = cursorWasLocked;
            Cursor.visible = cursorWasVisible;
            captured = false;
        }
    }
}
