using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RPG4.Raid.Domain;

namespace RPG4.Raid.Application
{
    /// <summary>Server use cases. Tick and all public commands are invoked by the main-thread adapter.</summary>
    public sealed class ServerRaidApplication : IDisposable
    {
        private readonly IServerInventoryRaidService store;
        private readonly ILobbyInventoryExecutor inventory;
        private readonly IRaidWorld world;
        private readonly IRaidClientChannel clients;
        private readonly double raidDuration;
        private readonly Dictionary<string, RaidPlayerSession> sessions = new Dictionary<string, RaidPlayerSession>();
        private readonly ConcurrentQueue<Action> completions = new ConcurrentQueue<Action>();
        private int stopped;
        private double now;

        public ServerRaidApplication(IServerInventoryRaidService store, ILobbyInventoryExecutor inventory,
            IRaidWorld world, IRaidClientChannel clients, double raidDuration)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
            this.world = world ?? throw new ArgumentNullException(nameof(world));
            this.clients = clients ?? throw new ArgumentNullException(nameof(clients));
            this.raidDuration = Math.Max(30d, raidDuration);
        }

        public void RegisterSession(string key, RaidIdentity identity)
        {
            if (Volatile.Read(ref stopped) != 0) return;
            if (!sessions.ContainsKey(key)) sessions.Add(key, new RaidPlayerSession(key, identity));
        }
        private bool Current(RaidPlayerSession session) => session.Connected && clients.IsCurrent(session.Key);
        private bool TrySession(string key, out RaidPlayerSession session)
        {
            session = null;
            return Volatile.Read(ref stopped) == 0 && key != null && sessions.TryGetValue(key, out session);
        }
        private void Phase(RaidPlayerSession session, RaidPhase phase, string text)
        {
            if (Current(session)) clients.SendPhase(session.Key, phase, session.Raid?.Reservation.RaidId,
                session.Raid?.Reservation.WorldRunId ?? store.WorldRunId, text);
        }
        private void Reject(RaidPlayerSession session, string text) => Phase(session, RaidPhase.Rejected, text);

        public void HandleCommand(string key, LobbyCommand command)
        {
            if (!TrySession(key, out RaidPlayerSession session) || !Current(session) || command == null
                || !ValidOperationId(command.OperationId) || command.Kind > LobbyCommandKind.CancelEntry) return;
            if (session.Replies.TryGetValue(command.OperationId, out LobbyReply reply))
            {
                if (!reply.Command.SamePayload(command)) { Reject(session, "操作标识已使用，请刷新库存。"); return; }
                clients.SendSnapshot(key, reply.Inventory, reply.Command, reply.Result); return;
            }
            RaidRun raid = session.Raid;
            if (command.Kind == LobbyCommandKind.AbandonRaid || command.Kind == LobbyCommandKind.CancelEntry)
            {
                if (raid == null || command.RaidId != raid.Reservation.RaidId) return;
                if (raid.Stage == RaidStage.Settling) { Phase(session, RaidPhase.Settling, "正在结算，请稍候…"); return; }
                if (raid.Reservation.Status == PersistentRaidStatus.Prepared && !raid.ActivationRequested) CancelEntry(session);
                else if (!raid.IsSpawning) BeginSettlement(session, RaidOutcome.Abandoned);
                return;
            }
            if (session.Busy || session.Pending != null || raid?.IsSpawning == true || raid?.Stage == RaidStage.Settling) return;
            if (command.Kind == LobbyCommandKind.Load)
            {
                if (world.HasActor(key)) return;
                PersistentContainerData grant;
                try { grant = inventory.CreateInitialStash(); }
                catch (Exception) { Reject(session, "初始仓库配置不可用。"); return; }
                session.Busy = true;
                Observe(() => store.LoadAsync(session.Identity, grant, CancellationToken.None), state =>
                {
                    session.Busy = false;
                    if (!Current(session)) return;
                    session.Inventory = state;
                    Remember(session, command, LobbyInventoryResult.Accepted(command.RequestId));
                }, error => { session.Busy = false; if (Current(session)) Reject(session, "仓库暂时不可用，请稍后重试。"); });
                return;
            }
            if (session.Inventory == null || session.Inventory.ReadOnly || !string.IsNullOrEmpty(session.Inventory.ActiveRaidId)
                || raid != null || world.HasActor(key))
            { Reject(session, "库存已改变或上一局正在结算，请刷新仓库。"); return; }
            if (session.Inventory.Revision != command.Revision)
            { ReloadRejected(session, command, "库存已更新，已重新同步，请重试操作。"); return; }
            if (command.Kind == LobbyCommandKind.StartRaid) PrepareEntry(session, command);
            else if (command.Kind == LobbyCommandKind.InventoryOperation) CommitInventory(session, command);
        }

