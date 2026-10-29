using System.Collections.Generic;
using RPG4.Backpack.Core;
using RPG4.Backpack.Equipment;
using RPG4.Backpack.Items;
using RPG4.Backpack.Runtime;
using UnityEngine;

namespace RPG4.PlayerMovement
{
    /// <summary>装备只引用本角色背包中的物品，武器运行时按实例身份保留冷却。</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(100)]
    [RequireComponent(typeof(BackpackInventory))]
    public sealed class PlayerEquipment : MonoBehaviour
    {
        [SerializeField] private BackpackInventory inventory;
        [SerializeField] private PlayerWeaponRig weaponRig;
        private readonly Dictionary<ItemId, Weapon> weapons = new Dictionary<ItemId, Weapon>();
        private readonly List<RuntimeItem> items = new List<RuntimeItem>();
        private EventSubscription inventoryChanged;
        private object eventSource;
        private Player player;
        private Weapon current;
        private ItemId resumeItemId;
        private bool started;
        public ItemId CurrentItemId => CurrentWeapon != null ? current.ItemId : ItemId.None;
        public Weapon CurrentWeapon => InventoryAvailable && isActiveAndEnabled ? current : null;
        private bool InventoryAvailable => inventory != null && inventory.isActiveAndEnabled && inventory.IsInitialized;

        private void Awake()
        {
            if (inventory == null) inventory = GetComponent<BackpackInventory>();
            if (weaponRig == null) weaponRig = GetComponent<PlayerWeaponRig>();
            player = GetComponent<Player>();
            weaponRig?.PrepareEquipment();
        }
        private void OnEnable()
        {
            BindInventory();
            RestoreSelection();
        }
        private void Start()
        {
            started = true;
            BindInventory();
            if (!InventoryAvailable || current != null) return;
            inventory.CopyItems(items); // CopyItems 的契约为 ItemId 升序。
            foreach (RuntimeItem item in items)
                if (TryEquip(item.ItemId, out _)) break;
            items.Clear();
        }
        private void Update() => ValidateEquipment();
        private void LateUpdate() => ValidateEquipment();
        private void BindInventory()
        {
            object source = InventoryAvailable ? inventory.EventSource : null;
            if (ReferenceEquals(eventSource, source) && (source == null || inventoryChanged.IsAlive)) return;
            inventoryChanged.Dispose();
            eventSource = source;
            if (source != null)
                inventoryChanged = EventBus.Global.Subscribe(InvChannels.Changed, source, this, ValidateEquipment);
        }
        private void ValidateEquipment()
        {
            BindInventory(); // OnEnable 可能早于库存 Awake；初始化后再订阅正确的容器。
            RestoreSelection();
            if (current == null) return;
            if (!InventoryAvailable)
            {
                ItemId selected = current.ItemId;
                Unequip();
                resumeItemId = selected;
                return;
            }
            if (!inventory.TryGetItem(current.ItemId, out RuntimeItem item)) Unequip();
            else if (!ReferenceEquals(item, current.Item) && !TryEquip(item.ItemId, out _)) Unequip();
        }
        private void RestoreSelection()
        {
            if (!started || !resumeItemId.IsValid || !InventoryAvailable || !isActiveAndEnabled) return;
            ItemId selected = resumeItemId;
            resumeItemId = ItemId.None;
            TryEquip(selected, out _);
        }
        public bool TryEquip(ItemId itemId, out string error)
        {
            error = null;
            if (!isActiveAndEnabled || !InventoryAvailable || inventory.gameObject != gameObject)
                error = "需要本角色已启用且初始化的背包。";
            else if (!itemId.IsValid || !inventory.TryGetItem(itemId, out RuntimeItem item))
                error = "物品不属于本角色背包。";
            else
            {
                EquipmentFragment equipment = item.GetFragment<EquipmentFragment>();
                if (item.GetFragment<WeaponDefinitionFragment>() == null || equipment?.EquipPrefab == null)
                    error = "物品缺少武器定义或装备模型。";
                else if (weaponRig == null || !weaponRig.CanBindEquipment)
                    error = "角色未配置稳定武器挂点与左手目标。";
                else
                {
                    if (current != null && current.ItemId == itemId && ReferenceEquals(current.Item, item)) return true;
                    Weapon weapon;
                    try
                    {
                        // 验证完成后才撤下旧装备；坏候选不会阻断默认选枪循环。
                        Weapon candidate = new Weapon(item);
                        if (!weapons.TryGetValue(itemId, out weapon)) weapon = candidate;
                    }
                    catch (System.ArgumentException exception) { error = exception.Message; return false; }
                    catch (System.InvalidOperationException exception) { error = exception.Message; return false; }
                    if (!weaponRig.TryPrepareEquipment(equipment.EquipPrefab, out GameObject model, out error)) return false;
                    Unequip();
                    weapon.Rebind(item);
                    weapons[itemId] = weapon;
                    current = weapon;
                    weaponRig.BindEquipment(model);
                    BindInventory();
                    return true;
                }
            }
            return false;
        }
        public void Unequip()
        {
            resumeItemId = ItemId.None;
            if (current == null) return;
            current.CancelAction();
            current = null;
            player?.CancelWeaponAction();
            weaponRig?.UnbindEquipment();
        }
        private void OnDisable()
        {
            ItemId selected = current != null ? current.ItemId : resumeItemId;
            inventoryChanged.Dispose();
            eventSource = null;
            Unequip();
            resumeItemId = selected;
        }
    }
}
