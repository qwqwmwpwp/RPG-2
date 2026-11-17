using System;
using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace RPG4.LoadoutTemplate
{
    [Serializable]
    public sealed class FixedLootChestSeed
    {
        public string itemId;
        public LoadoutItemDefinition definition;
        public Vector2Int origin;
        [Min(1)] public int count = 1;
        public bool rotated;
    }

    /// <summary>服务器固定尺寸箱子。严格使用配置坐标；无效配置整体拒绝，不裁剪或重排物品。</summary>
    [DisallowMultipleComponent, RequireComponent(typeof(NetworkLootSource))]
    public sealed class FixedLootChest : NetworkBehaviour
    {
        [SerializeField] private string chestId = "fixed-chest";
        [SerializeField, Range(1, BackpackContainerData.MaxColumns)] private int columns = BackpackContainerData.MaxColumns;
        [SerializeField, Min(1)] private int rows = 6;
        [SerializeField] private FixedLootChestSeed[] seeds = Array.Empty<FixedLootChestSeed>();

        public override void OnStartServer()
        {
            base.OnStartServer();
            if (!TryInitializeServer(out string error)) Debug.LogError("固定箱子初始化失败：" + error, this);
        }

        public bool TryInitializeServer(out string error)
        {
            error = null;
            NetworkLootSource networkSource = GetComponent<NetworkLootSource>();
            if (!NetworkServer.active || networkSource == null)
                return Fail("固定箱子需要服务器与战利品访问点。", out error);
            // 场景启动和显式初始化可以重复进入，但绝不重新播种已使用的箱子。
            if (networkSource.Source != null)
                return networkSource.Source.AccessPolicy == LootAccessPolicy.ReadWrite || Fail("访问点已绑定其他来源。", out error);
            if (columns < 1 || columns > BackpackContainerData.MaxColumns)
                return Fail("箱子列数必须在 1 到 5 之间。", out error);
            if (string.IsNullOrWhiteSpace(chestId) || chestId.Length > 64 || rows < 1
                || (long)columns * rows > 1024 || (seeds?.Length ?? 0) > 256)
                return Fail("箱子编号、行列或物品数超过协议范围。", out error);
            var source = new LootSource(LootAccessPolicy.ReadWrite);
            string gridId = source.Id + "/" + chestId;
            var cells = new List<Vector2Int>();
            for (int y = 0; y < rows; y++) for (int x = 0; x < columns; x++) cells.Add(new Vector2Int(x, y));
            var items = new List<ItemDisplayData>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (FixedLootChestSeed seed in seeds ?? Array.Empty<FixedLootChestSeed>())
            {
                if (seed == null || string.IsNullOrWhiteSpace(seed.itemId) || seed.definition == null || seed.count < 1
                    || !ids.Add(seed.itemId) || seed.definition.ContainerType != ContainerKind.None
                    || string.IsNullOrWhiteSpace(seed.definition.DefinitionId) || seed.definition.DefinitionId.Length > 160)
                    return Fail("初始物品需要唯一稳定编号、普通物品定义与正整数数量。", out error);
                string id = gridId + "/" + seed.itemId;
                if (id.Length > 160) return Fail("初始物品编号超过协议范围。", out error);
                items.Add(seed.definition.Create(id, seed.origin, seed.count).WithRotation(seed.rotated));
            }
            if (!BackpackContainerData.TryCreate(gridId, ContainerKind.None, null, cells, items, out BackpackContainerData data, out error)) return false;
            source.Add(LootSectionKind.FixedGrid, data);
            source.Activate();
            return networkSource.InitializeServer(source, out error);
        }
        private static bool Fail(string reason, out string error) { error = reason; return false; }
    }
}
