using System;
using System.Collections.Generic;
using Mirror;
using RPG4.Backpack.Core;
using UnityEngine;

namespace RPG4.LoadoutTemplate
{
    /// <summary>一个真实地面物品。聚合窗口只投影它，不创建第二份所有权。</summary>
    [DisallowMultipleComponent, RequireComponent(typeof(NetworkLootSource))]
    public sealed class NetworkGroundLootItem : NetworkBehaviour
    {
        [SerializeField] private LoadoutItemDefinition initialDefinition;
        [SerializeField, Min(1)] private int initialCount = 1;
        [SerializeField, Min(.1f)] private float collectionRadius = 3f;
        [SerializeField] private GameObject worldVisual;
        [SyncVar] private bool present;
        internal static readonly HashSet<NetworkGroundLootItem> Items = new HashSet<NetworkGroundLootItem>();
        internal ItemDisplayData Item { get; private set; }
        internal bool IsPresent => present && Item != null && isServer;
        internal float Radius => float.IsNaN(collectionRadius) || float.IsInfinity(collectionRadius) ? 3f : Mathf.Clamp(collectionRadius, .1f, 50f);
        private Renderer[] renderers;
        private Collider[] colliders;
        private bool[] rendererEnabled, colliderEnabled;
        private bool visualPresent = true;
        private void Awake()
        {
            GameObject visual = worldVisual != null && worldVisual != gameObject ? worldVisual : gameObject;
            renderers = visual.GetComponentsInChildren<Renderer>(true); colliders = GetComponentsInChildren<Collider>(true);
            rendererEnabled = new bool[renderers.Length]; colliderEnabled = new bool[colliders.Length];
            for (int i = 0; i < renderers.Length; i++) rendererEnabled[i] = renderers[i].enabled;
            for (int i = 0; i < colliders.Length; i++) colliderEnabled[i] = colliders[i].enabled;
        }
        public override void OnStartServer()
        {
            base.OnStartServer();
            if (Item != null)
            {
                if (present) Items.Add(this);
                GetComponent<NetworkLootSource>().RefreshGround(); return;
            }
            if (initialDefinition == null || initialDefinition.ContainerType != ContainerKind.None || initialCount < 1)
            { Debug.LogError("地面物品需要普通定义；带内容容器请由服务器显式 InitializeServer。", this); return; }
            if (!InitializeServer(initialDefinition.Create("ground@" + Guid.NewGuid().ToString("N"), Vector2Int.zero, initialCount), out string error))
                Debug.LogError(error, this);
        }
        public bool InitializeServer(ItemDisplayData item, out string error)
        {
            error = null;
            if (!NetworkServer.active || Item != null || item == null || string.IsNullOrWhiteSpace(item.Id) || item.Id.Length > 160
                || item.Count < 1 || item.OriginalSize.x < 1 || item.OriginalSize.y < 1 || item.OriginalSize.x > 64 || item.OriginalSize.y > 64
                || item.IsContainer != (item.Storage != null) || item.IsContainer && (item.Count != 1 || item.Storage.Kind != item.ContainerType))
            { error = "地面实例、尺寸或容器储物关联无效。"; return false; }
            var identities = new HashSet<string>(StringComparer.Ordinal) { item.Id };
            if (item.Storage != null)
            {
                if (!identities.Add(item.Storage.Id)) { error = "地面容器身份重复。"; return false; }
                foreach (ItemDisplayData stored in item.Storage.Items)
                    if (stored.IsContainer || !identities.Add(stored.Id)) { error = "地面容器禁止嵌套或重复物品。"; return false; }
            }
            foreach (NetworkGroundLootItem existing in Items)
            {
                // 初始化可以发生在 Spawn 之前；已保留的身份此时同样不能重复使用。
                if (existing == null || existing.Item == null || !existing.present) continue;
                if (identities.Contains(existing.Item.Id) || existing.Item.Storage != null && identities.Contains(existing.Item.Storage.Id))
                { error = "地面物品身份已被占用。"; return false; }
                if (existing.Item.Storage != null) foreach (ItemDisplayData stored in existing.Item.Storage.Items)
                    if (identities.Contains(stored.Id)) { error = "地面物品身份已被占用。"; return false; }
            }
            NetworkLootSource anchor = GetComponent<NetworkLootSource>();
            if (!anchor.InitializeGroundServer(this, out error)) return false;
            Item = item.At(Vector2Int.zero); present = true; Items.Add(this); anchor.RefreshGround(); return true;
        }
        internal void Take() { present = false; GetComponent<NetworkLootSource>().RefreshGround(); }
        private void LateUpdate()
        {
            if (visualPresent == present) return;
            visualPresent = present;
            for (int i = 0; i < renderers.Length; i++) if (renderers[i] != null) renderers[i].enabled = present && rendererEnabled[i];
            for (int i = 0; i < colliders.Length; i++) if (colliders[i] != null) colliders[i].enabled = present && colliderEnabled[i];
        }
        public override void OnStopServer()
        {
            Items.Remove(this); present = false;
            GroundLootSession.RemoveDestroyed(this);
            GetComponent<NetworkLootSource>()?.RefreshGround(); base.OnStopServer();
        }
    }

