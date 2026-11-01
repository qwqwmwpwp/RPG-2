using Mirror;
using UnityEngine;

namespace RPG4.Networking
{
    /// <summary>Keep authored camera/audio/UI roots inactive on dedicated servers; world physics stays outside.</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-1100)]
    public sealed class ClientPresentationRoot : MonoBehaviour
    {
        [SerializeField] private GameObject[] clientOnlyRoots = System.Array.Empty<GameObject>();
        private void OnEnable() => RefreshPresentation();
        private void Update() => RefreshPresentation();
        private void OnDisable() => SetPresentation(false);
        private void RefreshPresentation()
        {
#if UNITY_SERVER
            SetPresentation(false);
#else
            SetPresentation(NetworkClient.isConnected);
#endif
        }
        private void SetPresentation(bool visible)
        {
            if (clientOnlyRoots == null) return;
            foreach (GameObject root in clientOnlyRoots)
                if (root != null && root != gameObject && root.scene == gameObject.scene && root.activeSelf != visible)
                    root.SetActive(visible);
        }
    }
}
