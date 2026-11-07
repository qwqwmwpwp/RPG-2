using RPG4.Backpack.Items;
using RPG4.Backpack.Core;
using System;

namespace RPG4.Backpack.Inventory
{
    public sealed partial class InventoryHostCore
    {
        // 玩家宿主只解析自己的背包/装备槽；未配置时仅允许本核心根容器。
        public Func<ItemId, InventoryContainer> WeaponContainerResolver { get; set; }
        public Func<ItemId, string> WeaponOperationBlockReason { get; set; }
        public Action<WeaponOperation> ForwardWeaponOperation { get; set; }

        public bool CanExecuteWeaponOperation(WeaponOperation operation, out string error)
        {
            error = null;
            if (!ResolveAuthority() || !EnsureContainer(out error))
            { error = error ?? "仅权威端可修改武器。"; return false; }
            if (!operation.TargetItemId.IsValid) { error = "目标物品标识无效。"; return false; }
            if (operation.Kind == WeaponOperationKind.LoadMagazine)
                return CanLoadMagazine(operation.TargetItemId, operation.CandidateItemId, operation.Count, out error);
            if (operation.Kind != WeaponOperationKind.ExchangeMagazine && operation.Kind != WeaponOperationKind.ExchangeAttachment)
            { error = "未知武器操作。"; return false; }
            if (operation.Kind == WeaponOperationKind.ExchangeAttachment && operation.Slot != WeaponAttachmentSlot.Muzzle)
            { error = "不支持的武器配件槽。"; return false; }
            InventoryContainer target = ResolveAssemblyContainer(operation.TargetItemId);
            if (target == null || !target.TryGetItemById(operation.TargetItemId, out RuntimeItem weapon) || weapon.WeaponState == null)
            { error = "目标武器已不在玩家可操作容器内。"; return false; }
            AssemblyOperationResult result = EvaluateAssembly(ToAssemblyOperation(operation));
            error = result.Error;
            return result.Success;
        }

        private static AssemblyOperation ToAssemblyOperation(WeaponOperation operation)
            => new AssemblyOperation(operation.RequestId, operation.TargetItemId, operation.TargetItemId,
                operation.Kind == WeaponOperationKind.ExchangeMagazine ? ItemAssemblySlots.Magazine : ItemAssemblySlots.Muzzle,
                operation.CandidateItemId);

        public WeaponOperationResult ExecuteWeaponOperation(WeaponOperation operation)
        {
            using (InventoryTransactions.DeferPublication()) return ExecuteWeaponOperationCore(operation);
        }

        private WeaponOperationResult ExecuteWeaponOperationCore(WeaponOperation operation)
        {
            if (!CanExecuteWeaponOperation(operation, out string error))
                return new WeaponOperationResult(operation.RequestId, operation.TargetItemId, false, error);
            bool success;
            if (operation.Kind == WeaponOperationKind.LoadMagazine)
                success = TryLoadMagazine(operation.TargetItemId, operation.CandidateItemId, operation.Count, out error);
            else
            {
                AssemblyOperationResult result = ExecuteAssembly(ToAssemblyOperation(operation));
                success = result.Success; error = result.Error;
            }
            return new WeaponOperationResult(operation.RequestId, operation.TargetItemId, success, success ? null : error);
        }

        public void PublishWeaponOperationResult(WeaponOperationResult result)
        {
            if (commandSource != null) InventoryTransactions.RunPostCommit(() => EventBus.Global.Publish(InvChannels.WeaponOperationCompleted, commandSource, result), "武器结果通知失败");
        }

        private void OnWeaponOperationCommand(WeaponOperation operation)
        {
            if (ResolveAuthority())
            {
                if (AuthorityWeaponOperationDispatcher != null) AuthorityWeaponOperationDispatcher(operation);
                else PublishWeaponOperationResult(ExecuteWeaponOperation(operation));
            }
            else if (ForwardWeaponOperation != null) ForwardWeaponOperation(operation);
            else PublishWeaponOperationResult(new WeaponOperationResult(operation.RequestId, operation.TargetItemId,
                false, "此库存当前不支持武器操作。"));
        }

        public bool CanExchangeWeaponMagazine(InventoryContainer weaponContainer, ItemId weaponId, ItemId magazineId, out string error)
            => CanExchangeWeaponChild(weaponContainer, weaponId, magazineId, true, default, out error);
        public bool TryExchangeWeaponMagazine(InventoryContainer weaponContainer, ItemId weaponId, ItemId magazineId, out string error)
            => TryExchangeWeaponChild(weaponContainer, weaponId, magazineId, true, default, out error);
        public bool CanExchangeWeaponAttachment(InventoryContainer weaponContainer, ItemId weaponId, WeaponAttachmentSlot slot, ItemId attachmentId, out string error)
            => CanExchangeWeaponChild(weaponContainer, weaponId, attachmentId, false, slot, out error);
        public bool TryExchangeWeaponAttachment(InventoryContainer weaponContainer, ItemId weaponId, WeaponAttachmentSlot slot, ItemId attachmentId, out string error)
            => TryExchangeWeaponChild(weaponContainer, weaponId, attachmentId, false, slot, out error);

        private bool CanExchangeWeaponChild(InventoryContainer weaponContainer, ItemId weaponId, ItemId childId,
            bool magazine, WeaponAttachmentSlot slot, out string error)
        {
            if (!ReferenceEquals(weaponContainer, ResolveAssemblyContainer(weaponId)))
            { error = "目标不属于宿主可操作容器。"; return false; }
            return CanExecuteWeaponOperation(new WeaponOperation(0, magazine ? WeaponOperationKind.ExchangeMagazine
                : WeaponOperationKind.ExchangeAttachment, weaponId, childId, 0, slot), out error);
        }

        private bool TryExchangeWeaponChild(InventoryContainer weaponContainer, ItemId weaponId, ItemId childId,
            bool magazine, WeaponAttachmentSlot slot, out string error)
        {
            if (!ReferenceEquals(weaponContainer, ResolveAssemblyContainer(weaponId)))
            { error = "目标不属于宿主可操作容器。"; return false; }
            WeaponOperationResult result = ExecuteWeaponOperation(new WeaponOperation(0, magazine ? WeaponOperationKind.ExchangeMagazine
                : WeaponOperationKind.ExchangeAttachment, weaponId, childId, 0, slot));
            error = result.Error; return result.Success;
        }

        public bool CanLoadMagazine(ItemId magazineId, ItemId ammoId, int roundCount, out string error)
        {
            if (!ResolveAuthority()) { error = "仅权威端可装填弹匣。"; return false; }
            if (!TryResolveItem(magazineId, out RuntimeItem magazine, out error) || !TryResolveItem(ammoId, out RuntimeItem ammo, out error)) return false;
            return Container.CanLoadMagazine(magazine, ammo, roundCount, Catalog, out error);
        }

        public bool TryLoadMagazine(ItemId magazineId, ItemId ammoId, int roundCount, out string error)
        {
            if (!CanLoadMagazine(magazineId, ammoId, roundCount, out error)) return false;
            Container.TryGetItemById(magazineId, out RuntimeItem magazine);
            Container.TryGetItemById(ammoId, out RuntimeItem ammo);
            return Container.TryLoadMagazine(magazine, ammo, roundCount, Catalog, CommitParticipant, out error);
        }
    }
}
