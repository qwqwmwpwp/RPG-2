using System;
using System.Collections.Generic;
using RPG4.Backpack.Core;
using UnityEngine;

namespace RPG4.LoadoutTemplate
{
    [Serializable]
    public sealed class ContainerTestItem
    {
        public string id;
        public string displayName;
        public Vector2Int origin;
        public Vector2Int size = Vector2Int.one;
        public int count = 1;
        public Sprite icon;
        public LoadoutItemDefinition definition;
        public BackpackTag itemType;
        public BackpackTag equipmentType;
    }

    [Serializable]
    public sealed class ContainerInitialContent
    {
        public LoadoutItemDefinition definition;
        public Vector2Int origin;
        [Min(1)] public int count = 1;
    }

    /// <summary>稳定容器入口，容器本体槽与储物实例成组交换。</summary>
    [DisallowMultipleComponent]
    public sealed class BackpackContainerController : MonoBehaviour, ILoadoutEndpoint
    {
        [Header("容器初始参数")]
        [SerializeField] private string containerId = "backpack";
        [SerializeField] private bool autoGenerateInstanceId = true;
        [SerializeField] private ContainerKind containerType = ContainerKind.Backpack;
        [SerializeField] private Vector2Int gridSize = new Vector2Int(BackpackContainerData.MaxColumns, 3);
        [SerializeField] private ContainerLayoutMode layoutMode = ContainerLayoutMode.Rectangle;
        [SerializeField] private Vector2Int[] validCells = Array.Empty<Vector2Int>();
        [SerializeField] private Sprite containerIcon;
        [SerializeField] private LoadoutItemDefinition containerItemDefinition;
        [SerializeField] private LoadoutSlotRule containerSlotRule;
        [Header("正式初始内容（默认空；实例编号由权威端创建）")]
        [SerializeField] private ContainerInitialContent[] contents = Array.Empty<ContainerInitialContent>();
        [Header("仅本地测试物品")]
        [SerializeField] private bool seedTestItems;
        [SerializeField] private ContainerTestItem[] testItems = Array.Empty<ContainerTestItem>();
        private BackpackContainerData container;
        private LoadoutSlotEndpoint bodyEndpoint;
        private LoadoutSlotRule fallbackRule;
        private LoadoutEndpointEvents gridEvents;
        private LoadoutEndpointEvents bodyEvents;
        private string runtimeId;
        private RoleLoadoutController runtimeOwner;
        internal RoleLoadoutController RuntimeOwner => runtimeOwner;
        internal void AttachRuntimeOwner(RoleLoadoutController owner) { runtimeOwner = owner; }
        private int revision;
        private LoadoutEndpointAddress eventSource;

        public BackpackContainerData Container => container;
        public bool IsReady => this != null && container != null && isActiveAndEnabled && (bodyEndpoint == null || bodyEndpoint.IsAvailable);
        public object EventSource => eventSource ?? (eventSource = new LoadoutEndpointAddress(this));
        public ILoadoutEndpoint BodyEndpoint { get { EnsureInitialized(out _); return bodyEndpoint; } }
        public ContainerSnapshot Snapshot => container == null
            ? new ContainerSnapshot(runtimeId + "/empty", containerType, null, validCells: Array.Empty<Vector2Int>(),
                eventSource: EventSource, revision: revision, isReady: false)
            : new ContainerSnapshot(container.Id, container.Kind, container.Icon, container.Items, container.ValidCells,
                EventSource, revision, rejectContainers: container.RejectContainers, isReady: IsReady);
        public event Action<BackpackContainerController> Changed;

