using Mirror;
using RPG4.PlayerMovement;
using RPG4.LoadoutTemplate;
using RPG4.Input;
using RPG4.UI;
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
        private Player bound;
        private void Update()
        {
#if !UNITY_SERVER
            Player local = GetComponentInParent<Player>();
            NetworkIdentity identity = local != null ? local.GetComponent<NetworkIdentity>() : null;
            bool networkOwned = identity != null && NetworkClient.active && NetworkClient.localPlayer == identity;
            bool offlineOwned = local != null && !NetworkClient.active && !NetworkServer.active
                && local.Input != null && local.Input.UsesLocalInput;
            NetworkRoleLoadout networkLoadout = local != null ? local.GetComponent<NetworkRoleLoadout>() : null;
            bool owned = local != null && local.isActiveAndEnabled && local.Equipment?.Role != null
                && (networkOwned && networkLoadout != null && networkLoadout.IsReady || offlineOwned)
                && (!NetworkServer.active || NetworkClient.active);
            if (!owned) { Release(); return; }
            if (instance == null && hudPrefab != null)
            {
                // Keep the runtime HUD in the scene's gameplay canvas so it shares the same
                // safe-area coordinate space and sorting context as WeaponAmmoHUD.
                Transform parent = FindGameplayCanvas();
                instance = parent != null ? Instantiate(hudPrefab, parent, false) : Instantiate(hudPrefab);
                hud = instance.GetComponentInChildren<ConsumableHUD>(true);
            }
            if (hud != null && !ReferenceEquals(bound, local)) { bound = local; hud.Bind(local); }
            // 输入仅请求角色动作，预占、治疗和扣除由控制器/服务器处理。
            if (local.Input != null && local.Input.CanReadGameplayInput && local.Input.LocalGameplayInputAllowed
                && Application.isFocused && Time.timeScale > 0f && InputManager.Instance != null
                && (UIManager.Instance == null || !UIManager.Instance.BlocksGameplayInput)
                && InputManager.Instance.backpackInput.useConsumable.WasPressedThisFrame())
            {
                var item = local.Consumables?.ResolveQuickUse(out _);
                if (item != null) local.Consumables.RequestUse(item.ItemId);
            }
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

        private static Transform FindGameplayCanvas()
        {
            Canvas[] canvases = FindObjectsOfType<Canvas>(true);
            for (int i = 0; i < canvases.Length; i++)
            {
                Canvas canvas = canvases[i];
                if (canvas != null && canvas.isRootCanvas && canvas.name == "UICanvas")
                    return canvas.transform;
            }
            return null;
        }
    }
}
