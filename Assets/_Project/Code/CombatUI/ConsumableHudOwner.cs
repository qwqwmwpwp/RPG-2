using RPG4.PlayerMovement;
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
        private Player player;

        private void OnEnable()
        {
#if !UNITY_SERVER
            player = GetComponentInParent<Player>();
            if (player == null) return;
            player.LocalLoadout.AvailabilityChanged += OnAvailabilityChanged;
            OnAvailabilityChanged(player.LocalLoadout.Available);
#endif
        }

        private void OnAvailabilityChanged(bool available)
        {
            if (!available) { Release(); return; }
            if (instance == null && hudPrefab != null)
            {
                // Keep the runtime HUD in the scene's gameplay canvas so it shares the same
                // safe-area coordinate space and sorting context as WeaponAmmoHUD.
                Transform parent = FindGameplayCanvas();
                instance = parent != null ? Instantiate(hudPrefab, parent, false) : Instantiate(hudPrefab);
                hud = instance.GetComponentInChildren<ConsumableHUD>(true);
            }
            if (hud != null) hud.Bind(player);
        }
        private void OnDisable()
        {
            if (!ReferenceEquals(player, null)) player.LocalLoadout.AvailabilityChanged -= OnAvailabilityChanged;
            player = null;
            Release();
        }
        private void Release()
        {
            if (hud != null) hud.Unbind();
            if (instance != null) Destroy(instance);
            instance = null; hud = null;
        }

        private Transform FindGameplayCanvas()
        {
            Canvas[] canvases = FindObjectsOfType<Canvas>(true);
            for (int i = 0; i < canvases.Length; i++)
            {
                Canvas canvas = canvases[i];
                if (canvas != null && canvas.gameObject.scene == gameObject.scene && canvas.isRootCanvas && canvas.name == "UICanvas")
                    return canvas.transform;
            }
            return null;
        }
    }
}
