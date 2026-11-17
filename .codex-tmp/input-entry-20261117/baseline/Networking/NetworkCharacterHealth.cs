using Mirror;
using UnityEngine;
using AttributeComponent = RPG4.CharacterAttributes.CharacterAttributes;

namespace RPG4.Networking
{
    /// <summary>无 owner 的场景角色/NPC 生命同步；离线恢复原有写权限，联网只允许服务器写入。</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-350)]
    [RequireComponent(typeof(NetworkIdentity), typeof(AttributeComponent))]
    public sealed class NetworkCharacterHealth : NetworkBehaviour
    {
        [SyncVar(hook = nameof(OnHealthChanged))] private NetworkCharacterHealthSnapshot snapshot;
        private AttributeComponent attributes;
        private bool serverStarted, clientStarted, subscribed, offlineAuthority;

        private void Awake()
        {
            attributes = GetComponent<AttributeComponent>();
            offlineAuthority = attributes.CanExecuteAuthority;
            syncDirection = SyncDirection.ServerToClient;
            syncMode = SyncMode.Observers;
            RefreshAuthority();
        }

        private void OnEnable()
        {
            if (attributes == null) return;
            RefreshAuthority();
            if (serverStarted) { Subscribe(); PublishHealth(); }
            else if (clientStarted) ApplySnapshot();
        }

        // 网络启动与场景对象 spawn 之间也关闭写入；早于普通伤害/靶子脚本运行。
        private void FixedUpdate() => RefreshAuthority();
        private void Update() => RefreshAuthority();

        public override void OnStartServer()
        {
            base.OnStartServer();
            serverStarted = true;
            RefreshAuthority();
            Subscribe();
            PublishHealth();
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            clientStarted = true;
            RefreshAuthority();
            if (!serverStarted) ApplySnapshot();
        }

        public override void OnStopServer()
        {
            serverStarted = false;
            Unsubscribe();
            RefreshAuthority();
            base.OnStopServer();
        }

        public override void OnStopClient()
        {
            clientStarted = false;
            RefreshAuthority();
            base.OnStopClient();
        }

        private void RefreshAuthority()
        {
            if (attributes == null) return;
            bool networking = NetworkServer.active || NetworkClient.active || serverStarted || clientStarted;
            attributes.ConfigureAuthority(isActiveAndEnabled && serverStarted || !networking && offlineAuthority);
        }

        private void Subscribe()
        {
            if (subscribed || attributes == null || !isActiveAndEnabled) return;
            attributes.Changed += PublishHealth;
            subscribed = true;
        }

        private void Unsubscribe()
        {
            if (subscribed && attributes != null) attributes.Changed -= PublishHealth;
            subscribed = false;
        }

        private void PublishHealth()
        {
            if (!serverStarted || !isActiveAndEnabled || attributes == null) return;
            snapshot = new NetworkCharacterHealthSnapshot
            { MaxHealth = attributes.MaxHealth, CurrentHealth = attributes.CurrentHealth };
        }

        private void OnHealthChanged(NetworkCharacterHealthSnapshot previous, NetworkCharacterHealthSnapshot current)
        {
            if (clientStarted && !serverStarted) ApplySnapshot();
        }

        private void ApplySnapshot()
        {
            // Changed/Died/Revived 保持本地持续表现；不伪造客户端 DamageInfo 或重放伤害业务。
            if (attributes != null) attributes.ApplyReplicatedHealth(snapshot.MaxHealth, snapshot.CurrentHealth);
        }

        private void OnDisable()
        {
            Unsubscribe();
            RefreshAuthority();
        }

        private void OnDestroy()
        {
            Unsubscribe();
            if (attributes != null)
                attributes.ConfigureAuthority(!NetworkServer.active && !NetworkClient.active && offlineAuthority);
        }
    }

    public struct NetworkCharacterHealthSnapshot
    {
        public float MaxHealth, CurrentHealth;
    }
}