    /// <summary>打开瞬间固定中心、成员及格子。其他会话只同步移除，绝不重排剩余物品。</summary>
    internal sealed class GroundLootSession : IDisposable
    {
        private static readonly HashSet<GroundLootSession> sessions = new HashSet<GroundLootSession>();
        private static readonly HashSet<LootSource> pending = new HashSet<LootSource>();
        private readonly Dictionary<string, NetworkGroundLootItem> members = new Dictionary<string, NetworkGroundLootItem>(StringComparer.Ordinal);
        private readonly Vector3 center;
        private readonly int scene;
        private readonly float distance;
        private BackpackContainerData data;
        internal readonly LootSource Source = new LootSource();
        private GroundEndpoint endpoint;
        private bool disposed;
        private GroundLootSession(NetworkLootSource anchor)
        { center = anchor.transform.position; scene = anchor.gameObject.scene.handle; distance = anchor.InteractionDistance; Source.GroundSession = this; }
        internal bool CanAccess(Transform actor) => !disposed && Source.IsActive && actor != null && actor.gameObject.scene.handle == scene
            && (actor.position - center).sqrMagnitude <= distance * distance;
        internal static bool TryCreate(NetworkLootSource anchor, NetworkGroundLootItem selected, out GroundLootSession result, out string error)
        {
            result = null; error = null;
            var session = new GroundLootSession(anchor); var items = new List<ItemDisplayData>();
            foreach (NetworkGroundLootItem item in NetworkGroundLootItem.Items)
            {
                if (item == null || !item.IsPresent || item.gameObject.scene.handle != session.scene
                    || (item.transform.position - session.center).sqrMagnitude > selected.Radius * selected.Radius) continue;
                if (items.Count >= 256) { error = "地面范围超过 256 件物品，未创建会话。"; return false; }
                session.members.Add(item.Item.Id, item); items.Add(item.Item);
            }
            if (!EquipmentRectanglePacking.TryPack(items, out Vector2Int size, out List<ItemDisplayData> packed, out error)) return false;
            var cells = new List<Vector2Int>();
            for (int y = 0; y < size.y; y++) for (int x = 0; x < size.x; x++) cells.Add(new Vector2Int(x, y));
            if (!BackpackContainerData.TryCreate(session.Source.Id + "/ground", ContainerKind.None, null, cells, packed,
                out session.data, out error, rejectContainers: false)) return false;
            session.endpoint = new GroundEndpoint(session); session.Source.AddGround(session.endpoint); session.Source.Activate();
            sessions.Add(session); result = session; return true;
        }
        public void Dispose() { if (disposed) return; disposed = true; sessions.Remove(this); pending.Remove(Source); Source.Deactivate(); members.Clear(); }
        internal static void PublishPending()
        {
            if (pending.Count == 0) return;
            var sources = new List<LootSource>(pending); pending.Clear();
            foreach (LootSource source in sources) source.Publish();
        }
        internal static void RemoveDestroyed(NetworkGroundLootItem item)
        {
            RemoveFromOtherSessions(item, null); PublishPending();
        }
        private static void RemoveFromOtherSessions(NetworkGroundLootItem item, GroundLootSession owner)
        {
            foreach (GroundLootSession session in sessions)
            {
                if (ReferenceEquals(session, owner) || !session.members.Remove(item.Item?.Id ?? "")) continue;
                var contents = new List<ItemDisplayData>();
                foreach (ItemDisplayData value in session.data.Items) if (value.Id != item.Item.Id) contents.Add(value);
                // 删除已验证网格中的物品不会破坏几何约束；不触发外部回调。
                if (!session.data.TryPrepare(contents, out BackpackContainerData.PreparedContents state, out _)) continue;
                session.data.Commit(state);
                if (session.Source.Revision == int.MaxValue) session.Source.Deactivate(); else session.Source.Bump();
                pending.Add(session.Source);
            }
        }
        private sealed class Prepared
        {
            internal BackpackContainerData.PreparedContents contents;
            internal readonly List<NetworkGroundLootItem> removed = new List<NetworkGroundLootItem>();
            internal readonly List<PreparedRemoval> otherSessions = new List<PreparedRemoval>();
        }
        private sealed class PreparedRemoval
        {
            internal GroundLootSession session;
            internal BackpackContainerData.PreparedContents contents;
            internal readonly List<string> removedIds = new List<string>();
        }
        private static bool TryPrepareOtherSessions(GroundLootSession owner, Prepared prepared, out string error)
        {
            error = null;
            if (prepared.removed.Count == 0) return true;
            foreach (GroundLootSession session in sessions)
            {
                if (ReferenceEquals(session, owner)) continue;
                var removal = new PreparedRemoval { session = session };
                foreach (NetworkGroundLootItem member in prepared.removed)
                    if (session.members.ContainsKey(member.Item.Id)) removal.removedIds.Add(member.Item.Id);
                if (removal.removedIds.Count == 0) continue;
                var contents = new List<ItemDisplayData>();
                foreach (ItemDisplayData value in session.data.Items)
                    if (!removal.removedIds.Contains(value.Id)) contents.Add(value);
                if (!session.data.TryPrepare(contents, out removal.contents, out error)) return false;
                prepared.otherSessions.Add(removal);
            }
            return true;
        }
        private sealed class GroundEndpoint : IExternalLootEndpoint
        {
            private readonly GroundLootSession session;
            private readonly LoadoutEndpointAddress address;
            internal GroundEndpoint(GroundLootSession session) { this.session = session; address = new LoadoutEndpointAddress(this); }
            public object EventSource => address;
            public ContainerSnapshot Snapshot => new ContainerSnapshot(session.data.Id, ContainerKind.None, null, session.data.Items, session.data.ValidCells,
                EventSource, session.Source.Revision, rejectContainers: false, isReady: session.Source.IsActive, withdrawalOnly: true);
            public bool TryPrepare(IReadOnlyList<ItemDisplayData> items, out object prepared, out string error)
            {
                prepared = null; error = null;
                if (session.disposed || !session.Source.IsActive || session.Source.Revision == int.MaxValue)
                { error = "地面会话已失效。"; return false; }
                var state = new Prepared(); var retained = new HashSet<string>(StringComparer.Ordinal);
                foreach (ItemDisplayData value in items)
                {
                    if (!session.members.TryGetValue(value.Id, out NetworkGroundLootItem member) || member == null || !member.IsPresent
                        || !session.data.TryGetItem(value.Id, out ItemDisplayData previous) || !ReferenceEquals(previous.Storage, value.Storage)
                        || previous.Count != value.Count || previous.DefinitionId != value.DefinitionId)
                    { error = "地面来源只允许操作本会话仍归地面的物品。"; return false; }
                    retained.Add(value.Id);
                }
                foreach (ItemDisplayData previous in session.data.Items)
                {
                    if (!session.members.TryGetValue(previous.Id, out NetworkGroundLootItem member) || member == null || !member.IsPresent)
                    { error = "地面物品已被其他玩家取走。"; return false; }
                    if (!retained.Contains(previous.Id)) state.removed.Add(member);
                }
                if (!session.data.TryPrepare(items, out state.contents, out error)) return false;
                if (!TryPrepareOtherSessions(session, state, out error)) return false;
                prepared = state; return true;
            }
            public void Commit(object prepared)
            {
                var state = (Prepared)prepared;
                session.data.Commit(state.contents); session.Source.Bump();
                foreach (NetworkGroundLootItem member in state.removed)
                {
                    member.Take(); session.members.Remove(member.Item.Id);
                }
                foreach (PreparedRemoval removal in state.otherSessions)
                {
                    GroundLootSession other = removal.session;
                    other.data.Commit(removal.contents);
                    foreach (string id in removal.removedIds) other.members.Remove(id);
                    if (other.Source.Revision == int.MaxValue) other.Source.Deactivate();
                    else other.Source.Bump();
                    pending.Add(other.Source);
                }
            }
            public void PublishChanged() => EventBus.Global.Publish(LoadoutChannels.Changed, EventSource);
        }
    }
}
