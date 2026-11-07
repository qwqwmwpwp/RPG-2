using System.Collections.Generic;
using RPG4.Backpack.Inventory;
using RPG4.Backpack.Items;
using RPG4.PlayerMovement;
using TMPro;
using UnityEngine;

namespace RPG4.Frontend
{
    /// <summary>只投影已确认装备；复用局内角色外观和装备模型装配。</summary>
    public sealed class LobbyCharacterPreview : MonoBehaviour
    {
        [SerializeField] private CharacterPresentation presentation;
        [SerializeField] private TMP_Text equippedLabel;
        [SerializeField] private ItemManifestCatalog catalog;
        private readonly List<RuntimeItem> items = new List<RuntimeItem>();
        public void BindEquipment(IBackpackView source)
        {
            items.Clear();
            if (source != null && source.IsReady) source.CopyItems(items);
            RuntimeItem confirmed = items.Count > 0 ? items[0] : null;
            if (presentation != null) presentation.BindPreviewEquipment(confirmed);
            if (equippedLabel != null) equippedLabel.text = confirmed == null ? "空装出战 · 可在仓库选择武器"
                : presentation != null && presentation.HasEquipmentModel ? "当前武器 · " + ResolveName(confirmed)
                : "已装备武器 · 暂无展示外观";
        }
        private string ResolveName(RuntimeItem item)
        {
            if (catalog != null && catalog.TryGet(item.ItemType, out ItemManifest manifest) && manifest != null)
                return manifest.name;
            return "已装备";
        }
        private void OnDisable()
        {
            if (presentation != null) presentation.BindPreviewEquipment(null);
        }
    }
}
