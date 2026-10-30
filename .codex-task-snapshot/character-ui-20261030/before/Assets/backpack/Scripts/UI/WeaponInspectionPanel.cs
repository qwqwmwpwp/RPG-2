using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using RPG4.Backpack.Core;
using RPG4.Backpack.Inventory;
using RPG4.Backpack.Items;
using RPG4.UI;

namespace RPG4.Backpack.UI
{
    /// <summary>固定检查目标的只读装配面板；所有业务意图和状态均经过事件总线。</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(30)]
    public sealed class WeaponInspectionPanel : MonoBehaviour
    {
        public UIBase window;
        public BackpackInteractionController interaction;
        public RectTransform panelRect;
        public GameObject contentRoot;
        public TMP_Text title;
        public TMP_Text summary;
        public TMP_Text magazineLabel;
        public TMP_Text muzzleLabel;
        public TMP_Text candidateHeading;
        public TMP_Text status;
        public Image icon;
        public Button magazineButton;
        public Button muzzleButton;
        public Button removeMagazineButton;
        public Button removeMuzzleButton;
        public Transform candidateRoot;
        public WeaponCandidateRow candidatePrefab;
        public Slider loadQuantity;
        public TMP_Text loadQuantityLabel;
        public Button loadButton;
        public GameObject quantityRoot;

        private static int nextRequestId;
        private static int nextSessionId;
        private readonly List<WeaponCandidateRow> rows = new List<WeaponCandidateRow>();
        private EventSubscription inspectionSubscription;
        private EventSubscription resultSubscription;
        private EventSubscription hudSubscription;
        private EventSubscription candidateSubscription;
        private object source;
        private ItemId targetId;
        private int sessionId;
        private int snapshotRequestId;
        private int pendingRequestId;
        private ItemId pendingTargetId;
        private float pendingDeadline;
        private float nextBindingCheck;
        private float nextSnapshotRetry;
        private float messageExpires;
        private string message;
        private bool sessionOpen;
        private bool chooseDefault;
        private bool previousBlocked;
        private int lastInteractionFrame = -1;
        private WeaponInspectionSnapshot snapshot;
        private WeaponOperationKind candidateKind;
        private ItemId selectedAmmoId;
        private Canvas canvas;

        public bool IsFor(UIBase owner) => isActiveAndEnabled && window == owner;
        private bool InventoryBusy => interaction == null || !interaction.IsSessionActive || interaction.IsCarrying
            || interaction.IsChoosingSplitQuantity || interaction.IsWaitingForOperation;
        private bool Blocked => InventoryBusy || pendingRequestId != 0;

        private void Awake()
        {
            if (magazineButton != null) magazineButton.onClick.AddListener(ShowMagazines);
            if (muzzleButton != null) muzzleButton.onClick.AddListener(ShowMuzzles);
            if (removeMagazineButton != null) removeMagazineButton.onClick.AddListener(RemoveMagazine);
            if (removeMuzzleButton != null) removeMuzzleButton.onClick.AddListener(RemoveMuzzle);
            if (loadButton != null) loadButton.onClick.AddListener(LoadMagazine);
            if (loadQuantity != null) loadQuantity.onValueChanged.AddListener(OnQuantityChanged);
            canvas = GetComponentInParent<Canvas>();
            Render();
        }

        private void Update()
        {
            bool open = window != null && window.IsOpen;
            if (!open)
            {
                if (sessionOpen) EndSession();
                return;
            }
            if (!sessionOpen)
            {
                sessionOpen = true;
                nextBindingCheck = 0f;
            }
            if (Time.unscaledTime >= nextBindingCheck)
            {
                nextBindingCheck = Time.unscaledTime + 0.25f;
                IBackpackView inventory = LocalBackpackSourceResolver.Resolve();
                object next = inventory != null && inventory.IsReady ? inventory.EventSource : null;
                if (!ReferenceEquals(next, source)) BindSource(next);
            }
            if (source != null && (chooseDefault || snapshot == null && !targetId.IsNone) && Time.unscaledTime >= nextSnapshotRetry)
            {
                nextSnapshotRetry = Time.unscaledTime + 1f;
                if (chooseDefault) RequestHud();
                else RequestInspection();
            }
            if (pendingRequestId != 0 && Time.unscaledTime >= pendingDeadline)
            {
                pendingRequestId = 0;
                SetMessage("等待超时，库存以同步结果为准");
                RequestInspection();
            }
            bool blocked = Blocked;
            if (blocked != previousBlocked) { previousBlocked = blocked; Render(); }
            RefreshStatus();
        }

