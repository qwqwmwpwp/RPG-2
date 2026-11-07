using TMPro;
using UnityEngine.UI;
namespace RPG4.Frontend
{
    public sealed class ServerSelectionView : FrontendWindow
    {
        public TMP_Dropdown servers;
        public TMP_InputField address, port;
        public TMP_Text validationLabel;
        public Button continueButton, closeButton;
    }
}