        private static bool ValidOperationId(string id)
        {
            if (id == null || id.Length != 32) return false;
            foreach (char value in id) if (!(value >= '0' && value <= '9') && !(value >= 'a' && value <= 'f')) return false;
            return true;
        }

        private void CommitInventory(RaidPlayerSession session, LobbyCommand command)
        {
            if (command.Source > 2 || command.Destination > 2 || command.OperationKind > (byte)LobbyInventoryOperationKind.SwapItems) return;
            try
            {
                LobbyInventoryCandidate candidate = inventory.Execute(session.Inventory, command);
                if (!candidate.Result.Success) { Remember(session, command, candidate.Result); return; }
                session.Pending = new PendingLobbyOperation { Kind = PendingLobbyOperationKind.Commit, Command = command, Candidate = candidate };
                SubmitInventory(session);
            }
            catch (Exception) { Remember(session, command, LobbyInventoryResult.Rejected(command.RequestId, "库存操作未通过校验。")); }
        }
        private void SubmitInventory(RaidPlayerSession session)
        {
            PendingLobbyOperation pending = session.Pending;
            LobbyCommand command = pending.Command;
            LobbyInventoryCandidate candidate = pending.Candidate;
            session.Busy = true;
            Observe(() => store.CommitInventoryAsync(session.Identity, command.OperationId, command.Revision,
                candidate.Stash, candidate.Backpack, candidate.Equipment, CancellationToken.None), state =>
            {
                session.Busy = false; session.Pending = null; session.Inventory = state;
                if (Current(session)) Remember(session, command, candidate.Result);
            }, error =>
            {
                session.Busy = false;
                if (error is InventoryRaidException) { session.Pending = null; ReloadRejected(session, command, "库存版本或物品校验失败，已重新读取。"); }
                else pending.RetryAt = now + 3d; // Unknown commit outcome: keep the same ID and candidate.
            });
        }
        private void ReloadRejected(RaidPlayerSession session, LobbyCommand command, string text)
        {
            session.Busy = true;
            Observe(() => store.LoadAsync(session.Identity, null, CancellationToken.None), state =>
            {
                session.Busy = false; session.Inventory = state;
                if (!Current(session)) return;
                Remember(session, command, LobbyInventoryResult.Rejected(command.RequestId, text));
                if (command.Kind != LobbyCommandKind.InventoryOperation) Reject(session, text);
            }, error => { session.Busy = false; if (Current(session)) Reject(session, "库存暂不可用，请稍后重新登录。"); });
        }
        private void PrepareEntry(RaidPlayerSession session, LobbyCommand command)
        {
            if (!world.CanPrepare(session.Key)) { Reject(session, "战场或掉落包配置尚未就绪。"); return; }
            session.Pending = new PendingLobbyOperation { Kind = PendingLobbyOperationKind.Prepare, Command = command };
            SubmitPrepare(session);
        }
        private void SubmitPrepare(RaidPlayerSession session)
        {
            PendingLobbyOperation pending = session.Pending;
            session.Busy = true;
            Observe(() => store.PrepareRaidAsync(session.Identity, pending.Command.OperationId, pending.Command.Revision, CancellationToken.None), reservation =>
            {
                session.Busy = false; session.Pending = null;
                session.Raid = new RaidRun(reservation, now + 75d);
                if (!Current(session)) { CancelEntry(session); return; }
                Phase(session, RaidPhase.Loading, null);
            }, error =>
            {
                session.Busy = false;
                if (error is InventoryRaidException) { session.Pending = null; ReloadRejected(session, pending.Command, "出战准备未通过校验，请检查仓库。"); }
                else pending.RetryAt = now + 3d;
            });
        }