        private void OnDisable() => EndSession();
        private void OnDestroy()
        {
            EndSession();
            if (magazineButton != null) magazineButton.onClick.RemoveListener(ShowMagazines);
            if (muzzleButton != null) muzzleButton.onClick.RemoveListener(ShowMuzzles);
            if (removeMagazineButton != null) removeMagazineButton.onClick.RemoveListener(RemoveMagazine);
            if (removeMuzzleButton != null) removeMuzzleButton.onClick.RemoveListener(RemoveMuzzle);
            if (loadButton != null) loadButton.onClick.RemoveListener(LoadMagazine);
            if (loadQuantity != null) loadQuantity.onValueChanged.RemoveListener(OnQuantityChanged);
        }

        private void BindSource(object next)
        {
            UnbindSource();
            source = next;
            if (source == null) { Render(); return; }
            sessionId = ++nextSessionId;
            chooseDefault = true;
            inspectionSubscription = EventBus.Global.Subscribe<WeaponInspectionSnapshot>(InvChannels.WeaponInspectionChanged, source, this, OnInspection);
            resultSubscription = EventBus.Global.Subscribe<WeaponOperationResult>(InvChannels.WeaponOperationCompleted, source, this, OnOperationCompleted);
            hudSubscription = EventBus.Global.Subscribe<WeaponHudSnapshot>(InvChannels.WeaponHudChanged, source, this, OnHud);
            candidateSubscription = EventBus.Global.Subscribe<int>(UiChannels.WeaponCandidateSelected, this, this, OnCandidateSelected);
            nextSnapshotRetry = Time.unscaledTime + 1f;
            RequestHud();
            Render();
        }

        private void UnbindSource()
        {
            object previous = source;
            int previousSession = sessionId;
            source = null;
            inspectionSubscription.Dispose(); resultSubscription.Dispose(); hudSubscription.Dispose(); candidateSubscription.Dispose();
            inspectionSubscription = resultSubscription = hudSubscription = candidateSubscription = default;
            if (previous != null)
                EventBus.Global.Publish(CmdChannels.WeaponSnapshot, previous, new WeaponSnapshotRequest(ItemId.None, previousSession, ++nextRequestId));
            sessionId = snapshotRequestId = pendingRequestId = 0;
            targetId = pendingTargetId = selectedAmmoId = ItemId.None;
            snapshot = null;
            chooseDefault = false;
            message = null;
            messageExpires = 0f;
            lastInteractionFrame = -1;
        }

        private void EndSession()
        {
            UnbindSource();
            sessionOpen = false;
            Render();
        }

        private void RequestHud()
        {
            if (source != null) EventBus.Global.Publish(CmdChannels.WeaponSnapshot, source,
                new WeaponSnapshotRequest(ItemId.None, sessionId, ++nextRequestId, hudOnly: true));
        }

        private void OnHud(WeaponHudSnapshot value)
        {
            if (!sessionOpen || !chooseDefault || source == null) return;
            chooseDefault = false;
            if (value?.Item != null) Inspect(value.Item.ItemId);
            else Render();
        }

        /// <summary>输入路由使用当前鼠标空间查询结果触发检查，不改变原有点击语义。</summary>
        public void Inspect(RuntimeItem item)
        {
            if (item == null || item.WeaponState == null && item.MagazineState == null || InventoryBusy) return;
            Inspect(item.ItemId);
        }

        public void InspectAtPointer(Vector2 point)
        {
            if (Blocked || interaction == null || !interaction.TryGetItemAtPointer(point, out RuntimeItem item, out IBackpackView inventory)) return;
            IBackpackView local = LocalBackpackSourceResolver.Resolve();
            IBackpackView equipment = LocalBackpackSourceResolver.ResolveEquipment(local);
            // 箱子窗口也复用面板，但只检查当前玩家持有的物品。
            if (local == null || !ReferenceEquals(local.EventSource, source)
                || !ReferenceEquals(inventory, local) && !ReferenceEquals(inventory, equipment)) return;
            Inspect(item);
        }

        private void Inspect(ItemId id)
        {
            if (source == null || pendingRequestId != 0 || id.IsNone) return;
            targetId = id;
            snapshot = null;
            selectedAmmoId = ItemId.None;
            chooseDefault = false;
            candidateKind = WeaponOperationKind.ExchangeMagazine;
            RequestInspection();
            Render();
        }

        private void RequestInspection()
        {
            if (source == null || targetId.IsNone) return;
            snapshotRequestId = ++nextRequestId;
            nextSnapshotRetry = Time.unscaledTime + 1f;
            EventBus.Global.Publish(CmdChannels.WeaponSnapshot, source, new WeaponSnapshotRequest(targetId, sessionId, snapshotRequestId));
        }

