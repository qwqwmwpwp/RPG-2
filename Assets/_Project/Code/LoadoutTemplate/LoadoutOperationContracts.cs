using System;
using System.Collections.Generic;

namespace RPG4.LoadoutTemplate
{
    public enum LoadoutResultCode
    {
        Unknown = 0, Success = 1, InvalidRequest = 2, SessionExpired = 3,
        ConfigurationMismatch = 4, AccessDenied = 5, RevisionConflict = 6,
        ItemUnavailable = 7, ItemReserved = 8, CapacityExceeded = 9, RuleRejected = 10,
        DuplicateRequestConflict = 11, RequestExpired = 12, SnapshotInvalid = 13,
        NotReady = 14, Timeout = 15, InternalError = 16
    }

    public readonly struct LoadoutFailure
    {
        public LoadoutFailure(LoadoutResultCode code, string reason)
        { Code = code; Reason = reason ?? string.Empty; }
        public LoadoutResultCode Code { get; }
        public string Reason { get; }
    }

    public enum LoadoutOperationChannel { Inventory, Loot, Runtime, Local, System }

    public readonly struct LoadoutOperationKey
    {
        public LoadoutOperationKey(uint actorNetId, string epoch, string session,
            LoadoutOperationChannel channel, int requestId)
        {
            ActorNetId = actorNetId; Epoch = epoch ?? string.Empty; Session = session ?? string.Empty;
            Channel = channel; RequestId = requestId;
        }
        public uint ActorNetId { get; }
        public string Epoch { get; }
        public string Session { get; }
        public LoadoutOperationChannel Channel { get; }
        public int RequestId { get; }
    }

    public readonly struct LoadoutStateVersion
    {
        public LoadoutStateVersion(string epoch, long roleRevision, string sourceId, int sourceRevision)
        {
            Epoch = epoch ?? string.Empty; RoleRevision = roleRevision;
            SourceId = sourceId ?? string.Empty; SourceRevision = sourceRevision;
        }
        public string Epoch { get; }
        public long RoleRevision { get; }
        public string SourceId { get; }
        public int SourceRevision { get; }
    }

    public sealed class LoadoutEndpointChange
    {
        public LoadoutEndpointChange(string endpointId, int beforeRevision, int afterRevision,
            IEnumerable<string> itemIds)
        {
            EndpointId = endpointId ?? string.Empty; BeforeRevision = beforeRevision; AfterRevision = afterRevision;
            ItemIds = Array.AsReadOnly(itemIds == null ? Array.Empty<string>() : new List<string>(itemIds).ToArray());
        }
        public string EndpointId { get; }
        public int BeforeRevision { get; }
        public int AfterRevision { get; }
        public IReadOnlyList<string> ItemIds { get; }
    }

    public sealed class LoadoutCommitRecord
    {
        public LoadoutCommitRecord(LoadoutOperationKey key, LoadoutStateVersion before, LoadoutStateVersion after,
            IEnumerable<LoadoutEndpointChange> changes, bool runtimeItemsChanged = false, bool selectedWeaponChanged = false,
            bool detailsComplete = true)
        {
            Key = key; Before = before; After = after;
            var copied = new List<LoadoutEndpointChange>();
            if (changes != null)
                foreach (LoadoutEndpointChange change in changes)
                {
                    if (change == null) throw new ArgumentException("提交记录不能包含空端点变化。", nameof(changes));
                    copied.Add(new LoadoutEndpointChange(change.EndpointId, change.BeforeRevision, change.AfterRevision, change.ItemIds));
                }
            Changes = copied.AsReadOnly();
            RuntimeItemsChanged = runtimeItemsChanged; SelectedWeaponChanged = selectedWeaponChanged;
            DetailsComplete = detailsComplete;
        }
        public LoadoutOperationKey Key { get; }
        public LoadoutStateVersion Before { get; }
        public LoadoutStateVersion After { get; }
        public IReadOnlyList<LoadoutEndpointChange> Changes { get; }
        public bool RuntimeItemsChanged { get; }
        public bool SelectedWeaponChanged { get; }
        /// <summary>为 false 时 Changes 是提交摘要，完整状态由同批权威快照提供。</summary>
        public bool DetailsComplete { get; }
    }

    public sealed class LoadoutOperationOutcome
    {
        public LoadoutOperationOutcome(LoadoutOperationKey key, LoadoutResultCode code, string message,
            LoadoutCommitRecord commit, LoadoutStateVersion current)
        { Key = key; Code = code; Message = message ?? string.Empty; Commit = commit; Current = current; }
        public LoadoutOperationKey Key { get; }
        public LoadoutResultCode Code { get; }
        public string Message { get; }
        public LoadoutCommitRecord Commit { get; }
        public LoadoutStateVersion Current { get; }
        public bool Success => Code == LoadoutResultCode.Success;
    }
}