        internal bool NetworkManaged => GetComponentInParent<NetworkRoleLoadout>(true) != null;
        internal LoadoutItemDefinition Definition => containerItemDefinition;
        internal IReadOnlyList<ContainerInitialContent> InitialContents => contents;
        internal ContainerKind ConfiguredKind => containerType;
        internal long ConfiguredCellCount => layoutMode == ContainerLayoutMode.Rectangle
            ? (long)gridSize.x * gridSize.y : validCells?.LongLength ?? 0;
        internal string ConfigurationSignature => JsonUtility.ToJson(new ShapeConfiguration { kind = (int)containerType, size = gridSize, mode = (int)layoutMode, cells = validCells });
        [Serializable] private sealed class ShapeConfiguration { public int kind, mode; public Vector2Int size; public Vector2Int[] cells; }
        internal ItemDisplayData CreateConfiguredBody(string id, BackpackContainerData storage)
        {
            if (containerItemDefinition != null) return containerItemDefinition.Create(id, Vector2Int.zero, storage: storage);
            var tag = new BackpackTag("GameItems.Equipment.Containers." + containerType);
            return new ItemDisplayData(id, Vector2Int.zero, Vector2Int.one, containerIcon, name: containerType.ToString(),
                containerType: containerType, itemType: tag, equipmentType: tag, storage: storage);
        }
        internal bool TryGetConfiguredShape(out List<Vector2Int> shape, out string error) => TryBuildShape(out shape, out error);
        internal void ApplyNetworkStorage(BackpackContainerData value, int version) { container = value; revision = version; }

        private void Awake() { if (NetworkManaged) return; if (!EnsureInitialized(out string error)) Debug.LogWarning(error, this); }
        private void OnEnable()
        {
            if (NetworkManaged) return;
            if (!EnsureInitialized(out string error)) { Debug.LogWarning(error, this); return; }
            BindEvents(); PublishChanged(); bodyEndpoint.PublishChanged();
        }
        private void OnDisable()
        {
            PublishChanged(); bodyEndpoint?.PublishChanged();
            // 保留查询订阅，停用后仍可返回 IsReady=false 快照。
        }
        private void OnDestroy()
        {
            gridEvents?.Dispose(); bodyEvents?.Dispose();
            if (fallbackRule != null) Destroy(fallbackRule);
        }

