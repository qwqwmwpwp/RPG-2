using System.Collections.Generic;
using RPG4.Backpack.Core;
using RPG4.Backpack.Equipment;
using RPG4.LoadoutContracts;
using RPG4.Backpack.Items;
using RPG4.LoadoutTemplate;

namespace RPG4.PlayerMovement
{
    public sealed partial class PlayerEquipment
    {
        private object weaponUiSource;
        private EventSubscription weaponUiRequest, weaponUiActionChanged, weaponOperationRequest;
        private NetworkRoleLoadout weaponOperationNetwork;
        private int highestLocalWeaponRequest;
        private readonly Dictionary<int, (WeaponOperation operation, WeaponOperationResult result)> localWeaponReceipts
            = new Dictionary<int, (WeaponOperation operation, WeaponOperationResult result)>();
        public event System.Action<WeaponOperationResult> WeaponOperationCompleted;
        private readonly List<RuntimeItem> weaponUiItems = new List<RuntimeItem>();
        private bool weaponUiDirty, weaponHudRequested, hudReloading;
        private ulong hudReloadWeapon;
        private float hudReloadDuration, hudReloadPhase;
        private RuntimeItem hudItem;
        private int hudItemRevision;
        private WeaponHudSnapshot cachedHud, publishedHud;
        private Weapon weaponUiObservedAction;
        private bool weaponUiWasReloading;
        private WeaponUiActionStage weaponUiActionStage;
        public object WeaponUiEventSource => this;
        public double SimulationTime => player != null ? player.SimulationTime : UnityEngine.Time.timeAsDouble;
        public event System.Action<WeaponHudSnapshot> WeaponHudChanged;
        private void BindWeaponUiEvents()
        {
            if (weaponUiSource != null || role == null) return;
            weaponUiSource = this;
            weaponUiRequest = EventBus.Global.Subscribe<WeaponSnapshotRequest>(CmdChannels.WeaponSnapshot,
                weaponUiSource, this, _ =>
                {
                    weaponHudRequested = true;
                    PublishWeaponHud(CaptureWeaponHud());
                });
            weaponOperationRequest = EventBus.Global.Subscribe<WeaponOperation>(CmdChannels.WeaponOperation,
                weaponUiSource, this, operation => RequestWeaponOperation(operation));
            weaponOperationNetwork = role.GetComponentInParent<NetworkRoleLoadout>(true);
            if (weaponOperationNetwork != null)
            {
                weaponOperationNetwork.BindRuntimeOperations(CanOperateWeaponItems, PrepareWeaponOperation);
                weaponOperationNetwork.RuntimeOperationCompleted += OnRuntimeOperationCompleted;
            }
            BindWeaponUiAction(); weaponUiDirty = true;
        }
        private bool CanOperateWeaponItems() => CanExecuteAuthority && RuntimeAvailable && player.IsAlive
            && role != null && (weaponOperationNetwork == null || weaponOperationNetwork.Role == role);
        /// <summary>新UI通过此控制器或 WeaponUiEventSource 的命令事件提交纯意图，不能上传物品状态。</summary>
        public bool RequestWeaponOperation(WeaponOperation operation)
        {
            if (weaponOperationNetwork != null) return weaponOperationNetwork.RequestWeaponOperation(operation);
            bool success = false; string error = null;
            if (localWeaponReceipts.TryGetValue(operation.RequestId, out var receipt))
            {
                WeaponOperationResult result = receipt.operation.Equals(operation) ? receipt.result
                    : new WeaponOperationResult(operation.RequestId, operation.TargetItemId, false, "重复请求编号对应不同操作。");
                PublishWeaponOperationResult(result); return result.Success;
            }
            if (!LoadoutWeaponOperations.Validate(operation, out error)
                || !LoadoutWeaponOperations.ValidateOwnership(role, operation, out error)) { }
            else if (operation.RequestId <= highestLocalWeaponRequest) error = "旧武器请求编号已过期。";
            else
            {
                highestLocalWeaponRequest = operation.RequestId;
                if (!CanOperateWeaponItems()) error = "角色当前不能操作武器物品。";
                else
                {
                    PrepareWeaponOperation(operation);
                    if (!CanOperateWeaponItems()) error = "操作准备期间角色状态已改变。";
                    else success = LoadoutWeaponOperations.TryApply(role, operation, out error);
                }
                if (localWeaponReceipts.Count >= 128) localWeaponReceipts.Clear();
                localWeaponReceipts[operation.RequestId] = (operation,
                    new WeaponOperationResult(operation.RequestId, operation.TargetItemId, success, error));
            }
            PublishWeaponOperationResult(new WeaponOperationResult(operation.RequestId, operation.TargetItemId, success, error));
            return success;
        }
        private void PublishWeaponOperationResult(WeaponOperationResult result)
        {
            MarkWeaponUiDirty();
            EventBus.Global.Publish(InvChannels.WeaponOperationResult, WeaponUiEventSource, result);
            if (WeaponOperationCompleted == null) return;
            foreach (System.Action<WeaponOperationResult> callback in WeaponOperationCompleted.GetInvocationList())
                try { callback(result); } catch (System.Exception exception) { UnityEngine.Debug.LogException(exception, this); }
        }
        private void OnRuntimeOperationCompleted(WeaponOperation operation, LoadoutOperationOutcome outcome)
        {
            PublishWeaponOperationResult(new WeaponOperationResult(operation.RequestId, operation.TargetItemId,
                outcome.Success, outcome.Message));
        }
        public WeaponHudSnapshot CaptureWeaponHud()
        {
            var frame = player?.PublishedFrame;
            bool reloading = current == null && frame != null && frame.Reloading;
            ulong weapon = reloading ? frame.WeaponId : 0;
            float duration = reloading ? frame.ReloadDuration : 0f;
            float phase = reloading ? frame.ReloadPhase : 0f;
            bool actionChanged = reloading != hudReloading || weapon != hudReloadWeapon
                || duration != hudReloadDuration || phase < hudReloadPhase;
            hudReloadPhase = phase;
            if (cachedHud != null && !weaponUiDirty && !actionChanged) return cachedHud;
            hudReloading = reloading;
            hudReloadWeapon = weapon;
            hudReloadDuration = duration;
            weaponUiDirty = false;
            cachedHud = BuildWeaponHud(PresentationItem, reloading, reloading ? frame.Time - phase * duration : 0d);
            return cachedHud;
        }

