using System;
using System.Collections.Generic;
using Mirror;
using UnityEngine;
using RPG4.Backpack.Core;
using RPG4.Backpack.Inventory;
using RPG4.Backpack.Items;

namespace RPG4.Backpack.Network
{
    public sealed partial class BackpackNetworkInventory
    {
        private readonly Dictionary<int, AssemblyWireBuffer> assemblyReceives = new Dictionary<int, AssemblyWireBuffer>();
        private readonly Dictionary<int, AssemblyQuery> assemblyQueries = new Dictionary<int, AssemblyQuery>();
        private readonly Dictionary<int, AssemblyOperation> assemblyOperations = new Dictionary<int, AssemblyOperation>();
        private readonly Dictionary<int, bool> assemblyQueryEquipmentSources = new Dictionary<int, bool>();
        private readonly Dictionary<int, bool> assemblyOperationEquipmentSources = new Dictionary<int, bool>();
        private static AssemblyOperationResult AssemblyFailure(AssemblyOperation operation, string reason)
            => new AssemblyOperationResult(operation.RequestId, operation.SessionId, operation.RootItemId,
                operation.ParentItemId, operation.SlotId, false, reason);
        private static AssemblyInspectionSnapshot AssemblyFailure(AssemblyQuery query, string reason)
            => new AssemblyInspectionSnapshot(null, null, null, null, null, reason, -1,
                query.SessionId, query.RequestId, query.RootItemId, query.ParentItemId, query.SlotId,
                Math.Max(0, query.CandidateOffset));
        private static bool ValidAssemblyQuery(AssemblyQuery query) => query.RootItemId.IsValid
            && (query.SlotId == null || query.SlotId.Length <= AssemblyWireCodec.MaxStringLength)
            && query.CandidateOffset >= 0 && query.CandidateLimit >= 1 && query.CandidateLimit <= AssemblyWireCodec.MaxCandidates;
        private static bool ValidAssemblyOperation(AssemblyOperation operation) => operation.RootItemId.IsValid
            && operation.ParentItemId.IsValid && ItemAssemblySlots.IsValid(operation.SlotId)
            && operation.ExpectedStructureRevision >= -1;
        internal void SubmitEquipmentAssemblyQuery(AssemblyQuery query)
        { if (isClient) RequestAssemblyQuery(query, true); else DispatchAuthorityAssemblyQuery(query, true); }
        internal void SubmitEquipmentAssemblyOperation(AssemblyOperation operation)
        { if (isClient) RequestAssemblyOperation(operation, true); else DispatchAuthorityAssemblyOperation(operation, true); }
        private void PublishNetworkAssemblyInspection(AssemblyInspectionSnapshot value, bool equipmentSource = false)
        {
            if (equipmentSource) GetComponent<BackpackNetworkEquipment>()?.PublishAssemblyInspection(value);
            else core?.PublishAssemblyInspection(value);
        }
        private void PublishNetworkAssemblyResult(AssemblyOperationResult value, bool equipmentSource = false)
        {
            if (equipmentSource) GetComponent<BackpackNetworkEquipment>()?.PublishAssemblyResult(value);
            else core?.PublishAssemblyOperationResult(value);
        }
        private void ConfigureNetworkAssembly()
        {
            core.ForwardAssemblyQuery = query => RequestAssemblyQuery(query, false);
            core.ForwardAssemblyOperation = operation => RequestAssemblyOperation(operation, false);
            if (IsServerInventory) {
                core.AuthorityAssemblyQueryDispatcher = query => DispatchAuthorityAssemblyQuery(query, false);
                core.AuthorityAssemblyOperationDispatcher = operation => DispatchAuthorityAssemblyOperation(operation, false);
            }
        }
        private void ClearNetworkAssembly()
        {
            assemblyQueries.Clear(); assemblyOperations.Clear(); assemblyReceives.Clear();
            assemblyQueryEquipmentSources.Clear(); assemblyOperationEquipmentSources.Clear();
        }
        private void RequestAssemblyQuery(AssemblyQuery query, bool equipmentSource = false)
        {
            if (query.ParentItemId.IsNone && query.RootItemId.IsValid)
                query = new AssemblyQuery(query.RootItemId, query.RootItemId, query.SlotId, query.SessionId,
                    query.RequestId, query.CandidateOffset, query.CandidateLimit);
            assemblyReceives.Remove(query.SessionId);
            if (query.RootItemId.IsNone) {
                assemblyQueries.Remove(query.SessionId); assemblyOperations.Remove(query.SessionId);
                assemblyQueryEquipmentSources.Remove(query.SessionId); assemblyOperationEquipmentSources.Remove(query.SessionId); return;
            }
            if (!assemblyQueries.ContainsKey(query.SessionId) && assemblyQueries.Count >= 32) {
                PublishNetworkAssemblyInspection(AssemblyFailure(query, "装配查询数已达上限，请关闭其他检查后重试。"), equipmentSource); return;
            }
            if (assemblyQueries.TryGetValue(query.SessionId, out AssemblyQuery previous) && (previous.RootItemId != query.RootItemId
                || previous.ParentItemId != query.ParentItemId || previous.SlotId != query.SlotId)) {
                assemblyOperations.Remove(query.SessionId); assemblyOperationEquipmentSources.Remove(query.SessionId);
            }
            assemblyQueries[query.SessionId] = query;
            assemblyQueryEquipmentSources[query.SessionId] = equipmentSource;
            if (!isClient || !isOwned || !IsReady || !ValidAssemblyQuery(query)) {
                PublishNetworkAssemblyInspection(AssemblyFailure(query, "当前客户端不能查询此装配。"), equipmentSource); return;
            }
            CmdQueryAssembly(NextClientTicket(), query.RequestId, query.SessionId, query.RootItemId.Value,
                query.ParentItemId.Value, query.SlotId, query.CandidateOffset, query.CandidateLimit);
        }
        private void RequestAssemblyOperation(AssemblyOperation operation, bool equipmentSource = false)
        {
            if (!assemblyOperations.ContainsKey(operation.SessionId) && assemblyOperations.Count >= 32) {
                PublishNetworkAssemblyResult(AssemblyFailure(operation, "装配操作队列已满，请重试。"), equipmentSource); return;
            }
            assemblyOperations[operation.SessionId] = operation;
            assemblyOperationEquipmentSources[operation.SessionId] = equipmentSource;
            if (!isClient || !isOwned || !IsReady || !ValidAssemblyOperation(operation)) {
                PublishNetworkAssemblyResult(AssemblyFailure(operation, "当前客户端不能提交此装配。"), equipmentSource); return;
            }
            CmdExecuteAssembly(NextClientTicket(), operation.RequestId, operation.SessionId, operation.RootItemId.Value,
                operation.ParentItemId.Value, operation.SlotId, operation.CandidateItemId.Value, operation.ExpectedStructureRevision);
        }
        private void BeforeAssembly(AssemblyOperation operation)
        {
            // Existing weapon cancellation resolves the root, including nested rail/optic operations.
            ServerBeforeWeaponOperation?.Invoke(new WeaponOperation(operation.RequestId,
                WeaponOperationKind.ExchangeAttachment, operation.RootItemId, operation.CandidateItemId));
        }
        [Command(requiresAuthority = true)]
        private void CmdQueryAssembly(ulong ticket, int requestId, int sessionId, ulong rootId, ulong parentId, string slotId, int offset, int limit)
        {
            NetworkConnectionToClient conn = connectionToClient;
            var query = new AssemblyQuery(new ItemId(rootId), new ItemId(parentId), slotId, sessionId, requestId, offset, limit);
            void Send(AssemblyInspectionSnapshot snapshot) {
                AssemblyQuery responseQuery = query.SlotId != null && query.SlotId.Length > AssemblyWireCodec.MaxStringLength
                    ? new AssemblyQuery(query.RootItemId, query.ParentItemId, null, query.SessionId, query.RequestId,
                        Math.Max(0, query.CandidateOffset), Math.Max(1, Math.Min(AssemblyWireCodec.MaxCandidates, query.CandidateLimit)))
                    : query;
                byte[] payload;
                try { payload = AssemblyWireCodec.Encode(snapshot); }
                catch (Exception) { payload = AssemblyWireCodec.Encode(AssemblyFailure(responseQuery, "装配信息超过网络显示上限。")); }
                int count = (payload.Length + AssemblyWireCodec.ChunkBytes - 1) / AssemblyWireCodec.ChunkBytes;
                for (int i = 0; i < count; i++) {
                    int length = Math.Min(AssemblyWireCodec.ChunkBytes, payload.Length - i * AssemblyWireCodec.ChunkBytes);
                    var chunk = new byte[length]; Buffer.BlockCopy(payload, i * AssemblyWireCodec.ChunkBytes, chunk, 0, length);
                    TargetAssemblyInspection(conn, responseQuery.SessionId, responseQuery.RequestId, responseQuery.RootItemId.Value,
                        responseQuery.ParentItemId.Value, responseQuery.SlotId, responseQuery.CandidateOffset, i, count, chunk);
                }
            }
            if (!BeginTicket(ticket, requestId, RequestCategory.AssemblyQuery, () => Send(AssemblyFailure(query, "请求已过期或队列已满。")))) return;
            ScheduleResultOperation(() => !IsAuthenticatedIngress(conn) || !ValidAssemblyQuery(query)
                ? AssemblyFailure(query, "库存访问授权或装配查询已失效。") : core.QueryAssembly(query), result => {
                    if (connectionToClient == conn && operationOwner == conn) CompleteTicket(ticket, () => Send(result));
                }, reason => AssemblyFailure(query, reason));
        }
        [Command(requiresAuthority = true)]
        private void CmdExecuteAssembly(ulong ticket, int requestId, int sessionId, ulong rootId, ulong parentId, string slotId, ulong candidateId, int revision)
        {
            NetworkConnectionToClient conn = connectionToClient;
            var operation = new AssemblyOperation(requestId, new ItemId(rootId), new ItemId(parentId), slotId, new ItemId(candidateId), revision, sessionId);
            void Send(AssemblyOperationResult result)
            {
                byte[] payload;
                try { payload = AssemblyWireCodec.Encode(result); }
                catch (Exception) { payload = AssemblyWireCodec.Encode(new AssemblyOperationResult(operation.RequestId,
                    operation.SessionId, operation.RootItemId, operation.ParentItemId,
                    operation.SlotId != null && operation.SlotId.Length > AssemblyWireCodec.MaxStringLength ? null : operation.SlotId, false,
                    "装配回执超过网络显示上限。")); }
                TargetAssemblyCompleted(conn, payload);
            }
            if (!BeginTicket(ticket, requestId, RequestCategory.Assembly, () => Send(AssemblyFailure(operation, "请求已过期或队列已满。")))) return;
            ScheduleResultOperation(() => {
                if (!IsAuthenticatedIngress(conn) || !ValidAssemblyOperation(operation)) return AssemblyFailure(operation, "库存访问授权或装配命令已失效。");
                BeforeAssembly(operation);
                return core.ExecuteAssembly(operation);
            }, result => { if (connectionToClient == conn && operationOwner == conn) CompleteTicket(ticket, () => Send(result)); },
                reason => AssemblyFailure(operation, reason));
        }
        [TargetRpc]
        private void TargetAssemblyInspection(NetworkConnectionToClient target, int sessionId, int requestId, ulong rootId,
            ulong parentId, string slotId, int candidateOffset, int index, int count, byte[] chunk)
        {
            if (!assemblyQueries.TryGetValue(sessionId, out AssemblyQuery query)
                || query.RequestId != requestId || query.RootItemId.Value != rootId || query.ParentItemId.Value != parentId
                || query.SlotId != slotId || query.CandidateOffset != candidateOffset) return;
            try {
                if (!assemblyReceives.TryGetValue(sessionId, out AssemblyWireBuffer buffer)) {
                    buffer = new AssemblyWireBuffer(count, Time.unscaledTimeAsDouble); assemblyReceives[sessionId] = buffer;
                }
                if (!buffer.Add(index, count, chunk, Time.unscaledTimeAsDouble, out byte[] payload)) return;
                assemblyReceives.Remove(sessionId);
                AssemblyInspectionSnapshot value = AssemblyWireCodec.DecodeInspection(payload, ResolveAssemblyUiItem);
                if (query.RequestId != value.RequestId || query.SessionId != value.SessionId || query.RootItemId != value.RootItemId
                    || query.ParentItemId != value.ParentItemId || query.SlotId != value.SlotId || query.CandidateOffset != value.CandidateOffset) return;
                assemblyQueryEquipmentSources.TryGetValue(sessionId, out bool equipmentSource);
                PublishNetworkAssemblyInspection(value, equipmentSource);
            }
            catch (Exception) { assemblyReceives.Remove(sessionId); }
        }
        [TargetRpc]
        private void TargetAssemblyCompleted(NetworkConnectionToClient target, byte[] payload)
        {
            AssemblyOperationResult value;
            try { value = AssemblyWireCodec.DecodeResult(payload); }
            catch (Exception) { return; }
            if (!assemblyOperations.TryGetValue(value.SessionId, out AssemblyOperation operation)
                || operation.RequestId != value.RequestId || operation.RootItemId != value.RootItemId
                || operation.ParentItemId != value.ParentItemId || operation.SlotId != value.SlotId) return;
            assemblyOperations.Remove(value.SessionId);
            assemblyOperationEquipmentSources.TryGetValue(value.SessionId, out bool equipmentSource);
            assemblyOperationEquipmentSources.Remove(value.SessionId);
            PublishNetworkAssemblyResult(value, equipmentSource);
        }
        private WeaponUiItem ResolveAssemblyUiItem(ItemId id)
        {
            CopyItems(networkWeaponUiItems);
            RuntimeItem equipped = NetworkEquippedItem;
            if (equipped != null && ItemAssemblyTraversal.TryFind(equipped, id, out RuntimeItem item)) return WeaponUiSnapshots.Describe(item, catalog);
            foreach (RuntimeItem root in networkWeaponUiItems)
                if (ItemAssemblyTraversal.TryFind(root, id, out item)) return WeaponUiSnapshots.Describe(item, catalog);
            return null;
        }
        private void DispatchAuthorityAssemblyQuery(AssemblyQuery query, bool equipmentSource = false)
        {
            if (isClient) { RequestAssemblyQuery(query, equipmentSource); return; }
            ScheduleResultOperation(() => core.QueryAssembly(query), value => PublishNetworkAssemblyInspection(value, equipmentSource),
                reason => AssemblyFailure(query, reason));
        }
        private void DispatchAuthorityAssemblyOperation(AssemblyOperation operation, bool equipmentSource = false)
        {
            if (isClient) { RequestAssemblyOperation(operation, equipmentSource); return; }
            ScheduleResultOperation(() => { BeforeAssembly(operation); return core.ExecuteAssembly(operation); },
                value => PublishNetworkAssemblyResult(value, equipmentSource), reason => AssemblyFailure(operation, reason));
        }
    }
}
