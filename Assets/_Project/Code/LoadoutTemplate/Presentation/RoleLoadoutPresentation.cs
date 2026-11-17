using System;
using System.Collections.Generic;
using RPG4.Backpack.Core;
using UnityEngine;

namespace RPG4.LoadoutTemplate
{
    /// <summary>管理拥有者 UI 绑定、窗口和拖拽的完整生命周期，不持有权威库存端点。</summary>
    [DisallowMultipleComponent]
    public sealed class RoleLoadoutPresentation : MonoBehaviour, IRoleLoadoutPresentation
    {
        [SerializeField] private LoadoutWindowUI window;
        [SerializeField] private LoadoutTemplateUI bindings;
        [SerializeField] private LoadoutDragController drag;
        [SerializeField] private GameObject presentationRoot;
        private EventSubscription windowSubscription;
        private bool sessionActive;

        public bool IsWindowOpen => sessionActive && window != null && window.IsOpen;
        public bool IsLootOpen => sessionActive && window != null && window.IsLootOpen;
        public event Action WindowChanged;
        public event Action LootClosed;

        public void Initialize(Transform actor, Transform authority)
        {
            if (presentationRoot != null && (actor.IsChildOf(presentationRoot.transform)
                || authority != null && authority.IsChildOf(presentationRoot.transform)
                || transform.IsChildOf(presentationRoot.transform)))
            {
                Debug.LogError("网络背包展示根必须独立于角色根、权威控制器和展示生命周期组件。", this);
                presentationRoot = null;
            }
            EndSession();
        }

        public bool BeginSession(string ownerKey, out string error)
        {
            EndSession();
            error = null;
            if (window == null || bindings == null || drag == null || presentationRoot == null)
            {
                error = "角色网络背包缺少展示引用。";
                return false;
            }
            if (!bindings.ValidateNetworkConfiguration(out error)
                || !window.ValidateNetworkConfiguration(bindings, out error)) return false;
            if (!drag.HasConfiguredLayer)
            {
                error = "交换器没有拖拽层。";
                return false;
            }
            bindings.SetNetworkOwner(ownerKey);
            drag.SetNetworkOwner(ownerKey);
            sessionActive = true;
            windowSubscription = EventBus.Global.Subscribe<bool>(LoadoutChannels.WindowChanged,
                bindings.WindowSource, this, open => WindowChanged?.Invoke());
            if (window.LootBindings != null) window.LootBindings.Closed += OnLootClosed;
            SetVisible(true);
            return true;
        }

        public void EndSession()
        {
            sessionActive = false;
            windowSubscription.Dispose();
            if (window != null && window.LootBindings != null)
                window.LootBindings.Closed -= OnLootClosed;
            CloseLoot();
            SetVisible(false);
        }

        public void SetInputAllowed(bool allowed)
        {
            allowed &= sessionActive;
            if (!allowed) CloseLoot();
            if (!allowed && drag != null) drag.CancelDrag();
            if (window != null) window.SetNetworkInput(allowed);
        }

        public bool OwnsSession(LoadoutWindowSession session) => sessionActive && session != null
            && session.IsActive && bindings != null && ReferenceEquals(bindings.Session, session);

        public bool CanOpenLoot(out string error)
        {
            error = null;
            if (!sessionActive || window == null || window.LootBindings == null)
            {
                error = "战利品窗口尚未接线。";
                return false;
            }
            return window.LootBindings.ValidateConfiguration(out error);
        }

        public bool ShowLoot(IReadOnlyList<LootSectionSnapshot> sections, out string error)
        {
            if (!CanOpenLoot(out error)) return false;
            window.ShowLootWindow();
            LoadoutWindowSession session = bindings.Session;
            if (!OwnsSession(session))
            {
                error = "玩家背包尚未连接。";
                return false;
            }
            if (!window.LootBindings.ValidateSections(sections, out error)) return false;
            window.LootBindings.Bind(sections, session);
            return true;
        }

        public void CloseLoot()
        {
            if (drag != null) drag.CancelDrag();
            if (window != null && window.LootBindings != null) window.LootBindings.Close();
        }

        private void SetVisible(bool active)
        {
            if (window != null)
            {
                window.SetNetworkInput(false);
                window.enabled = active;
            }
            if (drag != null)
            {
                drag.CancelDrag();
                drag.enabled = active;
            }
            if (bindings != null) bindings.enabled = active;
            if (presentationRoot != null) presentationRoot.SetActive(active);
        }

        private void OnLootClosed() => LootClosed?.Invoke();
        private void OnDestroy() => EndSession();
    }
}
