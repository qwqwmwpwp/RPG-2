using System.Collections.Generic;
using UnityEngine;

namespace RPG4.LoadoutTemplate
{
    /// <summary>按来源顺序复用容器视图，单容器和多容器共用作者配置的自动布局。</summary>
    [DisallowMultipleComponent]
    public sealed class LootContainerListUI : MonoBehaviour
    {
        [SerializeField] private RectTransform content;
        [Tooltip("Content 下现有的首个容器视图；关闭或没有可显示分区时隐藏。")]
        [SerializeField] private ContainerUI firstView;
        [Tooltip("额外分区使用的干净容器预制体资产，不能引用已经绑定数据的场景视图。")]
        [SerializeField] private ContainerUI containerPrefab;
        private readonly List<ContainerUI> pool = new List<ContainerUI>();

        private void Awake() => Clear();

        public bool ValidateConfiguration(out string error)
        {
            error = null;
            if (content == null || firstView == null || firstView.transform.parent != content)
            { error = "战利品容器列表须接线 Content 和其直接子级的首个容器视图。"; return false; }
            if (containerPrefab == null || containerPrefab == firstView || containerPrefab.gameObject.scene.IsValid()
                || containerPrefab.transform.parent != null)
            { error = "战利品容器列表须接线根节点挂载 ContainerUI 的容器预制体资产。"; return false; }
            if (!firstView.ValidateNetworkConfiguration(out error) || !containerPrefab.ValidateNetworkConfiguration(out error)) return false;
            return true;
        }

        internal bool ValidateSections(IReadOnlyList<LootSectionSnapshot> sections, out string error)
        {
            error = null;
            int visibleCount = 0;
            foreach (LootSectionSnapshot section in sections)
            {
                if (section == null || section.Grid == null || !section.Grid.IsReady) continue;
                ContainerUI view = visibleCount++ == 0 ? firstView : containerPrefab;
                if (section.Body != null && section.Body.Items.Count != 0 && view.Slot == null)
                { error = "战利品 UI 未接线容器本体拖拽槽：" + section.Kind; return false; }
            }
            return true;
        }

        internal void Bind(IReadOnlyList<LootSectionSnapshot> sections, LoadoutWindowSession session)
        {
            if (session == null || !session.IsActive || sections == null) { Clear(); return; }
            EnsureFirstView();
            int visibleCount = 0;
            foreach (LootSectionSnapshot section in sections)
            {
                if (section == null || section.Grid == null || !section.Grid.IsReady) continue;
                ContainerUI view = GetView(visibleCount++);
                // 只调整数据视图顺序，尺寸和位置仍交给 Content 的既有自动布局。
                view.transform.SetSiblingIndex(visibleCount - 1);
                view.gameObject.SetActive(true);
                view.BindConnection(session, section.Grid.EventSource, section.Body?.EventSource, section.Grid);
            }
            for (int i = visibleCount; i < pool.Count; i++) Hide(pool[i]);
        }

        public void Clear()
        {
            EnsureFirstView();
            foreach (ContainerUI view in pool) Hide(view);
        }

        private void EnsureFirstView()
        {
            if (pool.Count == 0 && firstView != null) pool.Add(firstView);
        }

        private ContainerUI GetView(int index)
        {
            if (index == pool.Count) pool.Add(Instantiate(containerPrefab, content, false));
            else if (pool[index] == null) pool[index] = Instantiate(containerPrefab, content, false);
            return pool[index];
        }

        private static void Hide(ContainerUI view)
        {
            if (view == null) return;
            view.Disconnect();
            view.gameObject.SetActive(false);
        }

        private void OnDisable() => Clear();
    }
}
