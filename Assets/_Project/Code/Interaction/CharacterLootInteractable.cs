using RPG4.LoadoutTemplate;
using UnityEngine;

namespace RPG4.Interaction
{
    /// <summary>角色战利品及固定箱子的本机交互入口；读取及移动物品由角色网络库存向服务器请求。</summary>
    [DisallowMultipleComponent, RequireComponent(typeof(NetworkLootSource))]
    public sealed class CharacterLootInteractable : MonoBehaviour, IInteractable, IInteractionPrompt
    {
        [Tooltip("留空时沿用角色战利品提示；固定箱子可配置自己的交互文字。")]
        [SerializeField] private string interactionPrompt = "E 搜索角色战利品";
        [SerializeField] private string emptyInteractionPrompt = "已搜空 · E 查看";
        private NetworkLootSource source;

        public string InteractionPrompt => source != null && source.IsEmpty
            ? (string.IsNullOrWhiteSpace(emptyInteractionPrompt) ? "已搜空 · E 查看" : emptyInteractionPrompt)
            : (string.IsNullOrWhiteSpace(interactionPrompt) ? "E 搜索角色战利品" : interactionPrompt);

        private void Awake() => source = GetComponent<NetworkLootSource>();

        public bool CanInteract(PlayerInteraction player) => player != null && player.CanUseInteraction
            && isActiveAndEnabled && source != null && source.isActiveAndEnabled && source.isClient
            && source.netId != 0 && source.IsAvailable;

        public void Interact(PlayerInteraction player)
        {
            // 仅接受本机准星当前选中的箱子，不能借由另一角色或直接调用绕过设备许可。
            if (!CanInteract(player) || !ReferenceEquals(player.CurrentTarget, this)) return;
            if (!player.LocalLoadout.OpenLoot(source))
                player.ShowInteractionFeedback("战利品界面尚未就绪或来源不可用。", false);
        }
    }
}
