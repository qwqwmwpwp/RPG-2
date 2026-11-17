using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace RPG4.Networking
{
    [DisallowMultipleComponent, RequireComponent(typeof(BoxCollider))]
    public sealed class ExtractionZone : MonoBehaviour
    {
        [SerializeField, Min(0.1f)] private float extractionSeconds = 5f;
        private readonly Dictionary<NetworkPlayerController, double> entered = new Dictionary<NetworkPlayerController, double>();
        private readonly List<NetworkPlayerController> leaving = new List<NetworkPlayerController>();
        private BoxCollider region;
        private void Awake() { region = GetComponent<BoxCollider>(); region.isTrigger = true; }
        private void FixedUpdate()
        {
            if (!NetworkServer.active || !(NetworkManager.singleton is AuthoritativeNetworkManager manager)) return;
            leaving.Clear();
            foreach (NetworkConnectionToClient connection in NetworkServer.connections.Values)
            {
                NetworkPlayerController controller = connection.identity != null ? connection.identity.GetComponent<NetworkPlayerController>() : null;
                if (controller == null || controller.Player == null || !controller.Player.IsAlive || !controller.isActiveAndEnabled) continue;
                Vector3 position = region.transform.InverseTransformPoint(controller.transform.position) - region.center;
                Vector3 half = region.size * 0.5f;
                if (Mathf.Abs(position.x) > half.x || Mathf.Abs(position.y) > half.y || Mathf.Abs(position.z) > half.z) continue;
                leaving.Add(controller);
                if (!entered.TryGetValue(controller, out double start)) entered.Add(controller, Time.realtimeSinceStartupAsDouble);
                else if (Time.realtimeSinceStartupAsDouble - start >= Mathf.Max(0.1f, extractionSeconds)) manager.TryExtractPlayer(controller);
            }
            var previous = new List<NetworkPlayerController>(entered.Keys);
            foreach (NetworkPlayerController controller in previous) if (controller == null || !leaving.Contains(controller)) entered.Remove(controller);
        }
        private void OnDisable() => entered.Clear();
    }
}
