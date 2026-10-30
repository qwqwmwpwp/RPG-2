using UnityEngine;

namespace RPG4.UI
{
    /// <summary>窗口统一入口：状态与显隐由管理器驱动，派生类只处理打开条件和业务生命周期。</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(RectTransform), typeof(CanvasGroup))]
    public abstract class UIBase : MonoBehaviour
    {
        [Header("UI 管理")]
        [SerializeField] private CanvasGroup canvasGroup;
        [Tooltip("同组窗口互斥；留空时可与其他窗口同时打开。")]
        [SerializeField] private string exclusiveGroup;

        public string ExclusiveGroup => exclusiveGroup;
        public bool IsOpen { get; private set; }
        public UIManager Manager { get; internal set; }

        protected virtual void Awake() => SetVisible(IsOpen);

        protected virtual void OnEnable()
        {
            if (UIManager.Instance != null) UIManager.Instance.Register(this);
        }

        protected virtual void OnDisable() => Close();

        protected virtual void OnDestroy()
        {
            Close();
            if (Manager != null) Manager.Unregister(this);
        }

        public bool Open()
        {
            UIManager host = Manager != null ? Manager : UIManager.Instance;
            if (host != null) return host.Open(this);
            Debug.LogWarning($"{name} 无法打开：场景中未配置 UIManager。", this);
            return false;
        }

        public void Close()
        {
            if (Manager != null) Manager.Close(this);
            else ApplyClose();
        }

        public void Toggle()
        {
            if (IsOpen) Close();
            else Open();
        }

        protected virtual bool CanOpen() => true;
        protected virtual void OnOpened() { }
        protected virtual void OnClosed() { }

        internal bool PrepareOpen()
        {
            if (canvasGroup == null) canvasGroup = GetComponent<CanvasGroup>();
            return canvasGroup != null && CanOpen();
        }

        internal void ApplyOpen()
        {
            if (IsOpen) return;
            IsOpen = true;
            SetVisible(true);
            OnOpened();
        }

        internal void ApplyClose()
        {
            if (!IsOpen) return;
            IsOpen = false;
            SetVisible(false);
            OnClosed();
        }

        private void SetVisible(bool visible)
        {
            if (canvasGroup == null) canvasGroup = GetComponent<CanvasGroup>();
            if (canvasGroup == null) return;
            // 保持脚本启用，关闭后仍可响应输入和打开请求。
            canvasGroup.alpha = visible ? 1f : 0f;
            canvasGroup.interactable = visible;
            canvasGroup.blocksRaycasts = visible;
        }
    }
}
