using System;
using RPG4.Raid.Domain;

namespace RPG4.Raid.Application.Client
{
    /// <summary>Client raid and lobby state, independent of transport, scenes and item runtime types.</summary>
    public sealed class ClientRaidFlow
    {
        private readonly IClientRaidClock clock;
        private readonly IClientRaidTransport transport;
        private readonly IClientRaidScenes scenes;
        private LobbyCommand pending;
        private double requestAt;
        private bool entryReadySent, lobbyAdmissionReady;

        public event Action Changed;
        public RaidPhase Phase { get; private set; } = RaidPhase.Lobby;
        public string LastResultText { get; private set; } = string.Empty;
        public string RaidId { get; private set; }
        public string WorldRunId { get; private set; }
        public bool LobbyVisible { get; private set; }
        public bool IsLobbyReady => lobbyAdmissionReady;
        public bool RequestPending => pending != null;
        public bool CanStartRaid => LobbyVisible && pending == null && lobbyAdmissionReady && Phase == RaidPhase.Lobby;

        public ClientRaidFlow(IClientRaidClock clock, IClientRaidTransport transport,
            IClientRaidScenes scenes)
        {
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
            this.scenes = scenes ?? throw new ArgumentNullException(nameof(scenes));
        }

        public void Connected()
        {
            lobbyAdmissionReady = false;
            Phase = RaidPhase.Lobby;
            LoadScene(ClientRaidScene.Lobby);
        }

        public void Reset()
        {
            pending = null;
            entryReadySent = LobbyVisible = lobbyAdmissionReady = false;
            requestAt = 0;
            RaidId = WorldRunId = null;
            Phase = RaidPhase.Lobby; LastResultText = string.Empty;
        }

        public bool SceneCompleted(ClientRaidScene scene)
        {
            if (scene == ClientRaidScene.Lobby)
            {
                LobbyVisible = true;
                if (Phase != RaidPhase.Settling) Phase = RaidPhase.Lobby;
                if (pending == null) Send(NewCommand(LobbyCommandKind.Load));
            }
            else if (Phase == RaidPhase.Loading && !entryReadySent
                && !string.IsNullOrEmpty(RaidId) && !string.IsNullOrEmpty(WorldRunId))
            {
                entryReadySent = true;
                scenes.EnterGameplay();
            }
            else { transport.Disconnect(); return false; }
            return true;
        }

        // The adapter publishes its scene lifecycle fact before business observers advance.
        public void PublishSceneCompletion() => Changed?.Invoke();

        public bool TryStartRaid(out string reason)
        {
            if (!CanStartRaid)
            {
                reason = "入场尚未就绪或操作正在处理中。";
                return false;
            }
            LastResultText = string.Empty;
            LobbyCommand command = NewCommand(LobbyCommandKind.StartRaid);
            Send(command); reason = null; Changed?.Invoke(); return true;
        }

        public bool TryAbandonRaid(out string reason)
        {
            if (Phase != RaidPhase.Active && Phase != RaidPhase.Loading)
            { reason = "当前没有可以放弃的战局。"; return false; }
            LobbyCommand command = NewCommand(Phase == RaidPhase.Loading ? LobbyCommandKind.CancelEntry : LobbyCommandKind.AbandonRaid);
            command.RaidId = RaidId; Send(command);
            Phase = RaidPhase.Settling; Changed?.Invoke(); reason = null; return true;
        }

        public void Tick()
        {
            double now = clock.Now;
            if (!transport.Connected) return;

            // A controlled scene transition can still be finishing when the server's
            // Loading phase arrives. In that window the Unity adapter legitimately
            // returns Deferred; keep the phase alive and retry once Mirror has cleared
            // its scene work instead of leaving the client in a lobby-looking state.
            if (Phase == RaidPhase.Loading && !entryReadySent
                && !string.IsNullOrEmpty(RaidId) && !string.IsNullOrEmpty(WorldRunId)
                && !transport.LoadingScene)
                LoadScene(ClientRaidScene.Gameplay);

            if (transport.LoadingScene) return;
            if (pending != null && now - requestAt > 10d)
            { requestAt = now; transport.Send(pending); }
        }

        public void ReceiveSession(RaidPhase phase, string raidId, string worldRunId, string text)
        {
            if (phase == RaidPhase.Lobby)
            {
                if (!LobbyVisible || pending?.Kind != LobbyCommandKind.Load || string.IsNullOrEmpty(worldRunId)) return;
                pending = null; lobbyAdmissionReady = true; WorldRunId = worldRunId; Phase = RaidPhase.Lobby;
                if (!string.IsNullOrEmpty(text)) LastResultText = text;
            }
            else if (phase == RaidPhase.Loading)
            {
                if (string.IsNullOrEmpty(raidId) || string.IsNullOrEmpty(worldRunId))
                { transport.Disconnect(); return; }
                // Repeated Load requests during a slow reconnect may repeat the same admission phase.
                if (RaidId == raidId && WorldRunId == worldRunId && Phase != RaidPhase.Lobby) return;
                RaidId = raidId; WorldRunId = worldRunId; entryReadySent = false; lobbyAdmissionReady = false;
                pending = null; Phase = phase; LoadScene(ClientRaidScene.Gameplay);
            }
            else if (phase == RaidPhase.Returned)
            {
                if (!string.IsNullOrEmpty(RaidId) && raidId != RaidId) return;
                LastResultText = text ?? string.Empty; pending = null; entryReadySent = false;
                RaidId = WorldRunId = null; Phase = phase; lobbyAdmissionReady = false;
                scenes.ClearWorld(); LoadScene(ClientRaidScene.Lobby);
            }
            else if (phase == RaidPhase.Rejected)
            {
                LastResultText = text ?? "操作未完成。"; pending = null;
                if (LobbyVisible) Phase = RaidPhase.Lobby;
            }
            else
            {
                if (raidId != RaidId) return;
                Phase = phase; if (!string.IsNullOrEmpty(text)) LastResultText = text;
            }
            Changed?.Invoke();
        }

        private void LoadScene(ClientRaidScene scene)
        {
            bool previousVisible = LobbyVisible;
            LobbyVisible = false;
            ClientSceneLoadResult result = scenes.Load(scene);
            if (result == ClientSceneLoadResult.Deferred) { LobbyVisible = previousVisible; return; }
            if (result != ClientSceneLoadResult.Failed) return;
            if (scene == ClientRaidScene.Gameplay && !string.IsNullOrEmpty(RaidId) && Phase == RaidPhase.Loading)
            {
                LobbyVisible = scenes.IsLobbyActive;
                Phase = RaidPhase.Settling;
                LastResultText = "战场加载失败，正在取消入场并返回大厅。";
                LobbyCommand command = NewCommand(LobbyCommandKind.CancelEntry);
                command.RaidId = RaidId; Send(command); Changed?.Invoke();
            }
            else transport.Disconnect();
        }

        private void Send(LobbyCommand command)
        { pending = command; requestAt = clock.Now; transport.Send(command); }
        private static string NewOperationId() => Guid.NewGuid().ToString("N");
        private static LobbyCommand NewCommand(LobbyCommandKind kind) => new LobbyCommand { Kind = kind, OperationId = NewOperationId() };
    }
}
