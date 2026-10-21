namespace RPG4.Interaction
{
    /// <summary>场景物体提供给玩家准星射线的交互入口。</summary>
    public interface IInteractable
    {
        bool CanInteract(PlayerInteraction player);
        void Interact(PlayerInteraction player);
    }
}
