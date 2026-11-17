using System;
using System.Collections.Generic;
using RPG4.Raid.Domain;

namespace RPG4.Raid.Application
{
    /// <summary>Main-thread raid lifecycle. Each raid owns its independent role loadout.</summary>
    public sealed class ServerRaidApplication : IDisposable
    {
        private readonly IRaidWorld world;
        private readonly IRaidClientChannel clients;
        private readonly double raidDuration;
        private readonly string worldRunId = Guid.NewGuid().ToString("N");
        private readonly Dictionary<string, RaidPlayerSession> sessions = new Dictionary<string, RaidPlayerSession>();
        private ServerMatchRuntime matchRuntime;
        private bool stopped;
        // Personal deadlines retain the last Tick time; shared-match callbacks receive their adapter time separately.
        private double now;
        public ServerRaidApplication(IRaidWorld world, IRaidClientChannel clients, double raidDuration)
            : this(world, clients, raidDuration, null) { }
        public ServerRaidApplication(IRaidWorld world, IRaidClientChannel clients, double raidDuration, ServerMatchRuntime matchRuntime)
        {
            this.world = world ?? throw new ArgumentNullException(nameof(world));
            this.clients = clients ?? throw new ArgumentNullException(nameof(clients));
            this.raidDuration = Math.Max(30d, raidDuration);
            this.matchRuntime = matchRuntime;
        }
        // The process may configure its match after the network server starts. Ownership stays with the process.
        public void ConfigureMatchRuntime(ServerMatchRuntime runtime) => matchRuntime = runtime;
        public void RegisterSession(string key, RaidIdentity identity)
            => RegisterSession(key, identity, now);
        public void RegisterSession(string key, RaidIdentity identity, double time)
        {
            if (stopped || key == null || identity == null) return;
            if (sessions.TryGetValue(key, out RaidPlayerSession existing))
            {
                bool reconnecting = !existing.Connected;
                existing.Rebind(identity);
                if (reconnecting) matchRuntime?.Reconnect(key, time);
            }
            else sessions.Add(key, new RaidPlayerSession(key, identity));
        }
        private bool Current(RaidPlayerSession session) => session.Connected && clients.IsCurrent(session.Key);
        private bool TrySession(string key, out RaidPlayerSession session)
        {
            session = null;
            return !stopped && key != null && sessions.TryGetValue(key, out session);
        }
        private void Phase(RaidPlayerSession session, RaidPhase phase, string text)
        {
            if (Current(session)) clients.SendPhase(session.Key, phase, session.Raid?.Reservation.RaidId,
                session.Raid?.Reservation.WorldRunId ?? worldRunId, text);
        }
        private void Reject(RaidPlayerSession session, string text) => Phase(session, RaidPhase.Rejected, text);
        private void EnterLoading(RaidPlayerSession session, string text, double time)
        {
            if (!Current(session)) return;
            // Preserve admission semantics for repeated Load/AlreadyJoined and full rosters:
            // a shared-match rejection does not cancel the personal reservation or reject its publication.
            _ = matchRuntime?.JoinPlayer(session.Key, time);
            Phase(session, RaidPhase.Loading, text);
        }
        private void PublishActive(RaidPlayerSession session, double time)
        {
            if (!Current(session)) return;
            matchRuntime?.MarkReady(session.Key, time);
            Phase(session, RaidPhase.Active, null);
        }
        public void HandleCommand(string key, LobbyCommand command)
            => HandleCommand(key, command, now);
        public void HandleCommand(string key, LobbyCommand command, double time)
        {
            if (!TrySession(key, out RaidPlayerSession session) || !Current(session) || command == null
                || !ValidOperationId(command.OperationId)) return;
            if (command.Kind != LobbyCommandKind.Load && command.Kind != LobbyCommandKind.StartRaid
                && command.Kind != LobbyCommandKind.AbandonRaid && command.Kind != LobbyCommandKind.CancelEntry)
            { Reject(session, "请求类型不受支持，请更新客户端。"); return; }
            RaidRun raid = session.Raid;
            if (command.Kind == LobbyCommandKind.AbandonRaid || command.Kind == LobbyCommandKind.CancelEntry)
            {
                if (raid == null || command.RaidId != raid.Reservation.RaidId) return;
                if (raid.Stage == RaidStage.Settling) { Phase(session, RaidPhase.Settling, "正在结算，请稍候…"); return; }
                if (raid.Reservation.Status == RaidStatus.Prepared && !raid.ActivationRequested) CancelEntry(session);
                else if (!raid.IsSpawning) BeginSettlement(session, RaidOutcome.Abandoned);
                return;
            }
            if (session.Busy || raid?.IsSpawning == true || raid?.Stage == RaidStage.Settling) return;
            if (command.Kind == LobbyCommandKind.Load)
            {
                if (world.HasActor(key) && session.Raid?.Reservation.Status != RaidStatus.Active) return;
                session.LobbyAdmissionReady = true;
                if (session.Raid != null) EnterLoading(session, "恢复当前战局…", time);
                else Phase(session, RaidPhase.Lobby, "大厅已就绪。");
                return;
            }
            if (!session.LobbyAdmissionReady || raid != null || world.HasActor(key))
            { Reject(session, "当前不能开始新的战局。"); return; }
            if (!world.CanPrepare(session.Key)) { Reject(session, "战场配置尚未就绪。"); return; }
            session.Raid = new RaidRun(new RaidReservation {
                RaidId = Guid.NewGuid().ToString("N"), WorldRunId = worldRunId,
                AccountId = session.Identity.AccountId, CharacterId = session.Identity.CharacterId,
                Revision = 0, Status = RaidStatus.Prepared
            }, now + 75d);
            EnterLoading(session, null, time);
        }
        private static bool ValidOperationId(string id)
        {
            if (id == null || id.Length != 32) return false;
            foreach (char value in id) if (!(value >= '0' && value <= '9') && !(value >= 'a' && value <= 'f')) return false;
            return true;
        }
        public void Ready(string key)
        {
            if (TrySession(key, out RaidPlayerSession session) && Current(session)
                && session.Raid?.Stage == RaidStage.Prepared && session.Raid.Reservation.Status == RaidStatus.Prepared)
                session.Raid.ReadyRequested = true;
        }
        public void AddPlayer(string key)
            => AddPlayer(key, now);
        public void AddPlayer(string key, double time)
        {
            if (!TrySession(key, out RaidPlayerSession session) || !Current(session)) return;
            RaidRun raid = session.Raid;
            if (raid == null || raid.Reservation.WorldRunId != worldRunId || !raid.ReadyRequested
                || session.Busy || raid.IsSpawning || raid.Stage == RaidStage.Settling) return;
            if (raid.Reservation.Status == RaidStatus.Active)
            {
                if (world.HasActor(key) && world.TryRebindPlayer(key)) PublishActive(session, time);
                return;
            }
            if (raid.Reservation.Status != RaidStatus.Prepared || world.HasActor(key) || !raid.BeginInitialization()) return;
            if (!world.TryInitializePlayer(key, raid.Reservation)) { raid.InitializationFailed(); CancelEntry(session); return; }
            raid.BeginActivation();
            raid.Reservation.Status = RaidStatus.Active;
            raid.ActivationCommitted(raid.Reservation);
            try
            {
                if (!Current(session)) throw new InvalidOperationException("Stale raid publication.");
                world.PublishPlayer(session.Key);
                raid.Published(now + raidDuration);
                PublishActive(session, time);
            }
            catch (Exception) { BeginSettlement(session, RaidOutcome.Disconnected); }
        }
        private void CancelEntry(RaidPlayerSession session)
        {
            RaidRun raid = session.Raid;
            if (session.Busy || raid == null || raid.ActivationRequested) return;
            world.ReleasePlayer(session.Key);
            if (Current(session)) ReturnToLobby(session, "入场已取消。");
            session.Raid = null;
        }
        public void Disconnect(string key)
            => Disconnect(key, now);
        public void Disconnect(string key, double time)
        {
            if (stopped || key == null) return;
            if (TrySession(key, out RaidPlayerSession session))
            {
                // Keep the living actor in the world; a fresh authenticated session may reclaim it.
                session.Connected = false;
                if (session.Raid?.Reservation.Status == RaidStatus.Prepared && !session.Raid.ActivationRequested) CancelEntry(session);
            }
            matchRuntime?.Disconnect(key, time);
        }
        public bool CanReconnect(string key)
        {
            if (!TrySession(key, out RaidPlayerSession session) || session.Connected || session.Raid == null) return false;
            RaidRun raid = session.Raid;
            return raid.Reservation.Status == RaidStatus.Active && (raid.Stage == RaidStage.Active || raid.Stage == RaidStage.Activating)
                || raid.Reservation.Status == RaidStatus.Prepared && raid.Stage == RaidStage.Activating;
        }
        public bool HasPendingSettlement(string key) => TrySession(key, out RaidPlayerSession session)
            && session.Raid?.Stage == RaidStage.Settling;
        public void EndRaid(string key, RaidOutcome outcome)
            => EndRaid(key, outcome, now);
        public void EndRaid(string key, RaidOutcome outcome, double time)
        {
            if (stopped || key == null) return;
            if (TrySession(key, out RaidPlayerSession session)) BeginSettlement(session, outcome);
            // Personal inventory/loot settlement precedes ending shared-match control, as before.
            if (outcome == RaidOutcome.Death) matchRuntime?.MarkDead(key, time);
        }
        public bool TryExtract(string key)
            => TryExtract(key, now);
        public bool TryExtract(string key, double time)
        {
            if (!TrySession(key, out RaidPlayerSession session) || session.Raid?.Reservation.Status != RaidStatus.Active
                || session.Raid.Stage == RaidStage.Settling || !world.CanExtract(key)) return false;
            RaidRun raid = session.Raid;
            BeginSettlement(session, RaidOutcome.Extracted);
            if (raid.Stage != RaidStage.Settling) return false;
            matchRuntime?.MarkExtracted(key, time);
            return true;
        }
        private void BeginSettlement(RaidPlayerSession session, RaidOutcome outcome)
        {
            RaidRun raid = session.Raid;
            if (raid == null || outcome == RaidOutcome.Extracted && !world.CanExtract(session.Key) || !raid.BeginSettlement(outcome)) return;
            Phase(session, RaidPhase.Settling, "正在结算…");
            try { raid.Settlement.Capture(world.FreezePlayer(session.Key)); }
            catch (Exception) { raid.RetryAt = now + 3d; return; }
            SubmitSettlement(session);
        }
        private void SubmitSettlement(RaidPlayerSession session)
        {
            RaidRun raid = session.Raid;
            if (raid?.Stage != RaidStage.Settling || session.Busy) return;
            try
            {
                // Freeze can fail transiently; retry capturing before publishing or releasing ownership.
                if (raid.Settlement.Frozen == null) raid.Settlement.Capture(world.FreezePlayer(session.Key));
                if (raid.Settlement.Outcome == RaidOutcome.Death)
                    world.PublishRoleLoot(session.Key, raid.Settlement.BagId, raid.Settlement.Frozen.Position);
                session.LobbyAdmissionReady = false;
                world.ClearSource(session.Key);
                string text = raid.Settlement.Outcome == RaidOutcome.Death
                    ? "角色已死亡：装备和携行物品已留在战利品箱，保险箱不参与掉落。"
                    : "本局已结束，背包状态随战局释放。";
                if (Current(session)) ReturnToLobby(session, text);
                world.ReleasePlayer(session.Key); session.Raid = null;
            }
            catch (Exception)
            {
                raid.RetryAt = now + 3d;
                Phase(session, RaidPhase.Settling, "战利品箱暂时无法生成，正在重试；角色物品已冻结保留。");
            }
        }
        private void ReturnToLobby(RaidPlayerSession session, string text)
        {
            world.ReturnToLobby(session.Key);
            Phase(session, RaidPhase.Returned, text);
            session.Raid.ReadyRequested = false;
        }
        public void Tick(double time)
        {
            if (stopped) return;
            now = time;
            foreach (RaidPlayerSession session in new List<RaidPlayerSession>(sessions.Values))
            {
                if (session.Busy) continue;
                if (session.Raid?.Stage == RaidStage.Settling) { if (now >= session.Raid.RetryAt) SubmitSettlement(session); }
                else if (session.Raid?.Reservation.Status == RaidStatus.Prepared && now >= session.Raid.Deadline) CancelEntry(session);
                else if (session.Raid?.Reservation.Status == RaidStatus.Active && now >= session.Raid.Deadline) BeginSettlement(session, RaidOutcome.Timeout);
                if (!session.Connected && !session.Busy && session.Raid == null)
                { world.ReleasePlayer(session.Key); clients.Forget(session.Key); sessions.Remove(session.Key); }
            }
        }
        public void Dispose()
        {
            if (stopped) return;
            stopped = true;
            foreach (RaidPlayerSession session in sessions.Values) { world.ReleasePlayer(session.Key); clients.Forget(session.Key); }
            sessions.Clear();
        }
    }
}
