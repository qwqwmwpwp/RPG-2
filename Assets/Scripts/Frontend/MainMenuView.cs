using TMPro;
using UnityEngine;
using UnityEngine.UI;
namespace RPG4.Frontend
{
    public sealed class MainMenuView : FrontendWindow
    {
        public Button startButton, serversButton, settingsButton, quitButton;
        public TMP_Text serverCard, statusLabel;
        [SerializeField] private CanvasGroup inputGroup;
        public void SetInputEnabled(bool value)
        { if (inputGroup != null) { inputGroup.interactable = value; inputGroup.blocksRaycasts = value; } }
        protected override void Update()
        { if (inputGroup != null && inputGroup.interactable) base.Update(); }
    }
}
