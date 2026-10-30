using Mirror;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace RPG4.UI
{
    /// <summary>死亡模态窗口；先关闭库存会话，本地重新开始与网络重生严格分开。</summary>
    [DisallowMultipleComponent]
    public sealed class PlayerDeathUI : UIBase
    {
        [SerializeField] private CharacterHealthView healthView;
        [SerializeField] private Button restartButton;
        [SerializeField] private TMP_Text statusLabel;
        private bool alive = true, reloading, cursorCaptured;
        private bool previousCursorVisible;
        private CursorLockMode previousLock;

        protected override void Awake()
        {
            base.Awake();
            if (restartButton != null) restartButton.onClick.AddListener(Restart);
        }
        protected override void OnDestroy()
        {
            if (restartButton != null) restartButton.onClick.RemoveListener(Restart);
            base.OnDestroy();
        }
        public void SetLifeState(bool value)
        {
            alive = value;
            if (alive) { Close(); return; }
            if (!IsOpen)
            {
                UIManager host = Manager != null ? Manager : UIManager.Instance;
                if (host != null) host.CloseAll();
                Open();
            }
            if (!reloading) RefreshAction();
        }
        protected override bool CanOpen() => !alive && healthView != null && healthView.SourceAttributes != null;
        protected override void OnOpened()
        {
            previousLock = Cursor.lockState;
            previousCursorVisible = Cursor.visible;
            cursorCaptured = true;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            RefreshAction();
            if (restartButton != null && restartButton.interactable && EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(restartButton.gameObject);
        }
        protected override void OnClosed()
        {
            if (!cursorCaptured) return;
            if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null
                && EventSystem.current.currentSelectedGameObject.transform.IsChildOf(transform))
                EventSystem.current.SetSelectedGameObject(null);
            Cursor.lockState = previousLock;
            Cursor.visible = previousCursorVisible;
            cursorCaptured = false;
        }
        private void RefreshAction()
        {
            bool networked = NetworkClient.active || NetworkServer.active;
            Scene scene = gameObject.scene;
            bool canRestart = !alive && !networked && scene.IsValid() && scene.buildIndex >= 0;
            if (restartButton != null) restartButton.gameObject.SetActive(!networked);
            if (restartButton != null) restartButton.interactable = canRestart;
            if (statusLabel != null) statusLabel.text = networked ? "等待服务器安排重生"
                : canRestart ? "重新开始将重置当前场景与本局状态" : "当前场景未加入构建设置，无法重新开始";
        }
        private void Restart()
        {
            if (reloading || alive || !IsOpen || NetworkClient.active || NetworkServer.active) return;
            Scene scene = gameObject.scene;
            if (!scene.IsValid() || scene.buildIndex < 0) return;
            reloading = true;
            if (restartButton != null) restartButton.interactable = false;
            if (statusLabel != null) statusLabel.text = "正在重新开始…";
            try
            {
                SceneManager.LoadSceneAsync(scene.buildIndex, LoadSceneMode.Single);
            }
            catch (System.Exception exception)
            {
                reloading = false;
                RefreshAction();
                if (statusLabel != null) statusLabel.text = "重新开始失败，请重试";
                Debug.LogException(exception, this);
            }
        }
    }
}
