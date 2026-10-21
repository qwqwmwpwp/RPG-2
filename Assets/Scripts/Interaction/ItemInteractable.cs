using UnityEngine;
using UnityEngine.Events;

namespace RPG4.Interaction
{
    /// <summary>最小物品交互：记录调用并触发事件，后续可在事件中接入拾取或其他物品行为。</summary>
    [DisallowMultipleComponent]
    public sealed class ItemInteractable : MonoBehaviour, IInteractable
    {
        [SerializeField] private bool interactable = true;
        [SerializeField] private UnityEvent onInteracted = new UnityEvent();

        public bool CanInteract(PlayerInteraction player) =>
            isActiveAndEnabled && interactable && player != null;

        public void Interact(PlayerInteraction player)
        {
            if (!CanInteract(player)) return;
            Debug.Log($"{player.name} 与物品 {name} 交互。", this);
            onInteracted.Invoke();
        }
    }
}
