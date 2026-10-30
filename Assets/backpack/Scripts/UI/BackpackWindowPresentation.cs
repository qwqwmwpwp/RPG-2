using TMPro;
using UnityEngine;

namespace RPG4.Backpack.UI
{
    /// <summary>把容器摘要与交互器的只读状态映射到固定窗口；不提交库存操作。</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(200)]
    public sealed class BackpackWindowPresentation : MonoBehaviour
    {
        [SerializeField] private BackpackWindowUIData ui;
        private readonly SummaryCache primary = new SummaryCache();
        private readonly SummaryCache secondary = new SummaryCache();

        private sealed class SummaryCache
        {
            private bool initialized;
            private bool ready;
            private int occupied;
            private int capacity;
            private int stacks;
            private int overflow;
            private long count;

            public void Refresh(BackpackContainerUI view, TMP_Text label)
            {
                if (label == null) return;
                bool nextReady = view != null && view.IsReady;
                int nextOccupied = nextReady ? view.OccupiedCellCount : 0;
                int nextCapacity = nextReady ? view.Columns * view.Rows : 0;
                int nextStacks = nextReady ? view.ItemStackCount : 0;
                int nextOverflow = nextReady ? view.OverflowItemCount : 0;
                long nextCount = nextReady ? view.TotalItemCount : 0;
                if (initialized && ready == nextReady && occupied == nextOccupied && capacity == nextCapacity
                    && stacks == nextStacks && count == nextCount && overflow == nextOverflow) return;
                initialized = true;
                ready = nextReady;
                occupied = nextOccupied;
                capacity = nextCapacity;
                stacks = nextStacks;
                count = nextCount;
                overflow = nextOverflow;
                label.text = !ready ? "等待库存就绪…" : $"占用 {occupied} / {capacity} 格 · {stacks} 堆 / {count} 件"
                    + (overflow > 0 ? $"\n未显示 {overflow} 堆" : string.Empty);
            }

            public void Clear() => initialized = false;
        }

        private void LateUpdate()
        {
            if (ui == null) return;
            if (ui.window == null || !ui.window.IsOpen)
            {
                Clear();
                return;
            }
            primary.Refresh(ui.primaryView, ui.primarySummary);
            secondary.Refresh(ui.secondaryView, ui.secondarySummary);
            BackpackInteractionController interaction = ui.interaction;
            bool active = interaction != null && interaction.IsSessionActive;
            SetText(ui.actionHint, active ? interaction.ContextHint + (!interaction.IsCarrying && !interaction.IsChoosingSplitQuantity ? " · F 检查枪械 / 弹匣" : string.Empty) : "等待库存就绪…");
            SetText(ui.operationStatus, active ? interaction.OperationFeedback : string.Empty);
            if (ui.itemDetails != null)
            {
                ui.itemDetails.Show(active && !interaction.IsCarrying && !interaction.IsChoosingSplitQuantity
                    ? interaction.DisplayedItem ?? interaction.SelectedItem : null);
                if (active && interaction.HasPointer) ui.itemDetails.SetPointer(interaction.PointerPosition);
            }
        }

        private void OnDisable() => Clear();

        private void Clear()
        {
            if (ui == null) return;
            primary.Clear();
            secondary.Clear();
            SetText(ui.primarySummary, string.Empty);
            SetText(ui.secondarySummary, string.Empty);
            SetText(ui.actionHint, string.Empty);
            SetText(ui.operationStatus, string.Empty);
            if (ui.itemDetails != null) ui.itemDetails.Show(null);
        }

        private static void SetText(TMP_Text label, string value)
        {
            if (label != null && label.text != value) label.text = value;
        }
    }
}
