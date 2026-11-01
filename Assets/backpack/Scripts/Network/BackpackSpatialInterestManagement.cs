using System;
using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace RPG4.Backpack.Network
{
    /// <summary>按场景和九宫格维护候选缓存，仅在空间成员或可见性变化时重建观察者。</summary>
    [AddComponentMenu("RPG4/Backpack Spatial Interest Management")]
    public sealed class BackpackSpatialInterestManagement : InterestManagement
    {
        // 保留原 SpatialHashingInterestManagement 的序列化字段名、类型和值。
        public int visRange = 30;
        public float rebuildInterval = 1;
        public SpatialHashingInterestManagement.CheckMethod checkMethod = SpatialHashingInterestManagement.CheckMethod.XZ_FOR_3D;
        public bool showSlider;
        public int resolution => Mathf.Max(1, visRange / 2);

        private readonly struct CellKey : IEquatable<CellKey>
        {
            public readonly int Scene, X, Y;
            public CellKey(int scene, int x, int y) { Scene = scene; X = x; Y = y; }
            public bool Equals(CellKey other) => Scene == other.Scene && X == other.X && Y == other.Y;
            public override bool Equals(object obj) => obj is CellKey other && Equals(other);
            public override int GetHashCode() => unchecked((Scene * 397 ^ X) * 397 ^ Y);
        }

        private sealed class Cell
        {
            public readonly HashSet<NetworkIdentity> Identities = new HashSet<NetworkIdentity>();
            public readonly HashSet<NetworkConnectionToClient> Connections = new HashSet<NetworkConnectionToClient>();
            public readonly HashSet<NetworkConnectionToClient> Candidates = new HashSet<NetworkConnectionToClient>();
            public bool CandidatesDirty = true;
        }

        private sealed class IdentityState
        {
            public CellKey Key;
            public Visibility Visibility;
            public NetworkConnectionToClient Owner;
            public bool Stationary;
        }

        private readonly Dictionary<CellKey, Cell> cells = new Dictionary<CellKey, Cell>();
        private readonly Dictionary<NetworkIdentity, IdentityState> identities = new Dictionary<NetworkIdentity, IdentityState>();
        private readonly Dictionary<NetworkConnectionToClient, CellKey> connections = new Dictionary<NetworkConnectionToClient, CellKey>();
        private readonly HashSet<NetworkConnectionToClient> seenConnections = new HashSet<NetworkConnectionToClient>();
        private readonly List<NetworkConnectionToClient> removedConnections = new List<NetworkConnectionToClient>();
        private readonly HashSet<NetworkIdentity> dirty = new HashSet<NetworkIdentity>();
        private readonly HashSet<NetworkIdentity> globallyVisible = new HashSet<NetworkIdentity>();
        private readonly List<NetworkIdentity> rebuild = new List<NetworkIdentity>();
        private readonly List<NetworkIdentity> removedIdentities = new List<NetworkIdentity>();
        private double nextPollTime;
        private int indexedRange;
        private SpatialHashingInterestManagement.CheckMethod indexedMethod;
        private bool rebuilding;

        private void OnValidate()
        {
            visRange = Mathf.Max(2, visRange);
            rebuildInterval = Mathf.Max(0.05f, rebuildInterval);
        }

        protected override void OnEnable()
        {
            OnValidate();
            base.OnEnable();
        }

        private CellKey GetKey(NetworkIdentity identity)
        {
            Vector3 position = identity.transform.position;
            float y = checkMethod == SpatialHashingInterestManagement.CheckMethod.XZ_FOR_3D ? position.z : position.y;
            return new CellKey(identity.gameObject.scene.handle, Mathf.RoundToInt(position.x / resolution), Mathf.RoundToInt(y / resolution));
        }

        private Cell GetCell(CellKey key)
        {
            if (!cells.TryGetValue(key, out Cell cell)) cells.Add(key, cell = new Cell());
            return cell;
        }

        private void RemoveEmptyCell(CellKey key)
        {
            if (cells.TryGetValue(key, out Cell cell) && cell.Identities.Count == 0 && cell.Connections.Count == 0)
                cells.Remove(key);
        }

        private static bool Eligible(NetworkConnectionToClient connection) => connection != null
            && connection.isAuthenticated && connection.isReady && connection.identity != null
            && NetworkServer.connections.TryGetValue(connection.connectionId, out NetworkConnectionToClient current)
            && current == connection;

        private static bool InScene(NetworkIdentity identity, NetworkConnectionToClient observer) => identity != null
            && Eligible(observer) && identity.gameObject.scene == observer.identity.gameObject.scene;

        // 连接变化只影响前后位置九宫格；候选失效和身份重建均合并。
        private void InvalidateNeighbours(CellKey key)
        {
            for (int x = -1; x <= 1; ++x)
                for (int y = -1; y <= 1; ++y)
                    if (cells.TryGetValue(new CellKey(key.Scene, key.X + x, key.Y + y), out Cell cell))
                    {
                        cell.CandidatesDirty = true;
                        dirty.UnionWith(cell.Identities);
                    }
        }

        private void InvalidateGlobalVisibility()
        {
            dirty.UnionWith(globallyVisible);
        }

        private void RemoveConnection(NetworkConnectionToClient connection, CellKey previous, bool membershipChanged = true)
        {
            connections.Remove(connection);
            if (cells.TryGetValue(previous, out Cell cell)) cell.Connections.Remove(connection);
            InvalidateNeighbours(previous);
            RemoveEmptyCell(previous);
            if (membershipChanged) InvalidateGlobalVisibility();
        }

        private void RefreshConnection(NetworkConnectionToClient connection)
        {
            bool existed = connections.TryGetValue(connection, out CellKey previous);
            bool eligible = Eligible(connection);
            CellKey current = eligible ? GetKey(connection.identity) : default;
            if (existed && eligible && previous.Equals(current)) return;
            if (existed) RemoveConnection(connection, previous, !eligible);
            if (eligible)
            {
                GetCell(current).Connections.Add(connection);
                connections[connection] = current;
                InvalidateNeighbours(current);
                if (!existed) InvalidateGlobalVisibility();
            }
        }

        private void RefreshConnections()
        {
            seenConnections.Clear();
            foreach (NetworkConnectionToClient connection in NetworkServer.connections.Values)
            {
                if (connection == null) continue;
                seenConnections.Add(connection);
                RefreshConnection(connection);
            }
            removedConnections.Clear();
            foreach (NetworkConnectionToClient connection in connections.Keys)
                if (!seenConnections.Contains(connection)) removedConnections.Add(connection);
            foreach (NetworkConnectionToClient connection in removedConnections)
                RemoveConnection(connection, connections[connection]);
        }

        private IdentityState RefreshIdentity(NetworkIdentity identity, bool samplePosition)
        {
            if (!identities.TryGetValue(identity, out IdentityState state))
            {
                state = new IdentityState { Key = GetKey(identity), Visibility = identity.visibility, Owner = identity.connectionToClient };
                identities.Add(identity, state);
                if (state.Visibility == Visibility.ForceShown) globallyVisible.Add(identity);
                GetCell(state.Key).Identities.Add(identity);
                dirty.Add(identity);
                return state;
            }
            // 静态契约只省略坐标轮询，场景、显隐和所有权仍有兜底检查。
            CellKey current = samplePosition || state.Key.Scene != identity.gameObject.scene.handle ? GetKey(identity) : state.Key;
            if (!state.Key.Equals(current))
            {
                CellKey previous = state.Key;
                if (cells.TryGetValue(previous, out Cell cell)) cell.Identities.Remove(identity);
                state.Key = current;
                GetCell(current).Identities.Add(identity);
                RemoveEmptyCell(previous);
                dirty.Add(identity);
            }
            if (state.Visibility != identity.visibility || state.Owner != identity.connectionToClient)
            {
                state.Visibility = identity.visibility;
                state.Owner = identity.connectionToClient;
                if (state.Visibility == Visibility.ForceShown) globallyVisible.Add(identity);
                else globallyVisible.Remove(identity);
                dirty.Add(identity);
            }
            return state;
        }

        /// <summary>服务器移动/传送后调用，也可立即通知场景、显隐或所有权变化；不改变任何游戏仿真。</summary>
        public void NotifyIdentityMoved(NetworkIdentity identity)
        {
            if (!NetworkServer.active || identity == null || identity.netId == 0) return;
            RefreshIdentity(identity, true);
            NetworkConnectionToClient owner = identity.connectionToClient;
            if (owner != null && owner.identity == identity) RefreshConnection(owner);
        }

        /// <summary>
        /// 显式声明已 spawn 身份的坐标无需轮询。调用方保证每次移动后调用 NotifyIdentityMoved，
        /// 或在恢复持续移动前传入 false；未知物体默认动态。此设置不影响服务器模拟。
        /// </summary>
        public void SetIdentityStationary(NetworkIdentity identity, bool stationary)
        {
            if (!NetworkServer.active || identity == null || identity.netId == 0) return;
            RefreshIdentity(identity, true).Stationary = stationary;
        }

        public override void OnSpawned(NetworkIdentity identity) => RefreshIdentity(identity, true);

        public override void OnDestroyed(NetworkIdentity identity)
        {
            if (!identities.TryGetValue(identity, out IdentityState state)) return;
            identities.Remove(identity);
            dirty.Remove(identity);
            globallyVisible.Remove(identity);
            if (cells.TryGetValue(state.Key, out Cell cell)) cell.Identities.Remove(identity);
            RemoveEmptyCell(state.Key);
        }

        public override bool OnCheckObserver(NetworkIdentity identity, NetworkConnectionToClient newObserver)
        {
            if (identity == null || !Eligible(newObserver)) return false;
            if (identity.connectionToClient == newObserver) return true;
            if (!InScene(identity, newObserver)) return false;
            CellKey a = GetKey(identity), b = GetKey(newObserver.identity);
            return Math.Abs(a.X - b.X) <= 1 && Math.Abs(a.Y - b.Y) <= 1;
        }

        public override void OnRebuildObservers(NetworkIdentity identity, HashSet<NetworkConnectionToClient> newObservers)
        {
            // ForceShown 保留 Mirror 全局可见的覆盖含义，正常身份始终按 Scene 隔离。
            if (identity.visibility == Visibility.ForceShown)
            {
                foreach (NetworkConnectionToClient connection in NetworkServer.connections.Values)
                    if (Eligible(connection)) newObservers.Add(connection);
                return;
            }
            IdentityState state = RefreshIdentity(identity, true);
            Cell cell = GetCell(state.Key);
            if (cell.CandidatesDirty)
            {
                cell.Candidates.Clear();
                for (int x = -1; x <= 1; ++x)
                    for (int y = -1; y <= 1; ++y)
                        if (cells.TryGetValue(new CellKey(state.Key.Scene, state.Key.X + x, state.Key.Y + y), out Cell neighbour))
                            cell.Candidates.UnionWith(neighbour.Connections);
                cell.CandidatesDirty = false;
            }
            foreach (NetworkConnectionToClient connection in cell.Candidates)
                if (InScene(identity, connection)) newObservers.Add(connection);
        }

        public override void Rebuild(NetworkIdentity identity, bool initialize)
        {
            // Spawn 或外部主动 Rebuild 可能发生在本组件 Update 之前。
            if (!rebuilding) RefreshConnections();
            RefreshIdentity(identity, true);
            base.Rebuild(identity, initialize);
            // Mirror 保留 owner 时仅检查 ready；已退出/失去认证的连接不能被重新保留。
            NetworkConnectionToClient owner = identity.connectionToClient;
            if (owner != null && !Eligible(owner) && identity.observers.ContainsKey(owner.connectionId))
                RemoveObserver(owner, identity);
        }

        public override void ResetState()
        {
            cells.Clear(); identities.Clear(); connections.Clear(); dirty.Clear(); globallyVisible.Clear();
            seenConnections.Clear(); removedConnections.Clear(); removedIdentities.Clear(); rebuild.Clear();
            nextPollTime = 0; indexedRange = 0; rebuilding = false;
        }

        private void Update()
        {
            if (!NetworkServer.active) return;
            // 半径/投影方式变化只触发一次完整索引更新，平常不使用 RebuildAll。
            if (indexedRange != visRange || indexedMethod != checkMethod)
            {
                OnValidate();
                indexedRange = visRange; indexedMethod = checkMethod;
                cells.Clear(); connections.Clear();
                foreach (KeyValuePair<NetworkIdentity, IdentityState> pair in identities)
                {
                    if (pair.Key == null) continue;
                    pair.Value.Key = GetKey(pair.Key);
                    GetCell(pair.Value.Key).Identities.Add(pair.Key);
                    dirty.Add(pair.Key);
                }
            }
            RefreshConnections();
            if (NetworkTime.localTime >= nextPollTime)
            {
                nextPollTime = NetworkTime.localTime + Mathf.Max(0.05f, rebuildInterval);
                foreach (NetworkIdentity identity in NetworkServer.spawned.Values)
                    if (identity != null)
                        RefreshIdentity(identity, !identities.TryGetValue(identity, out IdentityState state) || !state.Stationary);
                removedIdentities.Clear();
                foreach (NetworkIdentity identity in identities.Keys)
                    if (identity == null || !NetworkServer.spawned.TryGetValue(identity.netId, out NetworkIdentity spawned) || spawned != identity)
                        removedIdentities.Add(identity);
                foreach (NetworkIdentity identity in removedIdentities) OnDestroyed(identity);
            }
            rebuild.Clear();
            rebuild.AddRange(dirty);
            dirty.Clear();
            rebuilding = true;
            try
            {
                foreach (NetworkIdentity identity in rebuild)
                    if (identity != null && NetworkServer.spawned.TryGetValue(identity.netId, out NetworkIdentity spawned) && spawned == identity)
                        // 直接调用，以处理运行时 ForceShown 变化；Mirror 默认分支仅处理 initialize。
                        Rebuild(identity, false);
            }
            finally { rebuilding = false; }
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private void OnGUI()
        {
            if (!showSlider || !NetworkServer.active) return;
            GUILayout.BeginArea(new Rect(Screen.width / 2 - 125, Screen.height - 30, 250, 30));
            GUILayout.BeginHorizontal("Box");
            GUILayout.Label("Radius:");
            visRange = Mathf.Max(2, Mathf.RoundToInt(GUILayout.HorizontalSlider(visRange, 2, 200, GUILayout.Width(150))));
            GUILayout.Label(visRange.ToString());
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }
#endif
    }
}
