using UnityEngine;
namespace RPG4.Frontend
{
    /// <summary>The application subtree is authored in ClientBootstrap, never assembled at runtime.</summary>
    [DisallowMultipleComponent]
    public sealed class ClientBootstrap : MonoBehaviour
    {
        [SerializeField] private ClientSessionController sessionController;
        private void Start()
        {
            if (sessionController == null) Debug.LogError("ClientBootstrap 缺少会话控制器配置。", this);
        }
    }
}
