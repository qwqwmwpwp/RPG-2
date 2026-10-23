using System;
using System.Collections.Generic;
using UnityEngine;

namespace RPG4.UI
{
    /// <summary>场景 UI 宿主：启动时实例化窗口预制体，统一注册、开关、置顶和互斥管理。</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-900)]
    public sealed class UIManager : MonoBehaviour
    {
        [SerializeField] private RectTransform windowRoot;
        [Tooltip("游戏开始时在窗口根节点下各生成一次，并注册到管理器。")]
        [SerializeField] private UIBase[] windowPrefabs = Array.Empty<UIBase>();
        private readonly List<UIBase> registeredWindows = new List<UIBase>();
        private int lastWindowChangeFrame = -1;

        public static UIManager Instance { get; private set; }

        /// <summary>场景交互据此暂停，避免窗口操作同时触发世界物品。</summary>
        public bool HasOpenWindows
        {
            get
            {
                foreach (UIBase window in registeredWindows)
                    if (window != null && window.IsOpen) return true;
                return false;
            }
        }

        /// <summary>窗口开关当帧也占用游戏输入，避免关闭窗口的按键或点击继续传给角色和镜头。</summary>
        public bool BlocksGameplayInput => HasOpenWindows || lastWindowChangeFrame == Time.frameCount;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogError("同一时间只能启用一个 UIManager。", this);
                enabled = false;
                return;
            }
            Instance = this;
            if (windowRoot == null)
            {
                Debug.LogError("UIManager 未配置窗口根节点。", this);
                return;
            }

            foreach (UIBase prefab in windowPrefabs)
            {
                if (prefab == null) continue;
                UIBase window = Instantiate(prefab, windowRoot, false);
                window.name = prefab.name;
                Register(window);
            }
        }

        private void OnDisable()
        {
            if (Instance == this) CloseAll();
        }

        private void OnDestroy()
        {
            if (Instance != this) return;
            foreach (UIBase window in registeredWindows.ToArray()) Unregister(window);
            Instance = null;
        }

        public bool Register(UIBase window)
        {
            if (window == null || (window.Manager != null && window.Manager != this)) return false;
            if (registeredWindows.Contains(window)) return true;
            registeredWindows.Add(window);
            window.Manager = this;
            return true;
        }

        public void Unregister(UIBase window)
        {
            if (window == null || window.Manager != this) return;
            Close(window);
            registeredWindows.Remove(window);
            window.Manager = null;
        }

        public T Get<T>() where T : UIBase
        {
            foreach (UIBase window in registeredWindows)
                if (window is T view && view != null) return view;
            return null;
        }

        public bool Open<T>() where T : UIBase => Open(Get<T>());
        public void Close<T>() where T : UIBase => Close(Get<T>());

        public void Toggle<T>() where T : UIBase
        {
            T window = Get<T>();
            if (window != null) window.Toggle();
        }

        public bool Open(UIBase window)
        {
            if (!isActiveAndEnabled || !Register(window)) return false;
            if (window.IsOpen) return true;
            Transform parent = window.transform.parent;
            if ((parent != null && !parent.gameObject.activeInHierarchy) || !window.PrepareOpen()) return false;

            // 先还原旧窗口的输入快照，新窗口再捕获状态。
            if (!string.IsNullOrEmpty(window.ExclusiveGroup))
            {
                foreach (UIBase other in registeredWindows.ToArray())
                    if (other != null && other != window && other.ExclusiveGroup == window.ExclusiveGroup)
                        Close(other);
            }
            window.gameObject.SetActive(true);
            window.enabled = true;
            window.transform.SetAsLastSibling();
            lastWindowChangeFrame = Time.frameCount;
            window.ApplyOpen();
            return true;
        }

        public void Close(UIBase window)
        {
            if (window == null || window.Manager != this || !window.IsOpen) return;
            lastWindowChangeFrame = Time.frameCount;
            window.ApplyClose();
        }

        public void CloseAll()
        {
            foreach (UIBase window in registeredWindows.ToArray()) Close(window);
        }
    }
}
