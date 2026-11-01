using RPG4.Networking.Contracts;
using UnityEngine;

namespace RPG4.Networking
{
    /// <summary>Shared client/server build content identity. S3 must supply the authoritative generated digest.</summary>
    [CreateAssetMenu(menuName = "RPG4/Entry Content Manifest")]
    public sealed class EntryContentManifest : ScriptableObject
    {
        public const string OnlineScenePath = "Assets/Scenes/OnlineGame.unity";
        public const string MainMenuScenePath = "Assets/Scenes/MainMenu.unity";
        public const string GenerationRule = "rpg-entry-content-v2";
        [SerializeField] private string contentHash = string.Empty;
        [SerializeField] private string generationRule = string.Empty;
        [SerializeField] private uint protocolVersion;
        public string ContentHash => contentHash ?? string.Empty;
        public string Rule => generationRule ?? string.Empty;
        public uint ProtocolVersion => protocolVersion;
        public bool IsValid => generationRule == GenerationRule && protocolVersion == EntryProtocol.Version
            && EntryProtocol.IsLowerHex(contentHash, 64);
    }
}
