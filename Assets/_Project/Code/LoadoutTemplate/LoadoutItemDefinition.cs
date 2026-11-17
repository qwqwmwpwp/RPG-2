using RPG4.Backpack.Core;
using RPG4.Backpack.Items;
using UnityEngine;

namespace RPG4.LoadoutTemplate
{
    [CreateAssetMenu(menuName = "RPG4/Loadout Template/Item Definition")]
    public sealed class LoadoutItemDefinition : ScriptableObject
    {
        [Tooltip("稳定网络定义编号；留空时使用资产名称，发布后不要重命名。")]
        [SerializeField] private string definitionId;
        [SerializeField] private string displayName;
        [SerializeField] private Vector2Int size = Vector2Int.one;
        [SerializeField] private Sprite icon;
        [SerializeField] private BackpackTag itemType;
        [SerializeField] private BackpackTag equipmentType;
        [SerializeField] private ContainerKind containerType;
        [Tooltip("可选运行物品模板；未配置时仍支持展示与移动。")][SerializeField] private ItemManifest itemManifest;
        public ItemManifest Manifest => itemManifest;
        public string DefinitionId => string.IsNullOrWhiteSpace(definitionId) ? name : definitionId;
        internal string ConfigurationSignature => JsonUtility.ToJson(new DefinitionSignature
        {
            id = DefinitionId, name = displayName, size = size, kind = (int)containerType,
            itemTag = itemType.Name, equipmentTag = equipmentType.Name, manifest = LoadoutRuntimeItems.ManifestSignature(itemManifest)
        });
        [System.Serializable] private sealed class DefinitionSignature
        {
            public string id, name, itemTag, equipmentTag, manifest;
            public Vector2Int size;
            public int kind;
        }
        public ContainerKind ContainerType => containerType;
        public Sprite Icon => icon;
        public ItemDisplayData Create(string id, Vector2Int origin, int count = 1, BackpackContainerData storage = null,
            RuntimeItem runtimeItem = null, bool createRuntime = true)
        {
            if (runtimeItem == null && createRuntime && itemManifest != null)
                runtimeItem = itemManifest.CreateRuntimeItem(LoadoutRuntimeItems.SharedItemIds.Next(), count);
            Vector2Int dimensions = runtimeItem == null ? size : new Vector2Int(runtimeItem.Size.X, runtimeItem.Size.Y);
            return new ItemDisplayData(id, origin, dimensions, icon, count, displayName, containerType,
                itemManifest != null ? itemManifest.ItemType : itemType, equipmentType, storage, DefinitionId, runtimeItem: runtimeItem);
        }
    }
}