        public void Ready(string key)
        {
            if (TrySession(key, out RaidPlayerSession session) && Current(session)
                && session.Raid?.Stage == RaidStage.Prepared && session.Raid.Reservation.Status == PersistentRaidStatus.Prepared)
                session.Raid.ReadyRequested = true;
        }
        public void AddPlayer(string key)
        {
            if (!TrySession(key, out RaidPlayerSession session) || !Current(session)) return;
            RaidRun raid = session.Raid;
            if (raid == null || raid.Reservation.Status != PersistentRaidStatus.Prepared || raid.Reservation.WorldRunId != store.WorldRunId
                || !raid.ReadyRequested || session.Busy || raid.IsSpawning || raid.Stage == RaidStage.Settling || world.HasActor(key)) return;
            if (!raid.BeginInitialization()) return;
            if (!world.TryInitializePlayer(key, raid.Reservation)) { raid.InitializationFailed(); CancelEntry(session); return; }
            raid.BeginActivation();
            ActivateAndPublish(session);
        }
        private void ActivateAndPublish(RaidPlayerSession session)
        {
            RaidRun raid = session.Raid;
            session.Busy = true;
            Observe(() => store.ActivateRaidAsync(session.Identity, raid.Reservation.RaidId, CancellationToken.None), reservation =>
            {
                session.Busy = false; raid.ActivationCommitted(reservation);
                try
                {
                    if (!Current(session)) throw new InvalidOperationException("Stale raid publication.");
                    world.PublishPlayer(session.Key);
                    raid.Published(now + raidDuration);
                    Phase(session, RaidPhase.Active, null);
                }
                catch (Exception) { BeginSettlement(session, RaidOutcome.Disconnected); }
            }, error => { session.Busy = false; raid.RetryAt = now + 3d; });
        }
        private void CancelEntry(RaidPlayerSession session)
        {
            RaidRun raid = session.Raid;
            if (session.Busy || raid == null || raid.ActivationRequested) return;
            session.Busy = true;
            Observe(() => store.CancelPreparedRaidAsync(session.Identity, raid.Reservation.RaidId, CancellationToken.None), state =>
            {
                session.Busy = false; session.Inventory = state;
                world.ReleasePlayer(session.Key);
                if (Current(session)) ReturnToLobby(session, "入场已取消，携行物品已保留。");
                session.Raid = null;
            }, error => { session.Busy = false; raid.Deadline = now + 3d; });
        }

