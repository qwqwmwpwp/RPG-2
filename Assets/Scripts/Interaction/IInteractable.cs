namespace RPG4.Interaction
{
    /// <summary>场景物体提供给玩家准星射线的交互入口。</summary>
    public interface IInteractable
    {
        bool CanInteract(PlayerInteraction player);
        void Interact(PlayerInteraction player);
    }

    /// <summary>可选的准星提示；不改变普通交互物体的行为。</summary>
    public interface IInteractionPrompt
    {
        string InteractionPrompt { get; }
    }
}