        private WeaponHudSnapshot BuildWeaponHud(RuntimeItem item, bool replicatedReload, double replicatedStart)
        {
            ItemManifestCatalog catalog = role?.ManifestCatalog;
            WeaponItemState state = item?.WeaponState;
            MagazineItemState magazine = state?.Magazine?.MagazineState;
            int spareMagazines = 0, looseAmmo = 0;
            if (state != null && role != null)
            {
                role.CopyOwnedRuntimeItems(weaponUiItems);
                foreach (RuntimeItem candidate in weaponUiItems)
                {
                    if (candidate.ParentItem != null) continue;
                    if (candidate.MagazineState?.RoundCount > 0 && state.IsCompatibleMagazine(candidate, catalog)) spareMagazines++;
                    AmmoDefinitionFragment ammo = candidate.GetFragment<AmmoDefinitionFragment>();
                    if (ammo != null && candidate.WeaponState == null && candidate.MagazineState == null
                        && ammo.AmmoType.MatchesTagExact(state.Definition.AmmoType)) looseAmmo += candidate.StackCount;
                }
            }
            bool hasAction = current != null && item != null && current.ItemId == item.ItemId;
            double start = hasAction ? current.ReloadStartTime : replicatedStart;
            float duration = hasAction ? current.Definition.ReloadDuration : replicatedReload ? hudReloadDuration : 0f;
            return new WeaponHudSnapshot(WeaponUiSnapshots.Describe(item, catalog),
                state == null || state.ChamberAmmo.IsEmpty ? 0 : 1, magazine?.RoundCount ?? 0, magazine?.Capacity ?? 0,
                state?.Durability ?? 0f, state?.Definition.MaxDurability ?? 0f,
                hasAction ? weaponUiActionStage : replicatedReload ? WeaponUiActionStage.Started : WeaponUiActionStage.None,
                start, duration, spareMagazines, looseAmmo,
                state == null ? null : state.Definition.FireMode == WeaponFireMode.Automatic ? "全自动" : "半自动",
                WeaponHudStatus(state, catalog));
        }
        private void PublishWeaponHud(WeaponHudSnapshot snapshot)
        {
            publishedHud = snapshot;
            if (weaponUiSource != null) EventBus.Global.Publish(InvChannels.WeaponHudChanged, weaponUiSource, snapshot);
            WeaponHudChanged?.Invoke(snapshot);
        }
        private static string WeaponHudStatus(WeaponItemState state, ItemManifestCatalog catalog)
        {
            if (state == null) return null;
            if (state.Durability <= 0f) return "武器已损坏";
            if (state.Durability < state.Definition.DurabilityPerShot) return "耐久不足，无法开火";
            if (state.ChamberAmmo.IsEmpty)
                return state.Magazine?.MagazineState.RoundCount > 0 ? "未上膛" : state.Magazine == null ? "未安装弹匣" : "弹药耗尽";
            return state.CanConsumeShot(catalog) ? null : "当前弹药不可用";
        }
        private void ObserveHudItem(RuntimeItem item)
        {
            int revision = item?.Revision ?? 0;
            if (ReferenceEquals(hudItem, item) && hudItemRevision == revision) return;
            hudItem = item; hudItemRevision = revision;
            MarkWeaponUiDirty();
        }
        private void MarkWeaponUiDirty() => weaponUiDirty = true;
        private void BindWeaponUiAction()
        {
            if (ReferenceEquals(weaponUiObservedAction, current)) return;
            weaponUiActionChanged.Dispose(); weaponUiActionChanged = default;
            weaponUiObservedAction = current;
            weaponUiWasReloading = current != null && current.IsReloading;
            weaponUiActionStage = current == null ? WeaponUiActionStage.None : (WeaponUiActionStage)current.ReloadStage;
            if (current != null) weaponUiActionChanged = EventBus.Global.Subscribe(InvChannels.WeaponActionChanged,
                current, this, OnWeaponUiActionChanged);
            weaponUiDirty = true;
        }
        private void OnWeaponUiActionChanged()
        {
            Weapon weapon = weaponUiObservedAction;
            if (weapon == null) return;
            weaponUiActionStage = weaponUiWasReloading && !weapon.IsReloading && weapon.ReloadStage == WeaponReloadStage.None
                ? WeaponUiActionStage.Cancelled : (WeaponUiActionStage)weapon.ReloadStage;
            weaponUiWasReloading = weapon.IsReloading; weaponUiDirty = true;
        }
        private void FlushWeaponUi()
        {
            if (WeaponHudChanged == null && !weaponHudRequested && player?.LocalLoadout.Available != true) return;
            WeaponHudSnapshot next = CaptureWeaponHud();
            if (!ReferenceEquals(publishedHud, next)) PublishWeaponHud(next);
        }
        private void UnbindWeaponUiEvents()
        {
            weaponUiRequest.Dispose(); weaponUiActionChanged.Dispose(); weaponOperationRequest.Dispose();
            if (weaponOperationNetwork != null)
            {
                weaponOperationNetwork.UnbindRuntimeOperations(CanOperateWeaponItems, PrepareWeaponOperation);
                weaponOperationNetwork.RuntimeOperationCompleted -= OnRuntimeOperationCompleted;
            }
            weaponOperationNetwork = null; localWeaponReceipts.Clear(); highestLocalWeaponRequest = 0;
            weaponUiSource = null; weaponUiObservedAction = null; weaponUiDirty = false;
            hudItem = null; cachedHud = publishedHud = null; weaponHudRequested = false;
            WeaponHudChanged?.Invoke(new WeaponHudSnapshot(null, 0, 0, 0, 0f, 0f, WeaponUiActionStage.None, 0d, 0f));
        }
    }
}
