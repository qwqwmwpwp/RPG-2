using RPG4.PlayerMovement;
using TMPro;
using UnityEngine;
namespace RPG4.Frontend
{
    /// <summary>Lobby preview for the independent per-raid role loadout flow.</summary>
    public sealed class LobbyCharacterPreview : MonoBehaviour
    {
        [SerializeField] private CharacterPresentation presentation;
        [SerializeField] private TMP_Text equippedLabel;
        public void ShowEmptyLoadout()
        {
            if (presentation != null) presentation.BindPreviewEquipment(null);
            if (equippedLabel != null) equippedLabel.text = "每局独立装备与携行物品";
        }
        private void OnDisable() { if (presentation != null) presentation.BindPreviewEquipment(null); }
    }
}
