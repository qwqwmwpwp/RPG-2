using TMPro;
using UnityEngine;
using UnityEngine.UI;
namespace RPG4.Frontend
{
    public sealed class LobbyView : FrontendWindow
    {
        public Button startButton, stashButton, settingsButton, quitButton;
        public TMP_Text statusLabel, resultLabel;
        [SerializeField] private CanvasGroup inputGroup;
        public void SetInputEnabled(bool value)
        { inputGroup.interactable = value; inputGroup.blocksRaycasts = value; }
        protected override void Update()
        { if (inputGroup.interactable) base.Update(); }
    }
}
