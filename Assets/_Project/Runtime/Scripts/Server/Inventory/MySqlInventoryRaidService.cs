using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MySqlConnector;
using RPG4.Backpack.Items;
using RPG4.Raid.Application;
using RPG4.Raid.Domain;

namespace RPG4.Server.Inventory
{
    /// <summary>Durable account inventory and single-character raid lifecycle. No Unity object is accessed here.</summary>
    public sealed class MySqlInventoryRaidService : IServerInventoryRaidService, IDisposable
    {
        private readonly Func<bool, MySqlConnection> createConnection;
        private readonly Func<CancellationToken, Task> validateSchema;
        private readonly int commandTimeout;
        private readonly string serverId;
        private readonly object initializeGate = new object();
        private Task<PersistentItemIdRange> initialization;
        private MySqlConnection worldLease;
        private string worldLock;
        private ulong leaseConnection;
        private PersistentItemIdRange range;
        // Recovery consumes from the reserved startup range before the gameplay generator is configured.
        private ulong recoveryNextId;
        private int disposed;
        public string WorldRunId { get; } = Guid.NewGuid().ToString("N");

        public MySqlInventoryRaidService(Func<bool, MySqlConnection> createConnection,
            Func<CancellationToken, Task> validateSchema, int commandTimeout, string serverId)
        {
            this.createConnection = createConnection ?? throw new ArgumentNullException(nameof(createConnection));
            this.validateSchema = validateSchema ?? throw new ArgumentNullException(nameof(validateSchema));
            this.commandTimeout = commandTimeout;
            this.serverId = serverId;
        }
        public Task<PersistentItemIdRange> InitializeAsync(CancellationToken token)
        {
            lock (initializeGate) return initialization ?? (initialization = InitializeCoreAsync(token));
        }
        private MySqlCommand Command(MySqlConnection connection, MySqlTransaction transaction, string sql, params (string key, object value)[] values)
        {
            var command = new MySqlCommand(sql, connection, transaction) { CommandTimeout = commandTimeout };
            foreach (var pair in values) command.Parameters.AddWithValue(pair.key, pair.value ?? DBNull.Value);
            return command;
        }
        private async Task<int> Execute(MySqlConnection c, MySqlTransaction t, string sql, CancellationToken token, params (string, object)[] values)
        { using (var command = Command(c, t, sql, values)) return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false); }
        private async Task<object> Scalar(MySqlConnection c, MySqlTransaction t, string sql, CancellationToken token, params (string, object)[] values)
        { using (var command = Command(c, t, sql, values)) return await command.ExecuteScalarAsync(token).ConfigureAwait(false); }

