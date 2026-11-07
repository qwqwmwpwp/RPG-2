using Mirror;
using UnityEngine;
using RPG4.Backpack.Core;

namespace RPG4.Backpack.Network
{
    /// <summary>最小联网玩家对象：只承载网络身份与归属关系（库存同步由同预制体上的 <see cref="BackpackNetworkInventory"/> 负责）；刻意不含移动、相机、输入与拾取，避免与离线玩家的权威与输入逻辑冲突。</summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("RPG4/Backpack Network Player")]
    public class BackpackNetworkPlayer : NetworkBehaviour
    {
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        [Header("归属可视化：本地玩家 / 远端玩家配色")]
        [SerializeField]
        private Renderer bodyRenderer;

        [SerializeField]
        private Color localColor = new Color(0.22f, 0.78f, 0.35f, 1f);

        [SerializeField]
        private Color remoteColor = new Color(0.82f, 0.35f, 0.30f, 1f);

        [SyncVar]
        private int ownerConnectionId = -1;

        private MaterialPropertyBlock propertyBlock;

        public int OwnerConnectionId => ownerConnectionId; // 服务端为该玩家分配的连接 Id；-1 表示尚未收到同步值。

        /// <inheritdoc/>
        public override void OnStartServer()
        {
            base.OnStartServer();
            ownerConnectionId = connectionToClient != null ? connectionToClient.connectionId : -1;
            BackpackLog.Info(LogModule.Network, "玩家已生成（服务端）：netId=" + netId + "，connectionId=" + ownerConnectionId + "。");
        }

        /// <inheritdoc/>
        public override void OnStartClient()
        {
            base.OnStartClient();
            BackpackLog.Info(LogModule.Network, "玩家已出现（客户端）：netId=" + netId + "，connectionId=" + ownerConnectionId + "，isLocalPlayer=" + isLocalPlayer + "，isOwned=" + isOwned + "。");
            ApplyOwnershipColor();
        }

        /// <inheritdoc/>
        public override void OnStartLocalPlayer()
        {
            base.OnStartLocalPlayer();
            BackpackLog.Info(LogModule.Network, "本地玩家已就绪：netId=" + netId + "。");
            ApplyOwnershipColor();
        }

        /// <inheritdoc/>
        public override void OnStopServer()
        {
            BackpackLog.Info(LogModule.Network, "玩家已移除（服务端）：netId=" + netId + "。");
            base.OnStopServer();
        }

        /// <inheritdoc/>
        public override void OnStopClient()
        {
            BackpackLog.Info(LogModule.Network, "玩家已移除（客户端）：netId=" + netId + "。");
            base.OnStopClient();
        }

        /// <summary>按归属（本地 / 远端）给身体渲染器配色，便于区分自己的角色。</summary>
        private void ApplyOwnershipColor()
        {
            if (bodyRenderer == null)
            {
                return;
            }

            if (propertyBlock == null)
            {
                propertyBlock = new MaterialPropertyBlock();
            }

            Color color = isOwned ? localColor : remoteColor;
            bodyRenderer.GetPropertyBlock(propertyBlock);
            propertyBlock.SetColor(BaseColorId, color);
            propertyBlock.SetColor(ColorId, color);
            bodyRenderer.SetPropertyBlock(propertyBlock);
        }
    }
}