        private void OnInspection(WeaponInspectionSnapshot value)
        {
            if (!sessionOpen || source == null || value == null || value.SessionId != sessionId
                || value.RequestId != snapshotRequestId || value.InspectedItemId != targetId) return;
            snapshot = value;
            if (value.Item == null)
            {
                targetId = selectedAmmoId = ItemId.None;
                snapshot = null;
                pendingRequestId = 0;
                SetMessage("物品已离开当前玩家，检查已清除");
                // 取消后端对此会话的旧目标追踪；取消回包使用新请求号，不会再进入此分支。
                EventBus.Global.Publish(CmdChannels.WeaponSnapshot, source,
                    new WeaponSnapshotRequest(ItemId.None, sessionId, ++nextRequestId));
            }
            else if (value.IsMagazine) candidateKind = WeaponOperationKind.LoadMagazine;
            Render();
        }

        private void OnOperationCompleted(WeaponOperationResult result)
        {
            if (!sessionOpen || pendingRequestId == 0 || result.RequestId != pendingRequestId || result.TargetItemId != pendingTargetId) return;
            pendingRequestId = 0;
            SetMessage(result.Success ? "操作完成" : string.IsNullOrEmpty(result.Error) ? "操作未完成，请重试" : result.Error);
            // 结果只解除等待；内容始终读取后端后续投影。
            RequestInspection();
            Render();
        }

        private void Submit(WeaponOperationKind kind, ItemId candidate, int count = 0)
        {
            Touch();
            if (Blocked || source == null || snapshot?.Item == null) return;
            pendingRequestId = ++nextRequestId;
            pendingTargetId = targetId;
            pendingDeadline = Time.unscaledTime + 5f;
            WeaponOperation operation = new WeaponOperation(pendingRequestId, kind, targetId, candidate, count);
            Render();
            EventBus.Global.Publish(CmdChannels.WeaponOperation, source, operation);
        }

        private void ShowMagazines() { Touch(); candidateKind = WeaponOperationKind.ExchangeMagazine; selectedAmmoId = ItemId.None; Render(); }
        private void ShowMuzzles() { Touch(); candidateKind = WeaponOperationKind.ExchangeAttachment; selectedAmmoId = ItemId.None; Render(); }
        private void RemoveMagazine() { if (snapshot != null && snapshot.CanRemoveMagazine) Submit(WeaponOperationKind.ExchangeMagazine, ItemId.None); }
        private void RemoveMuzzle() { if (snapshot != null && snapshot.CanRemoveMuzzle) Submit(WeaponOperationKind.ExchangeAttachment, ItemId.None); }

        private void OnCandidateSelected(int index)
        {
            Touch();
            if (Blocked || snapshot == null || index < 0 || index >= snapshot.Candidates.Count) return;
            WeaponUiCandidate candidate = snapshot.Candidates[index];
            if (!candidate.CanApply || candidate.Kind != candidateKind) return;
            if (candidate.Kind != WeaponOperationKind.LoadMagazine) { Submit(candidate.Kind, candidate.Item.ItemId); return; }
            selectedAmmoId = candidate.Item.ItemId;
            if (loadQuantity != null) loadQuantity.SetValueWithoutNotify(1f);
            RenderQuantity();
        }

        private WeaponUiCandidate SelectedAmmo()
        {
            if (snapshot == null || selectedAmmoId.IsNone) return null;
            foreach (WeaponUiCandidate candidate in snapshot.Candidates)
                if (candidate.Kind == WeaponOperationKind.LoadMagazine && candidate.Item.ItemId == selectedAmmoId) return candidate;
            return null;
        }

        private void OnQuantityChanged(float value) { Touch(); RenderQuantity(); }
        private void LoadMagazine()
        {
            WeaponUiCandidate candidate = SelectedAmmo();
            if (candidate == null || !candidate.CanApply || candidate.MaxCount < 1) return;
            int count = loadQuantity != null ? Mathf.Clamp(Mathf.RoundToInt(loadQuantity.value), 1, candidate.MaxCount) : candidate.MaxCount;
            Submit(WeaponOperationKind.LoadMagazine, candidate.Item.ItemId, count);
        }

