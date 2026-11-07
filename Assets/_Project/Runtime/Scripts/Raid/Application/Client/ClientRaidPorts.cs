using RPG4.Raid.Domain;

namespace RPG4.Raid.Application.Client
{
    public enum ClientRaidScene { Lobby, Gameplay }
    public enum ClientSceneLoadResult { Deferred, Started, Failed }

    public interface IClientRaidClock { double Now { get; } }

    public interface IClientRaidTransport
    {
        bool Connected { get; }
        bool LoadingScene { get; }
        void Send(LobbyCommand command);
        void Disconnect();
    }

    public interface IClientRaidScenes
    {
        bool IsLobbyActive { get; }
        ClientSceneLoadResult Load(ClientRaidScene scene);
        void EnterGameplay();
        void ClearWorld();
    }

    /// <summary>Projects opaque inventory payloads into the engine's inventory views.</summary>
    public interface IClientRaidInventoryProjection
    {
        bool IsReady { get; }
        void Apply(PersistentInventoryState inventory);
        void NotifyChanged();
        void PublishResult(PersonalContainerKind source, LobbyInventoryResult result);
        void Clear();
    }
}