        public void Disconnect(string key)
        {
            if (!TrySession(key, out RaidPlayerSession session)) return;
            // Must be called before the transport destroys the owned actor.
            if (session.Raid?.Reservation.Status == PersistentRaidStatus.Active && session.Raid.Stage != RaidStage.Settling)
                BeginSettlement(session, RaidOutcome.Disconnected);
            session.Connected = false;
            if (session.Raid?.Reservation.Status == PersistentRaidStatus.Prepared && !session.Raid.ActivationRequested) CancelEntry(session);
        }
        public void EndRaid(string key, RaidOutcome outcome)
        { if (TrySession(key, out RaidPlayerSession session)) BeginSettlement(session, outcome); }
        public bool TryExtract(string key)
        {
            if (!TrySession(key, out RaidPlayerSession session) || session.Raid?.Reservation.Status != PersistentRaidStatus.Active
                || session.Raid.Stage == RaidStage.Settling || !world.CanExtract(key)) return false;
            BeginSettlement(session, RaidOutcome.Extracted);
            return session.Raid?.Stage == RaidStage.Settling;
        }
        private void BeginSettlement(RaidPlayerSession session, RaidOutcome outcome)
        {
            RaidRun raid = session.Raid;
            if (raid == null) return;
            if (outcome == RaidOutcome.Extracted && !world.CanExtract(session.Key)) outcome = RaidOutcome.Death;
            if (!raid.BeginSettlement(outcome)) return; // Terminal gate precedes cancellation/events and snapshot capture.
            raid.RetryAt = now;
            raid.Settlement.Capture(world.FreezePlayer(session.Key));
            Phase(session, RaidPhase.Settling, outcome == RaidOutcome.Extracted ? "撤离成功，正在保存携行物品…" : "战局结束，正在处理掉落与结算…");
            SubmitSettlement(session);
        }
        private void SubmitSettlement(RaidPlayerSession session)
        {
            RaidRun raid = session.Raid;
            RaidSettlementIntent intent = raid?.Settlement;
            if (raid?.Stage != RaidStage.Settling || session.Busy || intent?.Frozen == null) return;
            session.Busy = true;
            Observe(() => store.SettleRaidAsync(session.Identity, raid.Reservation.RaidId, intent.OperationId, intent.Outcome,
                intent.BagId, intent.Frozen.Backpack, intent.Frozen.Equipment, CancellationToken.None), result =>
            {
                session.Inventory = result.Inventory;
                try
                {
                    world.ClearSource(session.Key);
                    if (result.DropBag != null) world.PublishDropBag(session.Key, result.DropBag, intent.Frozen.Position);
                    if (result.DropBag == null) CompleteSettlement(session);
                    else Observe(() => MarkPublished(session.Identity, raid.Reservation.RaidId, result.DropBag.BagId), state =>
                    { session.Inventory = state; CompleteSettlement(session); }, error => RetrySettlement(session));
                }
                catch (Exception) { RetrySettlement(session); }
            }, error =>
            {
                RetrySettlement(session);
                Phase(session, RaidPhase.Settling, "结算服务暂不可用，正在重试；保存完成前不能开始下一局。");
            });
        }
        private async Task<PersistentInventoryState> MarkPublished(RaidIdentity identity, string raidId, string bagId)
        {
            await store.MarkDropBagPublishedAsync(raidId, bagId, CancellationToken.None).ConfigureAwait(false);
            return await store.LoadAsync(identity, null, CancellationToken.None).ConfigureAwait(false);
        }
        private void RetrySettlement(RaidPlayerSession session) { session.Busy = false; session.Raid.RetryAt = now + 3d; }
        private void CompleteSettlement(RaidPlayerSession session)
        {
            session.Busy = false;
            string text = SettlementText(session.Raid.Settlement.Outcome);
            if (Current(session)) ReturnToLobby(session, text);
            world.ReleasePlayer(session.Key); session.Raid = null;
            foreach (RaidPlayerSession current in sessions.Values)
                if (!ReferenceEquals(current, session) && current.Identity.AccountId == session.Identity.AccountId && !current.Busy
                    && current.Pending == null && Current(current) && current.Raid == null && !world.HasActor(current.Key)
                    && session.Inventory != null && (current.Inventory == null || session.Inventory.Revision > current.Inventory.Revision))
                {
                    current.Inventory = session.Inventory;
                    clients.SendSnapshot(current.Key, current.Inventory, new LobbyCommand { Kind = LobbyCommandKind.Load, OperationId = Guid.NewGuid().ToString("N") },
                        LobbyInventoryResult.Accepted(0));
                    Phase(current, RaidPhase.Lobby, text);
                }
        }
        private void ReturnToLobby(RaidPlayerSession session, string text)
        {
            world.ReturnToLobby(session.Key);
            Phase(session, RaidPhase.Returned, text);
            session.Raid.ReadyRequested = false;
        }
        private static string SettlementText(RaidOutcome outcome)
        {
            switch (outcome)
            {
                case RaidOutcome.Extracted: return "撤离成功：当前携行物品已保留，可自行移入仓库。";
                case RaidOutcome.Death: return "角色已死亡：携行物品已留在战场掉落包，仓库不受影响。";
                case RaidOutcome.Timeout: return "战局时间已到：携行物品已留在战场掉落包，仓库不受影响。";
                case RaidOutcome.Disconnected: return "战局连接已中断：携行物品已留在战场掉落包。";
                default: return "已放弃本局：携行物品已留在战场掉落包，仓库不受影响。";
            }
        }
        private void Remember(RaidPlayerSession session, LobbyCommand command, LobbyInventoryResult result)
        {
            if (session.Inventory == null) return;
            if (!session.Replies.ContainsKey(command.OperationId))
            {
                session.Replies.Add(command.OperationId, new LobbyReply { Inventory = session.Inventory, Command = command, Result = result });
                session.ReplyOrder.Enqueue(command.OperationId);
                while (session.ReplyOrder.Count > 128) session.Replies.Remove(session.ReplyOrder.Dequeue());
            }
            clients.SendSnapshot(session.Key, session.Inventory, command, result);
        }

