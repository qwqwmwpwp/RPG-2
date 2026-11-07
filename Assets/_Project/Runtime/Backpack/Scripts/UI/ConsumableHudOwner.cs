using Mirror;
using RPG4.Backpack.Inventory;
using UnityEngine;

namespace RPG4.Backpack.UI
{
    /// <summary>玩家预制体的客户端表现引用；仅当前本机背包拥有者创建一次静态 HUD 预制体。</summary>
    [DisallowMultipleComponent]
    public sealed class ConsumableHudOwner : MonoBehaviour
    {
        [SerializeField] private GameObject hudPrefab;
        private GameObject instance;
        private ConsumableHUD hud;
        private IBackpackView bound;
        private void Update()
        {
#if !UNITY_SERVER
            IBackpackView local = LocalBackpackSourceResolver.Resolve();
            Component owner = local as Component;
            NetworkIdentity identity = GetComponentInParent<NetworkIdentity>();
            bool networkOwned = owner != null && identity != null
                && owner.gameObject == identity.gameObject
                && NetworkClient.active && NetworkClient.localPlayer == identity;
            bool offlineHierarchyOwned = owner != null && !NetworkClient.active && !NetworkServer.active
                && (transform == owner.transform || transform.IsChildOf(owner.transform)
                    || owner.transform.IsChildOf(transform));
            bool owned = local != null && local.IsReady && (networkOwned || offlineHierarchyOwned)
                && (!NetworkServer.active || NetworkClient.active);
            if (!owned) { Release(); return; }
            if (instance == null && hudPrefab != null)
            {
                instance = Instantiate(hudPrefab);
                hud = instance.GetComponentInChildren<ConsumableHUD>(true);
            }
            if (hud != null && !ReferenceEquals(bound, local)) { bound = local; hud.Bind(local); }
#endif
        }
        private void OnDisable() => Release();
        private void OnDestroy() => Release();
        private void Release()
        {
            if (hud != null) hud.Unbind();
            if (instance != null) Destroy(instance);
            instance = null; hud = null; bound = null;
        }
    }
}
