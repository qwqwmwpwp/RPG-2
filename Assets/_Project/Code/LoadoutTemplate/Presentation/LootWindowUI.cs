using System;
using System.Collections.Generic;
using RPG4.Backpack.Core;
using UnityEngine;

namespace RPG4.LoadoutTemplate
{
    [Serializable]
    public sealed class LootViewBinding
    {
        public LootSectionKind kind;
        public ContainerUI view;
    }
    /// <summary>只接线用户制作的通用网格/容器视图，不生成固定界面层级。</summary>
    [DisallowMultipleComponent]
    public sealed class LootWindowUI : MonoBehaviour
    {
        [SerializeField] private LoadoutWindowUI window;
        [SerializeField] private LoadoutTemplateUI playerBindings;
        [SerializeField] private LootContainerListUI containerList;
        [SerializeField] private LootViewBinding[] views = Array.Empty<LootViewBinding>();
        [SerializeField, Min(.1f)] private float localInteractionDistance = 3f;
        private LootSource localSource;
        private LocalLootSession localSession;
        private LoadoutWindowSession boundSession;
        public event Action Closed;
        public bool IsOpen => boundSession != null && boundSession.IsActive && window != null && window.IsLootOpen;

        public bool Open(LootSource source, RoleLoadoutController role, Transform actor, Transform anchor, out string error)
        {
            error = null; Close();
            if (!LocalLootSession.CanOpen(source, role, actor, anchor, localInteractionDistance, out error)) return false;
            if (window == null || playerBindings == null) { error = "战利品窗口缺少背包窗口/绑定引用。"; return false; }
            if (!ValidateConfiguration(out error)) return false;
            window.ShowLootWindow();
            LoadoutWindowSession session = playerBindings.Session;
            if (session == null || !session.IsActive) { error = "玩家背包尚未连接。"; Close(); return false; }
            if (!LocalLootSession.TryOpen(source, role, actor, anchor, localInteractionDistance, session,
                out localSession, out error)) { Close(); return false; }
            localSource = source;
            source.Changed += OnLocalSourceChanged;
            var snapshots = new List<LootSectionSnapshot>();
            foreach (LootSection section in source.Sections)
                snapshots.Add(new LootSectionSnapshot(section.Kind, section.Grid.Snapshot, section.Body?.Snapshot));
            if (!ValidateSections(snapshots, out error)) { Close(); return false; }
            Bind(snapshots, session); return true;
        }
        public bool ValidateConfiguration(out string error)
        {
            error = null;
            if (window == null || playerBindings == null) { error = "战利品 UI 缺少窗口或玩家绑定引用。"; return false; }
            if (containerList != null) return containerList.ValidateConfiguration(out error);
            if (views == null || views.Length == 0) { error = "战利品 UI 尚未接线用户制作的通用网格分区。"; return false; }
            var unique = new HashSet<LootSectionKind>();
            var uniqueViews = new HashSet<ContainerUI>();
            foreach (LootViewBinding binding in views)
            {
                if (binding == null || binding.view == null || !unique.Add(binding.kind) || !uniqueViews.Add(binding.view))
                { error = "战利品 UI 分区视图为空、类别重复或多个分区共用了同一视图。"; return false; }
                if (!binding.view.ValidateNetworkConfiguration(out error)) return false;
            }
            return true;
        }
        internal bool ValidateSections(IReadOnlyList<LootSectionSnapshot> sections, out string error)
        {
            if (!ValidateConfiguration(out error)) return false;
            if (containerList != null) return containerList.ValidateSections(sections, out error);
            foreach (LootSectionSnapshot section in sections)
            {
                if (!section.Grid.IsReady) continue;
                LootViewBinding found = null;
                foreach (LootViewBinding binding in views) if (binding.kind == section.Kind) { found = binding; break; }
                if (found == null) { error = "战利品 UI 未接线分区：" + section.Kind; return false; }
                if (section.Body != null && section.Body.Items.Count != 0 && found.view.Slot == null)
                { error = "战利品 UI 未接线容器本体拖拽槽：" + section.Kind; return false; }
            }
            return true;
        }
        private void OnLocalSourceChanged()
        {
            if (localSource == null || boundSession == null || !boundSession.IsActive) return;
            var snapshots = new List<LootSectionSnapshot>();
            foreach (LootSection section in localSource.Sections)
                snapshots.Add(new LootSectionSnapshot(section.Kind, section.Grid.Snapshot, section.Body?.Snapshot));
            Bind(snapshots, boundSession);
        }
        internal void Bind(IReadOnlyList<LootSectionSnapshot> sections, LoadoutWindowSession session)
        {
            boundSession = session;
            if (containerList != null) { containerList.Bind(sections, session); return; }
            foreach (LootViewBinding binding in views ?? Array.Empty<LootViewBinding>())
            {
                if (binding?.view == null) continue;
                LootSectionSnapshot found = null;
                foreach (LootSectionSnapshot section in sections) if (section.Kind == binding.kind) { found = section; break; }
                binding.view.gameObject.SetActive(found != null && found.Grid.IsReady);
                if (found != null) binding.view.BindConnection(session, found.Grid.EventSource, found.Body?.EventSource, found.Grid);
                else binding.view.Disconnect();
            }
        }
        public void Close()
        {
            bool wasOpen = boundSession != null;
            boundSession = null;
            localSession?.Dispose();
            localSession = null;
            if (containerList != null) containerList.Clear();
            else foreach (LootViewBinding binding in views ?? Array.Empty<LootViewBinding>()) if (binding?.view != null) binding.view.Disconnect();
            if (localSource != null) localSource.Changed -= OnLocalSourceChanged;
            localSource = null;
            if (window != null) window.CloseLoot();
            if (wasOpen) Closed?.Invoke();
        }
        private void Update()
        {
            if (boundSession != null && (!IsOpen || localSession != null && !localSession.IsAccessible)) Close();
        }
        private void OnDisable() => Close();
    }
}