        private void Observe<T>(Func<Task<T>> start, Action<T> success, Action<Exception> failure)
        {
            Task<T> task;
            try { task = start(); }
            catch (Exception error) { failure(error); return; }
            task.ContinueWith(completed =>
            {
                Exception error = completed.IsFaulted ? completed.Exception.GetBaseException()
                    : completed.IsCanceled ? new TaskCanceledException(completed) : null;
                if (Volatile.Read(ref stopped) != 0) return;
                completions.Enqueue(error != null ? (Action)(() => failure(error)) : () => success(completed.Result));
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        public void Tick(double time)
        {
            if (Volatile.Read(ref stopped) != 0) return;
            now = time;
            int work = completions.Count;
            while (work-- > 0 && completions.TryDequeue(out Action completed))
                try { completed(); } catch (Exception) { clients.CompletionFailed(); }
            foreach (RaidPlayerSession session in new List<RaidPlayerSession>(sessions.Values))
            {
                if (session.Busy) continue;
                if (session.Pending != null)
                {
                    if (now >= session.Pending.RetryAt)
                    {
                        if (session.Pending.Kind == PendingLobbyOperationKind.Commit) SubmitInventory(session); else SubmitPrepare(session);
                    }
                }
                else if (session.Raid?.Stage == RaidStage.Settling) { if (now >= session.Raid.RetryAt) SubmitSettlement(session); }
                else if (session.Raid?.Stage == RaidStage.Activating) { if (now >= session.Raid.RetryAt) ActivateAndPublish(session); }
                else if (session.Raid?.Reservation.Status == PersistentRaidStatus.Prepared && now >= session.Raid.Deadline) CancelEntry(session);
                else if (session.Raid?.Reservation.Status == PersistentRaidStatus.Active && now >= session.Raid.Deadline) BeginSettlement(session, RaidOutcome.Timeout);
                if (!session.Connected && !session.Busy && session.Pending == null && session.Raid == null)
                { world.ReleasePlayer(session.Key); clients.Forget(session.Key); sessions.Remove(session.Key); }
            }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref stopped, 1) != 0) return;
            foreach (RaidPlayerSession session in sessions.Values) { world.ReleasePlayer(session.Key); clients.Forget(session.Key); }
            sessions.Clear(); while (completions.TryDequeue(out _)) { }
        }
    }
}
