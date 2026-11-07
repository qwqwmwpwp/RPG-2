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
        private readonly IClientRaidInventoryProjection projection;
        private PersistentInventoryState inventory;
        private LobbyCommand pending;
        private double requestAt, readOnlyRefreshAt;
        private bool entryReadySent;

        public event Action Changed;
        public event Action InventoryChanged;
        public RaidPhase Phase { get; private set; } = RaidPhase.Lobby;
        public string LastResultText { get; private set; } = string.Empty;
        public string RaidId { get; private set; }
        public string WorldRunId { get; private set; }
        public bool LobbyVisible { get; private set; }
        public bool IsInventoryReady => inventory != null && projection.IsReady;
        public long InventoryRevision => inventory?.Revision ?? -1;
        public bool RequestPending => pending != null;
        public bool CanStartRaid => LobbyVisible && IsInventoryReady && !inventory.ReadOnly
            && string.IsNullOrEmpty(inventory.ActiveRaidId) && pending == null;

        public ClientRaidFlow(IClientRaidClock clock, IClientRaidTransport transport,
            IClientRaidScenes scenes, IClientRaidInventoryProjection projection)
        {
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
            this.scenes = scenes ?? throw new ArgumentNullException(nameof(scenes));
            this.projection = projection ?? throw new ArgumentNullException(nameof(projection));
        }

        public void Connected()
        {
            Phase = RaidPhase.Lobby;
            LoadScene(ClientRaidScene.Lobby);
        }

        public void Reset()
        {
            projection.Clear(); inventory = null; pending = null;
            entryReadySent = LobbyVisible = false;
            requestAt = readOnlyRefreshAt = 0;
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
                reason = inventory?.ReadOnly == true ? "上一局正在结算，请稍候。" : "仓库尚未就绪或操作正在处理中。";
                return false;
            }
            LastResultText = string.Empty;
            LobbyCommand command = NewCommand(LobbyCommandKind.StartRaid);
            command.Revision = inventory.Revision;
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

        public void SubmitInventoryOperation(LobbyCommand operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            if (!CanStartRaid)
            {
                projection.PublishResult((PersonalContainerKind)operation.Source,
                    LobbyInventoryResult.Rejected(operation.RequestId, "库存当前只读或正在处理中。"));
                return;
            }
            // Take ownership of a copy: callers cannot change the payload of a later retry.
            Send(new LobbyCommand { Kind = LobbyCommandKind.InventoryOperation, OperationId = NewOperationId(),
                Revision = inventory.Revision, RequestId = operation.RequestId, Source = operation.Source,
                Destination = operation.Destination, OperationKind = operation.OperationKind,
                SourceItemId = operation.SourceItemId, TargetItemId = operation.TargetItemId,
                Count = operation.Count, UpperLeftIndex = operation.UpperLeftIndex });
        }

        public void Tick()
        {
            double now = clock.Now;
            if (!transport.Connected || transport.LoadingScene) return;
            if (pending != null && now - requestAt > 10d)
            { requestAt = now; transport.Send(pending); }
            if (pending == null && LobbyVisible && inventory != null
                && (inventory.ReadOnly || !string.IsNullOrEmpty(inventory.ActiveRaidId))
                && now - readOnlyRefreshAt >= 3d)
            { readOnlyRefreshAt = now; Send(NewCommand(LobbyCommandKind.Load)); }
        }

        public void ReceiveInventory(PersistentInventoryState state, string operationId,
            PersonalContainerKind source, LobbyInventoryResult result)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (inventory != null && state.Revision < inventory.Revision) return;
            projection.Apply(state);
            inventory = state;
            LobbyCommand completed = pending != null && pending.OperationId == operationId ? pending : null;
            if (completed != null) pending = null;
            projection.NotifyChanged(); InventoryChanged?.Invoke(); Changed?.Invoke();
            if (completed?.Kind == LobbyCommandKind.InventoryOperation && (byte)source < 3 && result != null)
                projection.PublishResult(source, result);
        }

        public void ReceiveSession(RaidPhase phase, string raidId, string worldRunId, string text)
        {
            if (phase == RaidPhase.Loading)
            {
                if (string.IsNullOrEmpty(raidId) || string.IsNullOrEmpty(worldRunId))
                { transport.Disconnect(); return; }
                RaidId = raidId; WorldRunId = worldRunId; entryReadySent = false;
                pending = null; Phase = phase; LoadScene(ClientRaidScene.Gameplay);
            }
            else if (phase == RaidPhase.Returned)
            {
                if (!string.IsNullOrEmpty(RaidId) && raidId != RaidId) return;
                LastResultText = text ?? string.Empty; pending = null; entryReadySent = false;
                RaidId = WorldRunId = null; Phase = phase;
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