        private void Render()
        {
            bool hasItem = snapshot?.Item != null;
            if (contentRoot != null && contentRoot != gameObject) contentRoot.SetActive(hasItem);
            if (title != null) title.text = hasItem ? snapshot.Item.Name + (snapshot.IsEquipped ? " · 已装备" : "") : "武器检查";
            if (summary != null) summary.text = !hasItem ? string.Empty : snapshot.IsWeapon
                ? $"弹匣 {snapshot.MagazineRounds}/{snapshot.MagazineCapacity} + 膛内 {snapshot.ChamberCount}\n耐久 {snapshot.Durability:0.#}/{snapshot.MaxDurability:0.#}\n{snapshot.CombatSummary}"
                : $"弹匣 {snapshot.MagazineRounds}/{snapshot.MagazineCapacity} · 选择弹药并指定装填数量";
            if (icon != null) { icon.sprite = hasItem ? snapshot.Item.Icon : null; icon.enabled = icon.sprite != null; icon.preserveAspect = true; }
            if (magazineLabel != null) magazineLabel.text = hasItem ? snapshot.Magazine?.Name ?? "空弹匣槽" : string.Empty;
            if (muzzleLabel != null) muzzleLabel.text = hasItem ? snapshot.Muzzle?.Name ?? "空枪口槽" : string.Empty;
            SetVisible(magazineButton, hasItem && snapshot.IsWeapon);
            SetVisible(muzzleButton, hasItem && snapshot.IsWeapon && snapshot.HasMuzzleSlot);
            SetVisible(removeMagazineButton, hasItem && snapshot.IsWeapon);
            SetVisible(removeMuzzleButton, hasItem && snapshot.IsWeapon && snapshot.HasMuzzleSlot);
            if (magazineButton != null) magazineButton.interactable = hasItem && !Blocked;
            if (muzzleButton != null) muzzleButton.interactable = hasItem && !Blocked;
            if (removeMagazineButton != null) removeMagazineButton.interactable = hasItem && snapshot.CanRemoveMagazine && !Blocked;
            if (removeMuzzleButton != null) removeMuzzleButton.interactable = hasItem && snapshot.CanRemoveMuzzle && !Blocked;
            if (candidateHeading != null) candidateHeading.text = !hasItem ? string.Empty
                : candidateKind == WeaponOperationKind.LoadMagazine ? "兼容弹药 · 点击选择"
                : candidateKind == WeaponOperationKind.ExchangeMagazine ? "弹匣候选 · 点击装配" : "枪口候选 · 点击装配";
            int used = 0;
            if (hasItem && candidateRoot != null && candidatePrefab != null)
            {
                for (int index = 0; index < snapshot.Candidates.Count; index++)
                {
                    WeaponUiCandidate candidate = snapshot.Candidates[index];
                    if (candidate.Kind != candidateKind) continue;
                    if (used == rows.Count) rows.Add(Instantiate(candidatePrefab, candidateRoot));
                    WeaponCandidateRow row = rows[used++];
                    row.gameObject.SetActive(true);
                    row.Bind(this, index, candidate, Blocked);
                }
            }
            for (int i = used; i < rows.Count; i++) rows[i].gameObject.SetActive(false);
            if (hasItem && used == 0 && candidateHeading != null) candidateHeading.text += "\n背包中没有候选物品";
            RenderQuantity();
            RefreshStatus();
        }

        private void RenderQuantity()
        {
            WeaponUiCandidate candidate = SelectedAmmo();
            bool visible = snapshot?.Item != null && snapshot.IsMagazine;
            if (quantityRoot != null) quantityRoot.SetActive(visible);
            int maximum = candidate != null && candidate.CanApply ? Mathf.Max(0, candidate.MaxCount) : 0;
            if (loadQuantity != null)
            {
                loadQuantity.wholeNumbers = true;
                loadQuantity.minValue = 1f;
                loadQuantity.maxValue = Mathf.Max(1, maximum);
                loadQuantity.SetValueWithoutNotify(Mathf.Clamp(loadQuantity.value, 1f, Mathf.Max(1, maximum)));
                loadQuantity.interactable = maximum > 0 && !Blocked;
            }
            if (loadQuantityLabel != null) loadQuantityLabel.text = candidate == null ? "选择上方弹药"
                : maximum == 0 ? candidate.Reason : $"{candidate.Item.Name} · 装填 {(loadQuantity != null ? Mathf.RoundToInt(loadQuantity.value) : maximum)} / {maximum} 发";
            if (loadButton != null) loadButton.interactable = visible && maximum > 0 && !Blocked;
        }

        private void RefreshStatus()
        {
            if (status == null) return;
            status.text = pendingRequestId != 0 ? "正在提交操作，请稍候…"
                : Time.unscaledTime < messageExpires ? message
                : source == null ? "等待本地玩家就绪…"
                : snapshot?.Item == null ? "将鼠标移到自己的枪械或弹匣上，按 F 检查"
                : InventoryBusy ? "请先完成背包拿取、拆分或转移操作"
                : !string.IsNullOrEmpty(snapshot.OperationBlockReason) ? snapshot.OperationBlockReason : "F 检查其他物品 · 点击槽位选择装配";
        }

        private static void SetVisible(Button button, bool visible) { if (button != null) button.gameObject.SetActive(visible); }
        private void SetMessage(string value) { message = value; messageExpires = Time.unscaledTime + 3f; }
        private void Touch() => lastInteractionFrame = Time.frameCount;

        public bool BlocksInventoryPointer(Vector2 screenPoint)
        {
            if (!sessionOpen || window == null || !window.IsOpen) return false;
            if (lastInteractionFrame == Time.frameCount) return true;
            Camera camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            return panelRect != null && RectTransformUtility.RectangleContainsScreenPoint(panelRect, screenPoint, camera);
        }
    }
}