        private async Task<T> Transaction<T>(Func<MySqlConnection, MySqlTransaction, Task<T>> work, CancellationToken token, bool checkWorld = true)
        {
            if (Volatile.Read(ref disposed) != 0) throw new InventoryRaidException("inventory_service_stopped");
            var connection = createConnection(true);
            await using (connection.ConfigureAwait(false))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, token).ConfigureAwait(false);
                await using (transaction.ConfigureAwait(false))
                {
                    try
                    {
                        if (checkWorld) await RequireWorld(connection, transaction, token).ConfigureAwait(false);
                        T result = await work(connection, transaction).ConfigureAwait(false);
                        if (checkWorld) await RequireLease(connection, transaction, token).ConfigureAwait(false);
                        await transaction.CommitAsync(token).ConfigureAwait(false);
                        return result;
                    }
                    catch
                    {
                        using (var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                            try { await transaction.RollbackAsync(recovery.Token).ConfigureAwait(false); } catch (Exception) { }
                        throw;
                    }
                }
            }
        }
        private async Task RequireLease(MySqlConnection c, MySqlTransaction t, CancellationToken token)
        {
            object owner = await Scalar(c, t, "SELECT IS_USED_LOCK(@name)", token, ("@name", worldLock)).ConfigureAwait(false);
            if (owner == null || owner == DBNull.Value || Convert.ToUInt64(owner) != leaseConnection || Volatile.Read(ref disposed) != 0)
                throw new InventoryRaidException("inventory_world_lease_lost");
        }
        private async Task RequireWorld(MySqlConnection c, MySqlTransaction t, CancellationToken token)
        {
            if (range.First == 0) throw new InventoryRaidException("inventory_service_not_ready");
            await RequireLease(c, t, token).ConfigureAwait(false);
            object status = await Scalar(c, t, "SELECT status FROM world_runs WHERE world_run_id=@world AND server_id=@server LOCK IN SHARE MODE", token,
                ("@world", WorldRunId), ("@server", serverId)).ConfigureAwait(false);
            if (status == null || Convert.ToInt32(status) != 0) throw new InventoryRaidException("inventory_world_closed");
        }
        private async Task<PersistentItemIdRange> InitializeCoreAsync(CancellationToken token)
        {
            await validateSchema(token).ConfigureAwait(false);
            // A named lock must be released by closing its physical connection, never returned to a pool.
            worldLease = createConnection(false);
            try
            {
                await worldLease.OpenAsync(token).ConfigureAwait(false);
                worldLock = Convert.ToString(await Scalar(worldLease, null, "SELECT SHA2(CONCAT(DATABASE(),':rpg4-world:',@server),256)", token, ("@server", serverId)).ConfigureAwait(false));
                if (Convert.ToInt32(await Scalar(worldLease, null, "SELECT GET_LOCK(@name,0)", token, ("@name", worldLock)).ConfigureAwait(false)) != 1)
                    throw new InventoryRaidException("inventory_server_id_already_running");
                leaseConnection = Convert.ToUInt64(await Scalar(worldLease, null, "SELECT CONNECTION_ID()", token).ConfigureAwait(false));
                range = await Transaction(async (c, t) =>
                {
                    object previous = await Scalar(c, t, "SELECT last_issued FROM item_id_allocator WHERE id=1 FOR UPDATE", token).ConfigureAwait(false);
                    if (previous == null) throw new InventoryRaidException("inventory_allocator_missing");
                    ulong last = Convert.ToUInt64(previous);
                    const ulong block = 1UL << 32;
                    if (last > ulong.MaxValue - block) throw new InventoryRaidException("inventory_item_ids_exhausted");
                    await Execute(c, t, "UPDATE item_id_allocator SET last_issued=@last WHERE id=1", token, ("@last", last + block)).ConfigureAwait(false);
                    await Execute(c, t, "INSERT INTO world_runs(world_run_id,server_id,status,started_utc) VALUES(@world,@server,0,UTC_TIMESTAMP(6))", token,
                        ("@world", WorldRunId), ("@server", serverId)).ConfigureAwait(false);
                    return new PersistentItemIdRange(last + 1, last + block);
                }, token, false).ConfigureAwait(false);
                recoveryNextId = range.First;
                await RecoverPreviousWorldsAsync(token).ConfigureAwait(false);
                if (recoveryNextId != range.First) range = new PersistentItemIdRange(recoveryNextId, range.Last);
                return range;
            }
            catch { Dispose(); throw; }
        }

        public Task<PersistentInventoryState> LoadAsync(RaidIdentity identity, PersistentContainerData initialStash, CancellationToken token)
        {
            RequireIdentity(identity);
            PersistentContainerData grant = initialStash == null ? null : PersistentInventoryCodec.Freeze(initialStash);
            if (grant != null && grant.Kind != PersonalContainerKind.Stash) throw new InventoryRaidException("inventory_grant_invalid");
            return Transaction(async (c, t) =>
            {
                PersistentInventoryState state = await ReadProfile(c, t, identity.AccountId, identity.CharacterId, token, false).ConfigureAwait(false);
                if (state != null) return state;
                if (grant == null) throw new InventoryRaidException("inventory_initial_grant_required");
                state = new PersistentInventoryState
                {
                    AccountId = identity.AccountId, CharacterId = identity.CharacterId, Revision = 1,
                    Stash = grant, Backpack = PersistentInventoryCodec.Empty(PersonalContainerKind.Backpack), Equipment = PersistentInventoryCodec.Empty(PersonalContainerKind.Equipment)
                };
                // Serialize creation on the permanent account row, including two first-load requests.
                object account = await Scalar(c, t, "SELECT a.account_id FROM accounts a JOIN characters ch ON ch.account_id=a.account_id WHERE a.account_id=@account AND ch.character_id=@character AND a.disabled=0 FOR UPDATE", token,
                    ("@account", identity.AccountId), ("@character", identity.CharacterId)).ConfigureAwait(false);
                if (account == null) throw new InventoryRaidException("inventory_identity_invalid");
                PersistentInventoryState raced = await ReadProfile(c, t, identity.AccountId, identity.CharacterId, token, false).ConfigureAwait(false);
                if (raced != null) return raced;
                var ids = PersistentInventoryCodec.Items(grant);
                PersistentInventoryCodec.State(state); // Reject a full state exceeding the shared 8 MiB wire limit BEFORE writes.
                foreach (ulong id in ids.Keys.OrderBy(id => id))
                {
                    RequireNewId(id);
                    await Execute(c, t, "INSERT INTO item_ownership(item_id,owner_kind,owner_scope) VALUES(@id,0,@owner)", token,
                        ("@id", id), ("@owner", AccountScope(identity.AccountId))).ConfigureAwait(false);
                }
                await Execute(c, t, "INSERT INTO inventory_profiles(account_id,character_id,revision,grant_version,stash_payload,backpack_payload,equipment_payload) VALUES(@account,@character,1,1,@stash,@backpack,@equipment)", token,
                    ("@account", state.AccountId), ("@character", state.CharacterId), ("@stash", PersistentInventoryCodec.Container(state.Stash)),
                    ("@backpack", PersistentInventoryCodec.Container(state.Backpack)), ("@equipment", PersistentInventoryCodec.Container(state.Equipment))).ConfigureAwait(false);
                return state;
            }, token);
        }

        public Task<PersistentInventoryState> CommitInventoryAsync(RaidIdentity identity, string operationId, long expectedRevision,
            PersistentContainerData stash, PersistentContainerData backpack, PersistentContainerData equipment, CancellationToken token)
        {
            RequireIdentity(identity); RequireOperation(operationId);
            stash = PersistentInventoryCodec.Freeze(stash); backpack = PersistentInventoryCodec.Freeze(backpack); equipment = PersistentInventoryCodec.Freeze(equipment);
            RequireKinds(stash, backpack, equipment);
            var after = PersistentInventoryCodec.Items(stash, backpack, equipment);
            byte[] fingerprint = Fingerprint("inventory", expectedRevision.ToString(CultureInfo.InvariantCulture),
                PersistentInventoryCodec.Container(stash), PersistentInventoryCodec.Container(backpack), PersistentInventoryCodec.Container(equipment));
            return Transaction(async (c, t) =>
            {
                PersistentInventoryState state = await ReadProfile(c, t, identity.AccountId, identity.CharacterId, token).ConfigureAwait(false);
                byte[] replay = await Replay(c, t, identity.AccountId, operationId, "inventory", fingerprint, token).ConfigureAwait(false);
                if (replay != null) return PersistentInventoryCodec.State(replay);
                RequireWritable(state, expectedRevision);
                var before = PersistentInventoryCodec.Items(state.Stash, state.Backpack, state.Equipment);
                PersistentInventoryCodec.RequireSameQuantities(before, after);
                foreach (ulong id in before.Keys.Union(after.Keys).OrderBy(id => id))
                {
                    Owner owner = await ReadOwner(c, t, id, token).ConfigureAwait(false);
                    if (owner == null)
                    {
                        if (before.ContainsKey(id)) throw new InventoryRaidException("inventory_ownership_missing");
                        RequireNewId(id);
                        await InsertOwner(c, t, id, 0, AccountScope(identity.AccountId), null, null, token).ConfigureAwait(false);
                    }
                    else if (owner.Kind != 0 || owner.Scope != AccountScope(identity.AccountId)) throw new InventoryRaidException("inventory_ownership_conflict");
                    if (!after.ContainsKey(id)) await WriteOwner(c, t, id, 2, AccountScope(identity.AccountId), null, null, token).ConfigureAwait(false);
                }
                state.Stash = stash; state.Backpack = backpack; state.Equipment = equipment; state.Revision = checked(state.Revision + 1);
                await WriteProfile(c, t, state, token).ConfigureAwait(false);
                await Remember(c, t, identity.AccountId, operationId, "inventory", fingerprint, PersistentInventoryCodec.State(state), token).ConfigureAwait(false);
                return state;
            }, token);
        }

        public Task<RaidReservation> PrepareRaidAsync(RaidIdentity identity, string operationId, long expectedRevision, CancellationToken token)
        {
            RequireIdentity(identity); RequireOperation(operationId);
            byte[] fingerprint = Fingerprint("prepare", expectedRevision.ToString(CultureInfo.InvariantCulture), Encoding.UTF8.GetBytes(identity.SessionId));
            return Transaction(async (c, t) =>
            {
                PersistentInventoryState state = await ReadProfile(c, t, identity.AccountId, identity.CharacterId, token).ConfigureAwait(false);
                byte[] replay = await Replay(c, t, identity.AccountId, operationId, "prepare", fingerprint, token).ConfigureAwait(false);
                if (replay != null)
                {
                    RaidReservation original = PersistentInventoryCodec.Reservation(replay);
                    RaidRow current = await ReadRaid(c, t, original.RaidId, token).ConfigureAwait(false);
                    RequireRaidOwner(current, identity, true);
                    original.Status = current.Status; original.Revision = current.Revision;
                    return original;
                }
                RequireWritable(state, expectedRevision);
                var raid = new RaidReservation
                {
                    RaidId = Guid.NewGuid().ToString("N"), WorldRunId = WorldRunId, AccountId = identity.AccountId, CharacterId = identity.CharacterId,
                    Revision = checked(state.Revision + 1), Status = PersistentRaidStatus.Prepared, Backpack = state.Backpack, Equipment = state.Equipment
                };
                var carried = PersistentInventoryCodec.Items(raid.Backpack, raid.Equipment);
                foreach (ulong id in carried.Keys.OrderBy(id => id))
                {
                    Owner owner = await ReadOwner(c, t, id, token).ConfigureAwait(false);
                    if (owner == null || owner.Kind != 0 || owner.Scope != AccountScope(identity.AccountId)) throw new InventoryRaidException("inventory_ownership_conflict");
                    await WriteOwner(c, t, id, 1, WorldRunId, raid.RaidId, null, token).ConfigureAwait(false);
                }
                await Execute(c, t, "INSERT INTO raid_runs(raid_id,world_run_id,account_id,character_id,session_id,status,revision,entry_payload,created_utc) VALUES(@raid,@world,@account,@character,@session,0,@revision,@entry,UTC_TIMESTAMP(6))", token,
                    ("@raid", raid.RaidId), ("@world", WorldRunId), ("@account", identity.AccountId), ("@character", identity.CharacterId), ("@session", identity.SessionId),
                    ("@revision", raid.Revision), ("@entry", PersistentInventoryCodec.Reservation(raid))).ConfigureAwait(false);
                state.ActiveRaidId = raid.RaidId; state.ReadOnly = true; state.Revision = raid.Revision;
                state.Backpack = PersistentInventoryCodec.Empty(PersonalContainerKind.Backpack); state.Equipment = PersistentInventoryCodec.Empty(PersonalContainerKind.Equipment);
                await WriteProfile(c, t, state, token).ConfigureAwait(false);
                await Remember(c, t, identity.AccountId, operationId, "prepare", fingerprint, PersistentInventoryCodec.Reservation(raid), token).ConfigureAwait(false);
                return raid;
            }, token);
        }

        public Task<RaidReservation> ActivateRaidAsync(RaidIdentity identity, string raidId, CancellationToken token)
        {
            RequireIdentity(identity); RequireId(raidId);
            return Transaction(async (c, t) =>
            {
                PersistentInventoryState state = await ReadProfile(c, t, identity.AccountId, identity.CharacterId, token).ConfigureAwait(false);
                RaidRow raid = await ReadRaid(c, t, raidId, token).ConfigureAwait(false); RequireRaidOwner(raid, identity, true);
                if (state.ActiveRaidId != raidId || raid.World != WorldRunId) throw new InventoryRaidException("raid_gate_mismatch");
                if (raid.Status != PersistentRaidStatus.Prepared && raid.Status != PersistentRaidStatus.Active) throw new InventoryRaidException("raid_not_prepared");
                if (raid.Status == PersistentRaidStatus.Prepared)
                {
                    state.Revision = checked(state.Revision + 1);
                    if (await Execute(c, t, "UPDATE raid_runs SET status=1,revision=@revision WHERE raid_id=@raid AND status=0 AND revision=@previous", token,
                        ("@revision", state.Revision), ("@raid", raidId), ("@previous", raid.Revision)).ConfigureAwait(false) != 1) throw new InventoryRaidException("raid_transition_conflict");
                    await WriteProfile(c, t, state, token).ConfigureAwait(false);
                }
                RaidReservation result = raid.Entry; result.Status = PersistentRaidStatus.Active; result.Revision = state.Revision; return result;
            }, token);
        }

        public Task<PersistentInventoryState> CancelPreparedRaidAsync(RaidIdentity identity, string raidId, CancellationToken token)
        {
            RequireIdentity(identity); RequireId(raidId);
            return Transaction(async (c, t) =>
            {
                PersistentInventoryState state = await ReadProfile(c, t, identity.AccountId, identity.CharacterId, token).ConfigureAwait(false);
                RaidRow raid = await ReadRaid(c, t, raidId, token).ConfigureAwait(false); RequireRaidOwner(raid, identity, true);
                if (raid.Status == PersistentRaidStatus.Cancelled) return state;
                return await CancelPrepared(c, t, state, raid, token).ConfigureAwait(false);
            }, token);
        }
        private async Task<PersistentInventoryState> CancelPrepared(MySqlConnection c, MySqlTransaction t, PersistentInventoryState state, RaidRow raid, CancellationToken token)
        {
            if (raid.Status != PersistentRaidStatus.Prepared || state.ActiveRaidId != raid.Entry.RaidId) throw new InventoryRaidException("raid_cannot_rollback_active");
            foreach (ulong id in PersistentInventoryCodec.Items(raid.Entry.Backpack, raid.Entry.Equipment).Keys.OrderBy(id => id))
            {
                Owner owner = await ReadOwner(c, t, id, token).ConfigureAwait(false);
                if (owner == null || owner.Kind != 1 || owner.Scope != raid.World || owner.Raid != raid.Entry.RaidId) throw new InventoryRaidException("raid_ownership_conflict");
                await WriteOwner(c, t, id, 0, AccountScope(state.AccountId), null, null, token).ConfigureAwait(false);
            }
            state.Backpack = raid.Entry.Backpack; state.Equipment = raid.Entry.Equipment; state.ActiveRaidId = null; state.ReadOnly = false; state.Revision = checked(state.Revision + 1);
            if (await Execute(c, t, "UPDATE raid_runs SET status=4,revision=@revision WHERE raid_id=@raid AND status=0 AND revision=@previous", token,
                ("@revision", state.Revision), ("@raid", raid.Entry.RaidId), ("@previous", raid.Revision)).ConfigureAwait(false) != 1) throw new InventoryRaidException("raid_transition_conflict");
            await WriteProfile(c, t, state, token).ConfigureAwait(false); return state;
        }

        public Task<RaidSettlementResult> SettleRaidAsync(RaidIdentity identity, string raidId, string operationId, RaidOutcome outcome, string bagId,
            PersistentContainerData backpack, PersistentContainerData equipment, CancellationToken token)
        {
            RequireIdentity(identity); RequireId(raidId); RequireOperation(operationId);
            if (!Enum.IsDefined(typeof(RaidOutcome), outcome) || outcome == RaidOutcome.ServerRecovery) throw new InventoryRaidException("raid_outcome_invalid");
            if (outcome != RaidOutcome.Extracted) RequireId(bagId);
            backpack = PersistentInventoryCodec.Freeze(backpack); equipment = PersistentInventoryCodec.Freeze(equipment);
            if (backpack.Kind != PersonalContainerKind.Backpack || equipment.Kind != PersonalContainerKind.Equipment) throw new InventoryRaidException("inventory_container_kind");
            PersistentInventoryCodec.Items(backpack, equipment);
            byte[] fingerprint = Fingerprint("settle", raidId + ":" + (byte)outcome + ":" + (bagId ?? string.Empty),
                PersistentInventoryCodec.Container(backpack), PersistentInventoryCodec.Container(equipment));
            return Transaction(async (c, t) =>
            {
                PersistentInventoryState state = await ReadProfile(c, t, identity.AccountId, identity.CharacterId, token).ConfigureAwait(false);
                RaidRow raid = await ReadRaid(c, t, raidId, token).ConfigureAwait(false); RequireRaidOwner(raid, identity, true);
                byte[] replay = await Replay(c, t, identity.AccountId, operationId, "settle", fingerprint, token).ConfigureAwait(false);
                if (replay != null) return PersistentInventoryCodec.Settlement(replay);
                if (raid.Settlement != null) return PersistentInventoryCodec.Settlement(raid.Settlement);
                RaidSettlementResult result = await Settle(c, t, state, raid, outcome, bagId, backpack, equipment, token).ConfigureAwait(false);
                await Remember(c, t, identity.AccountId, operationId, "settle", fingerprint, PersistentInventoryCodec.Settlement(result), token).ConfigureAwait(false);
                return result;
            }, token);
        }

        private async Task<RaidSettlementResult> Settle(MySqlConnection c, MySqlTransaction t, PersistentInventoryState state, RaidRow raid, RaidOutcome outcome,
            string bagId, PersistentContainerData backpack, PersistentContainerData equipment, CancellationToken token)
        {
            if (raid.Status != PersistentRaidStatus.Active || state.ActiveRaidId != raid.Entry.RaidId) throw new InventoryRaidException("raid_not_active");
            // Server recovery is a compensating success: the entry snapshot is restored to the account,
            // with fresh ids, and no drop bag is created.
            bool extracted = outcome == RaidOutcome.Extracted || outcome == RaidOutcome.ServerRecovery;
            var current = PersistentInventoryCodec.Items(backpack, equipment);
            foreach (ulong id in current.Keys.OrderBy(id => id))
            {
                Owner owner = await ReadOwner(c, t, id, token).ConfigureAwait(false);
                if (owner == null)
                {
                    RequireNewId(id);
                    await InsertOwner(c, t, id, 1, raid.World, raid.Entry.RaidId, null, token).ConfigureAwait(false);
                }
                else if (owner.Kind != 1 || owner.Scope != raid.World) throw new InventoryRaidException("raid_item_already_claimed");
                await WriteOwner(c, t, id, extracted ? 0 : 1, extracted ? AccountScope(state.AccountId) : raid.World,
                    extracted ? null : raid.Entry.RaidId, extracted ? null : bagId, token).ConfigureAwait(false);
            }
            state.Backpack = extracted ? backpack : PersistentInventoryCodec.Empty(PersonalContainerKind.Backpack);
            state.Equipment = extracted ? equipment : PersistentInventoryCodec.Empty(PersonalContainerKind.Equipment);
            state.ActiveRaidId = extracted || outcome == RaidOutcome.ServerRecovery ? null : raid.Entry.RaidId;
            state.ReadOnly = state.ActiveRaidId != null; state.Revision = checked(state.Revision + 1);
            var result = new RaidSettlementResult { Inventory = state, Status = extracted ? PersistentRaidStatus.Extracted : PersistentRaidStatus.Failed };
            if (!extracted)
            {
                result.DropBag = new PersistentDropBag { RaidId = raid.Entry.RaidId, BagId = bagId, WorldRunId = raid.World, Backpack = backpack, Equipment = equipment };
                await Execute(c, t, "INSERT INTO raid_drop_bags(bag_id,raid_id,world_run_id,published,payload) VALUES(@bag,@raid,@world,0,@payload)", token,
                    ("@bag", bagId), ("@raid", raid.Entry.RaidId), ("@world", raid.World), ("@payload", PersistentInventoryCodec.Bag(result.DropBag))).ConfigureAwait(false);
            }
            if (await Execute(c, t, "UPDATE raid_runs SET status=@status,revision=@revision,settlement_payload=@payload,outcome=@outcome WHERE raid_id=@raid AND status=1 AND revision=@previous", token,
                ("@status", (byte)result.Status), ("@revision", state.Revision), ("@payload", PersistentInventoryCodec.Settlement(result)), ("@outcome", (byte)outcome),
                ("@raid", raid.Entry.RaidId), ("@previous", raid.Revision)).ConfigureAwait(false) != 1) throw new InventoryRaidException("raid_transition_conflict");
            await WriteProfile(c, t, state, token).ConfigureAwait(false); return result;
        }

        public Task<PersistentDropBag[]> GetPendingDropBagsAsync(CancellationToken token) => Transaction(async (c, t) =>
        {
            var result = new List<PersistentDropBag>();
            using (var command = Command(c, t, "SELECT payload FROM raid_drop_bags WHERE world_run_id=@world AND published=0 ORDER BY bag_id LIMIT 128", ("@world", WorldRunId)))
            {
                var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                    while (await reader.ReadAsync(token).ConfigureAwait(false)) result.Add(PersistentInventoryCodec.Bag((byte[])reader.GetValue(0)));
            }
            return result.ToArray();
        }, token);
        public Task MarkDropBagPublishedAsync(string raidId, string bagId, CancellationToken token)
        {
            RequireId(raidId); RequireId(bagId);
            return Transaction(async (c, t) =>
            {
                ulong account, character;
                using (var command = Command(c, t, "SELECT account_id,character_id FROM raid_runs WHERE raid_id=@raid AND world_run_id=@world", ("@raid", raidId), ("@world", WorldRunId)))
                {
                    var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                    await using (reader.ConfigureAwait(false))
                    {
                        if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw new InventoryRaidException("raid_not_found");
                        account = reader.GetUInt64(0); character = reader.GetUInt64(1);
                    }
                }
                PersistentInventoryState state = await ReadProfile(c, t, account, character, token).ConfigureAwait(false);
                RaidRow raid = await ReadRaid(c, t, raidId, token).ConfigureAwait(false);
                if (raid.Status != PersistentRaidStatus.Failed) throw new InventoryRaidException("raid_not_failed");
                object present = await Scalar(c, t, "SELECT published FROM raid_drop_bags WHERE bag_id=@bag AND raid_id=@raid AND world_run_id=@world FOR UPDATE", token,
                    ("@bag", bagId), ("@raid", raidId), ("@world", WorldRunId)).ConfigureAwait(false);
                if (present == null) throw new InventoryRaidException("raid_drop_bag_not_found");
                await Execute(c, t, "UPDATE raid_drop_bags SET published=1 WHERE bag_id=@bag AND raid_id=@raid AND world_run_id=@world", token,
                    ("@bag", bagId), ("@raid", raidId), ("@world", WorldRunId)).ConfigureAwait(false);
                if (state.ActiveRaidId == raidId)
                {
                    state.ActiveRaidId = null; state.ReadOnly = false; state.Revision = checked(state.Revision + 1);
                    await WriteProfile(c, t, state, token).ConfigureAwait(false);
                }
                return true;
            }, token);
        }

        private sealed class RaidRow
        {
            internal string World, Session;
            internal long Revision;
            internal PersistentRaidStatus Status;
            internal RaidReservation Entry;
            internal byte[] Settlement;
        }
        private async Task<RaidRow> ReadRaid(MySqlConnection c, MySqlTransaction t, string raidId, CancellationToken token)
        {
            using (var command = Command(c, t, "SELECT world_run_id,session_id,status,revision,entry_payload,settlement_payload FROM raid_runs WHERE raid_id=@raid FOR UPDATE", ("@raid", raidId)))
            {
                var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw new InventoryRaidException("raid_not_found");
                    return new RaidRow { World = reader.GetString(0), Session = reader.GetString(1), Status = (PersistentRaidStatus)reader.GetByte(2), Revision = reader.GetInt64(3),
                        Entry = PersistentInventoryCodec.Reservation((byte[])reader.GetValue(4)), Settlement = reader.IsDBNull(5) ? null : (byte[])reader.GetValue(5) };
                }
            }
        }
        private async Task<PersistentInventoryState> ReadProfile(MySqlConnection c, MySqlTransaction t, ulong account, ulong character, CancellationToken token, bool required = true)
        {
            using (var command = Command(c, t, "SELECT character_id,revision,active_raid_id,stash_payload,backpack_payload,equipment_payload FROM inventory_profiles WHERE account_id=@account FOR UPDATE", ("@account", account)))
            {
                var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    if (!await reader.ReadAsync(token).ConfigureAwait(false))
                    { if (required) throw new InventoryRaidException("inventory_not_initialized"); return null; }
                    if (reader.GetUInt64(0) != character) throw new InventoryRaidException("inventory_identity_invalid");
                    string active = reader.IsDBNull(2) ? null : reader.GetString(2);
                    return new PersistentInventoryState
                    {
                        AccountId = account, CharacterId = character, Revision = reader.GetInt64(1), ActiveRaidId = active, ReadOnly = active != null,
                        Stash = PersistentInventoryCodec.Container((byte[])reader.GetValue(3)), Backpack = PersistentInventoryCodec.Container((byte[])reader.GetValue(4)),
                        Equipment = PersistentInventoryCodec.Container((byte[])reader.GetValue(5))
                    };
                }
            }
        }
        private async Task WriteProfile(MySqlConnection c, MySqlTransaction t, PersistentInventoryState state, CancellationToken token)
        {
            PersistentInventoryCodec.State(state); // Durable success must remain encodable by RaidInventoryWireCodec.
            if (await Execute(c, t, "UPDATE inventory_profiles SET revision=@revision,active_raid_id=@raid,stash_payload=@stash,backpack_payload=@backpack,equipment_payload=@equipment WHERE account_id=@account AND character_id=@character", token,
                ("@revision", state.Revision), ("@raid", state.ActiveRaidId), ("@stash", PersistentInventoryCodec.Container(state.Stash)), ("@backpack", PersistentInventoryCodec.Container(state.Backpack)),
                ("@equipment", PersistentInventoryCodec.Container(state.Equipment)), ("@account", state.AccountId), ("@character", state.CharacterId)).ConfigureAwait(false) != 1)
                throw new InventoryRaidException("inventory_write_conflict");
        }
        private sealed class Owner { internal int Kind; internal string Scope, Raid; }
        private async Task<Owner> ReadOwner(MySqlConnection c, MySqlTransaction t, ulong id, CancellationToken token)
        {
            using (var command = Command(c, t, "SELECT owner_kind,owner_scope,raid_id FROM item_ownership WHERE item_id=@id FOR UPDATE", ("@id", id)))
            {
                var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
                    return new Owner { Kind = reader.GetByte(0), Scope = reader.GetString(1), Raid = reader.IsDBNull(2) ? null : reader.GetString(2) };
                }
            }
        }
        private Task<int> WriteOwner(MySqlConnection c, MySqlTransaction t, ulong id, int kind, string scope, string raid, string bag, CancellationToken token) =>
            Execute(c, t, "UPDATE item_ownership SET owner_kind=@kind,owner_scope=@scope,raid_id=@raid,bag_id=@bag WHERE item_id=@id", token,
                ("@kind", kind), ("@scope", scope), ("@raid", raid), ("@bag", bag), ("@id", id));
        private Task<int> InsertOwner(MySqlConnection c, MySqlTransaction t, ulong id, int kind, string scope, string raid, string bag, CancellationToken token) =>
            Execute(c, t, "INSERT INTO item_ownership(item_id,owner_kind,owner_scope,raid_id,bag_id) VALUES(@id,@kind,@scope,@raid,@bag)", token,
                ("@id", id), ("@kind", kind), ("@scope", scope), ("@raid", raid), ("@bag", bag));
        private async Task<byte[]> Replay(MySqlConnection c, MySqlTransaction t, ulong account, string operation, string kind, byte[] fingerprint, CancellationToken token)
        {
            using (var command = Command(c, t, "SELECT operation_kind,fingerprint,response_payload FROM inventory_operations WHERE account_id=@account AND operation_id=@operation", ("@account", account), ("@operation", operation)))
            {
                var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
                    if (reader.GetString(0) != kind || !((byte[])reader.GetValue(1)).SequenceEqual(fingerprint)) throw new InventoryRaidException("inventory_operation_id_reused");
                    return (byte[])reader.GetValue(2);
                }
            }
        }
        private Task<int> Remember(MySqlConnection c, MySqlTransaction t, ulong account, string operation, string kind, byte[] fingerprint, byte[] response, CancellationToken token) =>
            Execute(c, t, "INSERT INTO inventory_operations(account_id,operation_id,operation_kind,fingerprint,response_payload) VALUES(@account,@operation,@kind,@fingerprint,@response)", token,
                ("@account", account), ("@operation", operation), ("@kind", kind), ("@fingerprint", fingerprint), ("@response", response));
        private static byte[] Fingerprint(string kind, string key, params byte[][] payloads)
        {
            using (var stream = new System.IO.MemoryStream())
            using (var writer = new System.IO.BinaryWriter(stream))
            using (var sha = SHA256.Create())
            {
                writer.Write(kind); writer.Write(key);
                foreach (byte[] payload in payloads) { writer.Write(payload.Length); writer.Write(payload); }
                writer.Flush(); return sha.ComputeHash(stream.ToArray());
            }
        }
        private static string AccountScope(ulong account) => account.ToString(CultureInfo.InvariantCulture);
        private static void RequireIdentity(RaidIdentity identity)
        { if (identity == null || identity.AccountId == 0 || identity.CharacterId == 0) throw new InventoryRaidException("inventory_identity_invalid"); }
        private static void RequireId(string id)
        { if (!IsLowerHex(id, 32)) throw new InventoryRaidException("raid_identifier_invalid"); }
        private static void RequireOperation(string operation)
        { if (!IsAscii(operation, 64) || string.IsNullOrEmpty(operation)) throw new InventoryRaidException("inventory_operation_invalid"); }
        private static bool IsLowerHex(string value, int length)
        {
            if (value == null || value.Length != length) return false;
            foreach (char c in value)
                if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) return false;
            return true;
        }
        private static bool IsAscii(string value, int maxBytes)
        {
            if (value == null || value.Length == 0 || value.Length > maxBytes) return false;
            foreach (char c in value) if (c > 127) return false;
            return true;
        }
        private static void RequireWritable(PersistentInventoryState state, long revision)
        {
            if (state.ActiveRaidId != null) throw new InventoryRaidException("inventory_raid_in_progress");
            if (state.Revision != revision) throw new InventoryRaidException("inventory_revision_conflict");
        }
        private static void RequireKinds(PersistentContainerData stash, PersistentContainerData backpack, PersistentContainerData equipment)
        { if (stash.Kind != PersonalContainerKind.Stash || backpack.Kind != PersonalContainerKind.Backpack || equipment.Kind != PersonalContainerKind.Equipment) throw new InventoryRaidException("inventory_container_kind"); }
        private void RequireNewId(ulong id)
        { if (range.First == 0 || id < range.First || id > range.Last) throw new InventoryRaidException("inventory_item_id_outside_range"); }
        private ulong AllocateRecoveryId()
        {
            if (recoveryNextId == 0 || recoveryNextId > range.Last) throw new InventoryRaidException("inventory_item_ids_exhausted");
            return recoveryNextId++;
        }
        private static void RequireRaidOwner(RaidRow raid, RaidIdentity identity, bool session)
        {
            if (raid.Entry.AccountId != identity.AccountId || raid.Entry.CharacterId != identity.CharacterId || session && raid.Session != identity.SessionId)
                throw new InventoryRaidException("raid_session_mismatch");
        }

        private async Task RecoverPreviousWorldsAsync(CancellationToken token)
        {
            var previous = new List<string>();
            await Transaction(async (c, t) =>
            {
                using (var command = Command(c, t, "SELECT world_run_id FROM world_runs WHERE server_id=@server AND status=0 AND world_run_id<>@current", ("@server", serverId), ("@current", WorldRunId)))
                {
                    var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                    await using (reader.ConfigureAwait(false)) while (await reader.ReadAsync(token).ConfigureAwait(false)) previous.Add(reader.GetString(0));
                }
                return true;
            }, token).ConfigureAwait(false);
            foreach (string world in previous)
            {
                var raids = new List<(string raid, ulong account, ulong character)>();
                await Transaction(async (c, t) =>
                {
                    using (var command = Command(c, t, "SELECT raid_id,account_id,character_id FROM raid_runs WHERE world_run_id=@world AND status IN (0,1,3) ORDER BY account_id", ("@world", world)))
                    {
                        var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                        await using (reader.ConfigureAwait(false)) while (await reader.ReadAsync(token).ConfigureAwait(false)) raids.Add((reader.GetString(0), reader.GetUInt64(1), reader.GetUInt64(2)));
                    }
                    return true;
                }, token).ConfigureAwait(false);
                foreach (var entry in raids)
                    await Transaction(async (c, t) =>
                    {
                        PersistentInventoryState state = await ReadProfile(c, t, entry.account, entry.character, token).ConfigureAwait(false);
                        RaidRow raid = await ReadRaid(c, t, entry.raid, token).ConfigureAwait(false);
                        if (raid.Status == PersistentRaidStatus.Prepared) await CancelPrepared(c, t, state, raid, token).ConfigureAwait(false);
                        else if (raid.Status == PersistentRaidStatus.Active && raid.Settlement == null)
                        {
                            // A crash lost the actual world state. Quarantine old ids, then compensate the complete
                            // entry snapshot with fresh ids. A committed settlement payload is never overwritten.
                            await Execute(c, t, "UPDATE item_ownership SET owner_kind=2,bag_id=NULL WHERE owner_kind=1 AND owner_scope=@world AND raid_id=@raid", token,
                                ("@world", world), ("@raid", entry.raid)).ConfigureAwait(false);
                            PersistentContainerData backpack = PersistentInventoryCodec.CloneWithNewIds(raid.Entry.Backpack, AllocateRecoveryId);
                            PersistentContainerData equipment = PersistentInventoryCodec.CloneWithNewIds(raid.Entry.Equipment, AllocateRecoveryId);
                            await Settle(c, t, state, raid, RaidOutcome.ServerRecovery, Guid.NewGuid().ToString("N"),
                                backpack, equipment, token).ConfigureAwait(false);
                        }
                        else if (raid.Status == PersistentRaidStatus.Failed && state.ActiveRaidId == entry.raid)
                        {
                            state.ActiveRaidId = null; state.ReadOnly = false; state.Revision = checked(state.Revision + 1);
                            await WriteProfile(c, t, state, token).ConfigureAwait(false);
                        }
                        return true;
                    }, token).ConfigureAwait(false);
                await Transaction(async (c, t) =>
                {
                    await Execute(c, t, "UPDATE item_ownership SET owner_kind=2,bag_id=NULL WHERE owner_kind=1 AND owner_scope=@world", token, ("@world", world)).ConfigureAwait(false);
                    await Execute(c, t, "UPDATE world_runs SET status=1 WHERE world_run_id=@world AND server_id=@server", token, ("@world", world), ("@server", serverId)).ConfigureAwait(false);
                    return true;
                }, token).ConfigureAwait(false);
            }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            worldLease?.Dispose(); worldLease = null;
        }
    }
}