        public bool EnsureInitialized(out string error)
        {
            error = null;
            if (bodyEndpoint != null) return true;
            if (NetworkManaged && !Mirror.NetworkServer.active) return Fail("网络容器只允许服务器初始化权威实例。", out error);
            if (string.IsNullOrWhiteSpace(containerId)) return Fail("容器编号或编号前缀不能为空。", out error);
            if (containerItemDefinition != null && containerItemDefinition.ContainerType != containerType)
                return Fail("容器定义与初始容器类别不匹配。", out error);
            if (!TryBuildShape(out List<Vector2Int> shape, out error)) return false;
            runtimeId = NetworkManaged || autoGenerateInstanceId ? containerId + "@" + Guid.NewGuid().ToString("N") : containerId;
            var initialItems = new List<ItemDisplayData>();
            foreach (ContainerInitialContent content in contents ?? Array.Empty<ContainerInitialContent>())
            {
                if (content == null || content.definition == null || content.count < 1)
                    return Fail("正式初始内容需要物品定义和正整数数量。", out error);
                initialItems.Add(content.definition.Create(runtimeId + "/content/" + initialItems.Count,
                    content.origin, content.count));
            }
            if (seedTestItems && !NetworkManaged)
            {
                foreach (ContainerTestItem seed in testItems ?? Array.Empty<ContainerTestItem>())
                {
                    if (seed == null || string.IsNullOrWhiteSpace(seed.id)) return Fail("测试物品的局部编号不能为空。", out error);
                    initialItems.Add(seed.definition != null
                        ? seed.definition.Create(runtimeId + "/" + seed.id, seed.origin, seed.count)
                        : new ItemDisplayData(runtimeId + "/" + seed.id, seed.origin, seed.size, seed.icon,
                            seed.count, seed.displayName, itemType: seed.itemType, equipmentType: seed.equipmentType));
                }
            }
            if (!BackpackContainerData.TryCreate(runtimeId, containerType, containerItemDefinition != null
                ? containerItemDefinition.Icon : containerIcon, shape, initialItems, out BackpackContainerData created, out error)) return false;
            var tag = new BackpackTag("GameItems.Equipment.Containers." + containerType);
            ItemDisplayData bodyItem = containerItemDefinition != null
                ? containerItemDefinition.Create(runtimeId + "/body", Vector2Int.zero, storage: created)
                : new ItemDisplayData(runtimeId + "/body", Vector2Int.zero, Vector2Int.one, containerIcon,
                    name: containerType.ToString(), containerType: containerType, itemType: tag, equipmentType: tag, storage: created);
            LoadoutSlotRule rule = containerSlotRule;
            if (rule == null)
            {
                if (fallbackRule == null) fallbackRule = LoadoutSlotRule.CreateRuntime(tag);
                rule = fallbackRule;
            }
            container = created;
            bodyEndpoint = new LoadoutSlotEndpoint(runtimeId + "/slot", rule, bodyItem,
                () => this != null && isActiveAndEnabled, SetContainerItem, PublishChanged);
            if (!LoadoutOperations.Validate(bodyEndpoint.Snapshot, bodyEndpoint.Snapshot.Items, out error))
            {
                bodyEndpoint = null;
                container = null;
                if (fallbackRule != null)
                {
                    Destroy(fallbackRule);
                    fallbackRule = null;
                }
                return false;
            }
            if (isActiveAndEnabled) BindEvents();
            return true;
        }
        private void BindEvents()
        {
            if (NetworkManaged) return;
            gridEvents?.Dispose(); bodyEvents?.Dispose();
            gridEvents = new LoadoutEndpointEvents(this); bodyEvents = new LoadoutEndpointEvents(bodyEndpoint);
        }
        private void SetContainerItem(ItemDisplayData value) { container = value?.Storage; revision++; }
        private bool TryBuildShape(out List<Vector2Int> shape, out string error)
        {
            shape = new List<Vector2Int>(); error = null;
            if (layoutMode == ContainerLayoutMode.Coordinates)
            {
                var unique = new HashSet<Vector2Int>();
                foreach (Vector2Int cell in validCells ?? Array.Empty<Vector2Int>())
                {
                    if (!BackpackContainerData.IsSupportedCoordinate(cell) || !unique.Add(cell))
                        return Fail("容器有效格坐标无效或重复；列坐标必须在 0 到 4 之间。", out error);
                    shape.Add(cell);
                }
                return true;
            }
            if (layoutMode != ContainerLayoutMode.Rectangle) return Fail("数据容器使用矩形或显式有效格配置。", out error);
            if (gridSize.x < 1 || gridSize.x > BackpackContainerData.MaxColumns || gridSize.y < 1
                || (long)gridSize.x * gridSize.y > int.MaxValue)
                return Fail("容器列数必须在 1 到 5 之间，行数须为正数且总格数不能超出索引范围。", out error);
            for (int y = 0; y < gridSize.y; y++)
            for (int x = 0; x < gridSize.x; x++) shape.Add(new Vector2Int(x, y));
            return true;
        }
        public bool TryGetSnapshot(out ContainerSnapshot snapshot, out string error)
        {
            snapshot = null;
            if (!EnsureInitialized(out error)) return false;
            if (!IsReady) return Fail("容器控制器当前没有可用储物实例。", out error);
            snapshot = Snapshot; return true;
        }
        public bool TryPrepare(IReadOnlyList<ItemDisplayData> items, out object prepared, out string error)
        {
            prepared = null;
            if (!IsReady) return Fail("容器当前不可用。", out error);
            if (!container.TryPrepare(items, out BackpackContainerData.PreparedContents contents, out error)) return false;
            prepared = contents; return true;
        }
        public void Commit(object prepared) { container.Commit((BackpackContainerData.PreparedContents)prepared); revision++; }
        public bool TryApplyRequest(LoadoutDropIntent request, BackpackContainerController target,
            out string error, bool publishChanges = true)
        {
            if (NetworkManaged) return Fail("网络角色需要通过拥有者操作请求提交。", out error);
            return LoadoutOperations.TryApply(this, target, request, out error, publishChanges);
        }
        public void PublishChanged()
        {
            EventBus.Global.Publish(LoadoutChannels.Changed, EventSource);
            Changed?.Invoke(this);
            runtimeOwner?.PublishRuntimeChanged();
        }
        private static bool Fail(string message, out string error) { error = message; return false; }
    }
}





