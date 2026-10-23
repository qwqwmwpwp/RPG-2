using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace RPG4.Backpack.UI
{
    /// <summary>数量弹窗的视图引用与交互。不读写库存；确认后把数量交回会话。</summary>
    [DisallowMultipleComponent]
    public sealed class InventorySplitQuantityUI : MonoBehaviour
    {
        [SerializeField] private TMP_Text quantityLabel;
        [SerializeField] private TMP_Text rangeLabel;
        [SerializeField] private Button decreaseButton;
        [SerializeField] private Button increaseButton;
        [SerializeField] private Button halfButton;
        [SerializeField] private Button maximumButton;
        [SerializeField] private Button confirmButton;
        [SerializeField] private Button cancelButton;

        private int maximum;
        private int quantity;
        private Action<int> confirmed;
        public bool IsOpen => gameObject.activeSelf;
        public int LastInteractionFrame { get; private set; } = -1;

        private void Awake()
        {
            decreaseButton.onClick.AddListener(Decrease);
            increaseButton.onClick.AddListener(Increase);
            halfButton.onClick.AddListener(ChooseHalf);
            maximumButton.onClick.AddListener(ChooseMaximum);
            confirmButton.onClick.AddListener(Confirm);
            cancelButton.onClick.AddListener(Close);
        }

        public void Open(int maxQuantity, Action<int> onConfirmed)
        {
            maximum = Mathf.Max(1, maxQuantity);
            quantity = 1;
            confirmed = onConfirmed;
            gameObject.SetActive(true);
            transform.SetAsLastSibling();
            Refresh();
        }

        public void Close()
        {
            confirmed = null;
            LastInteractionFrame = Time.frameCount;
            gameObject.SetActive(false);
        }

        private void Decrease() { quantity = Mathf.Max(1, quantity - 1); Refresh(); }
        private void Increase() { quantity = Mathf.Min(maximum, quantity + 1); Refresh(); }
        private void ChooseHalf() { quantity = Mathf.Max(1, (maximum + 1) / 2); Refresh(); }
        private void ChooseMaximum() { quantity = maximum; Refresh(); }
        private void Confirm()
        {
            Action<int> callback = confirmed;
            int selected = quantity;
            Close();
            callback?.Invoke(selected);
        }

        private void Refresh()
        {
            quantityLabel.text = quantity.ToString();
            rangeLabel.text = $"可拆出 1 – {maximum} 件，原堆至少保留 1 件";
            decreaseButton.interactable = quantity > 1;
            increaseButton.interactable = quantity < maximum;
        }
    }
}
