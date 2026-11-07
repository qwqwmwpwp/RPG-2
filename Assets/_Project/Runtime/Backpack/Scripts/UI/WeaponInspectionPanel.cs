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
        [Header("通用装配")]
        public Transform slotRoot;
        public AssemblySlotRow slotPrefab;
        public Transform assemblyCandidateRoot;
        public Button assemblyInstallButton;
        public Button assemblyPreviousPageButton;
        public Button assemblyNextPageButton;
        public TMP_Text assemblyPageLabel;
        public TMP_Text assemblyCurrentSummary;
        public TMP_Text assemblyPreviewSummary;
        public AssemblyPreviewViewport assemblyPreview;
        public ScrollRect slotScrollRect;
        public ScrollRect candidateScrollRect;
        [SerializeField] private bool allowSessionInventories;
        [SerializeField] private CanvasGroup presentationGroup;
        public bool IsVisible => presentationGroup == null || presentationGroup.alpha > 0f;
        public ItemId InspectedItemId => targetId;

        public void SetPresentationVisible(bool value)
        {
            if (presentationGroup == null) return;
            presentationGroup.alpha = value ? 1f : 0f;
            presentationGroup.interactable = value;
            presentationGroup.blocksRaycasts = value;
            assemblyPreview?.SetVisible(value && assemblyMode);
            assemblyPreview?.SetAssemblyPreviewVisible(value && assemblyMode);
        }

        private static int nextRequestId;
        private static int nextSessionId;
        private readonly List<WeaponCandidateRow> rows = new List<WeaponCandidateRow>();
        private readonly List<AssemblySlotRow> slotRows = new List<AssemblySlotRow>();
        private readonly List<WeaponCandidateRow> assemblyRows = new List<WeaponCandidateRow>();
        private EventSubscription inspectionSubscription;
        private EventSubscription resultSubscription;
        private EventSubscription hudSubscription;
        private EventSubscription candidateSubscription;
        private EventSubscription assemblySubscription;
        private EventSubscription assemblyResultSubscription;
        private EventSubscription changedSubscription;
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
        private AssemblyInspectionSnapshot assemblySnapshot;
        private ItemId assemblyRootId;
        private ItemId selectedAssemblyCandidate;
        private ItemId pendingAssemblyRoot, pendingAssemblyParent;
        private string assemblySlotId, pendingAssemblySlot;
        private int assemblyRequestId, assemblyOffset, pendingAssemblyRequestId;
        private AssemblyCandidateSnapshot selectedAssemblySnapshot;
        private bool assemblyMode;
        private bool assemblyQueryPending;
        private bool assemblyDirty;
        private float assemblyQueryDeadline;
        private float nextAssemblyRetry;
        private ulong assemblyQueryFingerprint;
        private ulong assemblyFingerprint;
        private IBackpackView previewInventory;
        private object requestedSource;

        public bool IsFor(UIBase owner) => isActiveAndEnabled && window == owner && IsVisible;
        private bool InventoryBusy => interaction == null || !interaction.IsSessionActive || interaction.IsCarrying
            || interaction.IsChoosingSplitQuantity || interaction.IsWaitingForOperation;
        private bool Blocked => InventoryBusy || pendingRequestId != 0 || pendingAssemblyRequestId != 0 || assemblyQueryPending;

        private void Awake()
        {
            if (magazineButton != null) magazineButton.onClick.AddListener(ShowMagazines);
            if (muzzleButton != null) muzzleButton.onClick.AddListener(ShowMuzzles);
            if (removeMagazineButton != null) removeMagazineButton.onClick.AddListener(RemoveMagazine);
            if (removeMuzzleButton != null) removeMuzzleButton.onClick.AddListener(RemoveMuzzle);
            if (loadButton != null) loadButton.onClick.AddListener(LoadMagazine);
            if (loadQuantity != null) loadQuantity.onValueChanged.AddListener(OnQuantityChanged);
            if (assemblyInstallButton != null) assemblyInstallButton.onClick.AddListener(InstallAssemblyCandidate);
            if (assemblyPreviousPageButton != null) assemblyPreviousPageButton.onClick.AddListener(PreviousAssemblyPage);
            if (assemblyNextPageButton != null) assemblyNextPageButton.onClick.AddListener(NextAssemblyPage);
            canvas = GetComponentInParent<Canvas>();
            ConfigureText(title, true); ConfigureText(candidateHeading, true); ConfigureText(status, false);
            ConfigureText(summary, false); ConfigureText(magazineLabel, true); ConfigureText(muzzleLabel, true);
            ConfigureText(assemblyCurrentSummary, false); ConfigureText(assemblyPreviewSummary, false);
            Render();
        }

        private static void ConfigureText(TMP_Text label, bool singleLine)
        {
            if (label == null) return;
            label.enableWordWrapping = !singleLine;
            label.overflowMode = singleLine ? TextOverflowModes.Ellipsis : TextOverflowModes.Truncate;
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
                object next = requestedSource ?? (inventory != null && inventory.IsReady ? inventory.EventSource : null);
                if (previewInventory == null && inventory != null && ReferenceEquals(inventory.EventSource, next)) previewInventory = inventory;
                if (previewInventory == null && inventory != null)
                {
                    IBackpackView equipmentView = LocalBackpackSourceResolver.ResolveEquipment(inventory);
                    if (equipmentView != null && ReferenceEquals(equipmentView.EventSource, next)) previewInventory = equipmentView;
                }
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
            if (pendingAssemblyRequestId != 0 && Time.unscaledTime >= pendingDeadline)
            {
                pendingAssemblyRequestId = 0;
                SetMessage("装配操作等待超时，正在同步状态；可重新尝试");
                RequestAssembly();
            }
            if (assemblyMode && assemblyQueryPending && Time.unscaledTime >= assemblyQueryDeadline
                && Time.unscaledTime >= nextAssemblyRetry)
            {
                assemblyQueryPending = false;
                SetMessage("装配读取超时，正在重试…");
                RequestAssembly();
            }
            bool blocked = Blocked;
            if (blocked != previousBlocked) { previousBlocked = blocked; Render(); }
            if (assemblyMode) RefreshAssemblyStatus(); else RefreshStatus();
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
            if (assemblyInstallButton != null) assemblyInstallButton.onClick.RemoveListener(InstallAssemblyCandidate);
            if (assemblyPreviousPageButton != null) assemblyPreviousPageButton.onClick.RemoveListener(PreviousAssemblyPage);
            if (assemblyNextPageButton != null) assemblyNextPageButton.onClick.RemoveListener(NextAssemblyPage);
        }

        private void BindSource(object next)
        {
            object requested = requestedSource;
            IBackpackView preferredView = previewInventory;
            UnbindSource();
            source = next;
            requestedSource = requested;
            if (preferredView != null && ReferenceEquals(preferredView.EventSource, next)) previewInventory = preferredView;
            else
            {
                IBackpackView local = LocalBackpackSourceResolver.Resolve();
                if (local != null && ReferenceEquals(local.EventSource, next)) previewInventory = local;
                else if (local != null)
                {
                    IBackpackView gear = LocalBackpackSourceResolver.ResolveEquipment(local);
                    if (gear != null && ReferenceEquals(gear.EventSource, next)) previewInventory = gear;
                }
            }
            if (source == null) { Render(); return; }
            sessionId = ++nextSessionId;
            chooseDefault = true;
            inspectionSubscription = EventBus.Global.Subscribe<WeaponInspectionSnapshot>(InvChannels.WeaponInspectionChanged, source, this, OnInspection);
            resultSubscription = EventBus.Global.Subscribe<WeaponOperationResult>(InvChannels.WeaponOperationCompleted, source, this, OnOperationCompleted);
            hudSubscription = EventBus.Global.Subscribe<WeaponHudSnapshot>(InvChannels.WeaponHudChanged, source, this, OnHud);
            candidateSubscription = EventBus.Global.Subscribe<int>(UiChannels.WeaponCandidateSelected, this, this, OnCandidateSelected);
            assemblySubscription = EventBus.Global.Subscribe<AssemblyInspectionSnapshot>(InvChannels.AssemblyInspectionChanged, source, this, OnAssemblyInspection);
            assemblyResultSubscription = EventBus.Global.Subscribe<AssemblyOperationResult>(InvChannels.AssemblyOperationCompleted, source, this, OnAssemblyOperationCompleted);
            changedSubscription = EventBus.Global.Subscribe(InvChannels.Changed, source, this, OnInventoryChanged);
            nextSnapshotRetry = Time.unscaledTime + 1f;
            RequestHud();
            Render();
        }

        private void UnbindSource()
        {
            object previous = source;
            int previousSession = sessionId;
            source = null;
            requestedSource = null; previewInventory = null;
            inspectionSubscription.Dispose(); resultSubscription.Dispose(); hudSubscription.Dispose(); candidateSubscription.Dispose();
            assemblySubscription.Dispose(); assemblyResultSubscription.Dispose();
            changedSubscription.Dispose();
            inspectionSubscription = resultSubscription = hudSubscription = candidateSubscription = assemblySubscription = assemblyResultSubscription = default;
            changedSubscription = default;
            if (previous != null)
            {
                EventBus.Global.Publish(CmdChannels.WeaponSnapshot, previous, new WeaponSnapshotRequest(ItemId.None, previousSession, ++nextRequestId));
                EventBus.Global.Publish(CmdChannels.AssemblyQuery, previous, new AssemblyQuery(ItemId.None, sessionId: previousSession, requestId: ++nextRequestId));
            }
            sessionId = snapshotRequestId = pendingRequestId = 0;
            targetId = pendingTargetId = selectedAmmoId = ItemId.None;
            snapshot = null;
            assemblySnapshot = null; assemblyRootId = ItemId.None; selectedAssemblyCandidate = ItemId.None;
            pendingAssemblyRequestId = assemblyRequestId = 0; assemblySlotId = null; assemblyMode = false;
            assemblyQueryPending = assemblyDirty = false; assemblyQueryDeadline = nextAssemblyRetry = 0f; assemblyFingerprint = assemblyQueryFingerprint = 0;
            if (assemblyPreview != null) { assemblyPreview.Clear(); assemblyPreview.SetVisible(false); }
            assemblyPreview?.SetAssemblyPreviewVisible(false);
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
            if (value?.Item != null) InspectHudItem(value.Item.ItemId);
            else Render();
        }

        private void InspectHudItem(ItemId id)
        {
            IBackpackView local = LocalBackpackSourceResolver.Resolve();
            IBackpackView gear = LocalBackpackSourceResolver.ResolveEquipment(local);
            RuntimeItem item = FindRuntimeItem(local, id);
            IBackpackView ownerView = local;
            if (item == null) { item = FindRuntimeItem(gear, id); ownerView = gear; }
            if (item != null && item.Assembly.Slots.Count > 0)
            {
                previewInventory = ownerView;
                requestedSource = ownerView?.EventSource;
                if (!ReferenceEquals(source, requestedSource)) BindSource(requestedSource);
                InspectAssembly(item);
            }
            else Inspect(id);
        }

        private static RuntimeItem FindRuntimeItem(IBackpackView view, ItemId id)
        {
            if (view == null || !view.IsReady || id.IsNone) return null;
            var items = new List<RuntimeItem>(); view.CopyItems(items);
            foreach (RuntimeItem root in items)
                if (ItemAssemblyTraversal.TryFind(root, id, out RuntimeItem item)) return item;
            return null;
        }

        /// <summary>输入路由使用当前鼠标空间查询结果触发检查，不改变原有点击语义。</summary>
        public void Inspect(RuntimeItem item)
        {
            if (item == null || item.WeaponState == null && item.MagazineState == null && item.Assembly.Slots.Count == 0 || InventoryBusy) return;
            if (item.Assembly.Slots.Count > 0) { InspectAssembly(item); return; }
            Inspect(item.ItemId);
        }

        private void InspectAssembly(RuntimeItem item)
        {
            if (source == null || pendingAssemblyRequestId != 0) return;
            if (!assemblyRootId.IsNone && assemblyRootId != item.ItemId)
                EventBus.Global.Publish(CmdChannels.AssemblyQuery, source, new AssemblyQuery(ItemId.None, sessionId: sessionId, requestId: ++nextRequestId));
            EventBus.Global.Publish(CmdChannels.WeaponSnapshot, source, new WeaponSnapshotRequest(ItemId.None, sessionId, ++nextRequestId));
            assemblyMode = true; targetId = ItemId.None; snapshot = null; chooseDefault = false;
            assemblyRootId = item.ItemId; assemblyParentId = item.ItemId; assemblySlotId = null; assemblyOffset = 0; selectedAssemblyCandidate = ItemId.None;
            assemblySnapshot = null;
            assemblyDirty = false; assemblyFingerprint = ComputeAssemblyFingerprint();
            if (assemblyPreview != null) assemblyPreview.Bind(previewInventory ?? LocalBackpackSourceResolver.Resolve(), assemblyRootId);
            assemblyPreview?.SetVisible(IsVisible);
            assemblyPreview?.SetAssemblyPreviewVisible(IsVisible);
            RequestAssembly(); Render();
        }

        private void RequestAssembly()
        {
            if (source == null || assemblyRootId.IsNone) return;
            selectedAssemblyCandidate = ItemId.None;
            selectedAssemblySnapshot = null;
            assemblyPreview?.ClearCandidatePreview();
            assemblyRequestId = ++nextRequestId;
            assemblyQueryPending = true;
            assemblyQueryFingerprint = ComputeAssemblyFingerprint();
            assemblyQueryDeadline = Time.unscaledTime + 3f;
            nextAssemblyRetry = Time.unscaledTime + 2.5f;
            assemblyDirty = false;
            Render();
            EventBus.Global.Publish(CmdChannels.AssemblyQuery, source, new AssemblyQuery(assemblyRootId,
                assemblyParentId, assemblySlotId, sessionId, assemblyRequestId, assemblyOffset, 64));
        }

        private void OnInventoryChanged()
        {
            if (!assemblyMode || previewInventory == null || assemblyRootId.IsNone) return;
            ulong current = ComputeAssemblyFingerprint();
            assemblyPreview?.Bind(previewInventory, assemblyRootId);
            if (current == assemblyFingerprint) return;
            assemblyFingerprint = current;
            selectedAssemblyCandidate = ItemId.None; selectedAssemblySnapshot = null;
            assemblyPreview?.ClearCandidatePreview();
            if (assemblyQueryPending || pendingAssemblyRequestId != 0) { assemblyDirty = true; return; }
            assemblyOffset = 0;
            RequestAssembly();
        }

        private ulong ComputeAssemblyFingerprint()
        {
            unchecked
            {
                const ulong offset = 14695981039346656037UL, prime = 1099511628211UL;
                ulong hash = offset;
                void Mix(ulong value) { hash ^= value; hash *= prime; }
                void MixString(string value)
                {
                    if (value == null) { Mix(0); return; }
                    Mix((ulong)value.Length);
                    for (int i = 0; i < value.Length; i++) Mix(value[i]);
                }
                void MixTree(RuntimeItem item, int depth)
                {
                    if (item == null || depth > 8) { Mix(0); return; }
                    Mix(item.ItemId.Value); Mix((ulong)item.StackCount); Mix((ulong)item.StructureRevision);
                    foreach (ItemSlotDefinition slot in item.Assembly.Slots)
                    {
                        MixString(slot.SlotId);
                        RuntimeItem child = item.Assembly.GetChild(slot.SlotId);
                        Mix(child == null ? 0UL : child.ItemId.Value);
                        if (child != null) MixTree(child, depth + 1);
                    }
                }
                if (previewInventory == null || !previewInventory.IsReady) return 0;
                var items = new List<RuntimeItem>(); previewInventory.CopyItems(items);
                foreach (RuntimeItem item in items) MixTree(item, 0);
                return hash;
            }
        }

        private ItemId assemblyParentId;
        public void SelectAssemblySlot(AssemblySlotSnapshot slot)
        {
            if (InventoryBusy || pendingRequestId != 0 || pendingAssemblyRequestId != 0 || slot == null || !assemblyMode) return;
            assemblyParentId = slot.ParentItemId; assemblySlotId = slot.SlotId; assemblyOffset = 0;
            selectedAssemblyCandidate = ItemId.None; selectedAssemblySnapshot = null;
            assemblyPreview?.FocusSlot(assemblyParentId, assemblySlotId);
            assemblyPreview?.ClearCandidatePreview();
            if (slotScrollRect != null) slotScrollRect.verticalNormalizedPosition = 1f;
            if (candidateScrollRect != null) candidateScrollRect.verticalNormalizedPosition = 1f;
            RequestAssembly(); Render();
        }
        public void RemoveAssemblySlot(AssemblySlotSnapshot slot)
        {
            if (Blocked || slot == null || !slot.CanRemove || slot.InstalledItem == null || assemblySnapshot == null) return;
            SubmitAssembly(slot.ParentItemId, slot.SlotId, ItemId.None);
        }
        public void SelectAssemblyCandidate(int index)
        {
            Touch();
            if (Blocked || assemblySnapshot == null || index < 0 || index >= assemblySnapshot.Candidates.Count) return;
            assemblyPreview?.ClearCandidatePreview();
            selectedAssemblySnapshot = assemblySnapshot.Candidates[index];
            selectedAssemblyCandidate = selectedAssemblySnapshot.Item.ItemId;
            if (selectedAssemblySnapshot.CanApply)
            {
                IBackpackView candidateInventory = ResolveAssemblyCandidateInventory(selectedAssemblyCandidate);
                if (candidateInventory != null)
                    assemblyPreview?.PreviewCandidate(candidateInventory, selectedAssemblyCandidate, assemblyParentId, assemblySlotId);
                else
                    SetMessage("候选物品尚未在当前玩家库存中同步");
            }
            else
            {
                assemblyPreview?.FocusSlot(selectedAssemblySnapshot.ConflictItemId, selectedAssemblySnapshot.ConflictSlotId);
                if (!string.IsNullOrEmpty(selectedAssemblySnapshot.Reason)) SetMessage(selectedAssemblySnapshot.Reason);
            }
            Render();
        }

        private IBackpackView ResolveAssemblyCandidateInventory(ItemId candidateId)
        {
            if (candidateId.IsNone) return null;
            if (FindRuntimeItem(previewInventory, candidateId) != null) return previewInventory;
            IBackpackView local = LocalBackpackSourceResolver.Resolve();
            if (FindRuntimeItem(local, candidateId) != null) return local;
            IBackpackView equipment = LocalBackpackSourceResolver.ResolveEquipment(local);
            if (FindRuntimeItem(equipment, candidateId) != null) return equipment;
            if (allowSessionInventories && interaction != null
                && interaction.TryFindInspectableSessionItem(candidateId, out _, out IBackpackView sessionInventory))
                return sessionInventory;
            return null;
        }
        private void InstallAssemblyCandidate()
        {
            if (Blocked || assemblySnapshot == null) return;
            if (selectedAssemblySnapshot != null)
            {
                if (selectedAssemblySnapshot.CanApply) SubmitAssembly(assemblyParentId, assemblySlotId, selectedAssemblyCandidate);
                return;
            }
            foreach (AssemblySlotSnapshot slot in assemblySnapshot.Slots)
                if (slot.ParentItemId == assemblyParentId && slot.SlotId == assemblySlotId && slot.InstalledItem != null && slot.CanRemove)
                { SubmitAssembly(assemblyParentId, assemblySlotId, ItemId.None); return; }
        }
        private void SubmitAssembly(ItemId parentId, string slotId, ItemId candidateId)
        {
            if (Blocked || source == null || assemblySnapshot == null) return;
            assemblyPreview?.ClearCandidatePreview();
            pendingAssemblyRequestId = ++nextRequestId; pendingAssemblyRoot = assemblyRootId; pendingAssemblyParent = parentId; pendingAssemblySlot = slotId;
            pendingDeadline = Time.unscaledTime + 5f; Render();
            EventBus.Global.Publish(CmdChannels.AssemblyOperation, source, new AssemblyOperation(pendingAssemblyRequestId,
                assemblyRootId, parentId, slotId, candidateId, assemblySnapshot.StructureRevision, sessionId));
        }
        private void PreviousAssemblyPage()
        {
            if (Blocked || assemblySnapshot == null || assemblyOffset <= 0) return;
            assemblyOffset = Mathf.Max(0, assemblyOffset - 64); RequestAssembly();
            if (candidateScrollRect != null) candidateScrollRect.verticalNormalizedPosition = 1f;
        }
        private void NextAssemblyPage()
        {
            if (Blocked || assemblySnapshot == null || assemblySnapshot.NextCandidateOffset < 0) return;
            assemblyOffset = assemblySnapshot.NextCandidateOffset; RequestAssembly();
            if (candidateScrollRect != null) candidateScrollRect.verticalNormalizedPosition = 1f;
        }
        private void OnAssemblyInspection(AssemblyInspectionSnapshot value)
        {
            if (!sessionOpen || !assemblyMode || source == null || value == null || value.SessionId != sessionId
                || value.RequestId != assemblyRequestId || value.RootItemId != assemblyRootId || value.ParentItemId != assemblyParentId
                || value.SlotId != assemblySlotId || value.CandidateOffset != assemblyOffset) return;
            assemblySnapshot = value;
            assemblyPreview?.Bind(previewInventory ?? LocalBackpackSourceResolver.Resolve(), assemblyRootId);
            assemblyQueryPending = false;
            ulong currentFingerprint = ComputeAssemblyFingerprint();
            bool changedDuringQuery = assemblyDirty || currentFingerprint != assemblyQueryFingerprint;
            assemblyFingerprint = currentFingerprint;
            assemblyDirty = false;
            if (value.RootItem == null)
            {
                assemblyRootId = ItemId.None; assemblySlotId = null; assemblyMode = false;
                assemblyPreview?.Clear(); SetMessage("物品已离开当前玩家，检查已清除");
            }
            else if (changedDuringQuery && pendingAssemblyRequestId == 0)
            {
                selectedAssemblyCandidate = ItemId.None; selectedAssemblySnapshot = null;
                assemblyPreview?.ClearCandidatePreview(); assemblyOffset = 0; RequestAssembly();
            }
            Render();
        }
        private void OnAssemblyOperationCompleted(AssemblyOperationResult value)
        {
            if (!sessionOpen || pendingAssemblyRequestId == 0 || value.RequestId != pendingAssemblyRequestId
                || value.SessionId != sessionId || value.RootItemId != pendingAssemblyRoot || value.ParentItemId != pendingAssemblyParent
                || value.SlotId != pendingAssemblySlot) return;
            pendingAssemblyRequestId = 0;
            SetMessage(value.Success ? "装配完成" : string.IsNullOrEmpty(value.Error) ? "操作未完成，请重试" : value.Error);
            selectedAssemblyCandidate = ItemId.None; selectedAssemblySnapshot = null;
            if (!value.Success && !value.ConflictItemId.IsNone) assemblyPreview?.FocusSlot(value.ConflictItemId, value.ConflictSlotId);
            RequestAssembly(); Render();
        }

        public void InspectAtPointer(Vector2 point)
        {
            if (InventoryBusy || pendingRequestId != 0 || pendingAssemblyRequestId != 0 || interaction == null
                || !interaction.TryGetItemAtPointer(point, out RuntimeItem item, out IBackpackView inventory)) return;
            IBackpackView local = LocalBackpackSourceResolver.Resolve();
            IBackpackView equipment = LocalBackpackSourceResolver.ResolveEquipment(local);
            bool playerSource = local != null && (ReferenceEquals(inventory, local) || ReferenceEquals(inventory, equipment));
            bool sessionSource = allowSessionInventories && interaction.CanInspectSessionInventory(inventory);
            // 大厅可在没有场景玩家时检查当前交互会话中的玩家库存；普通箱子窗口仍只检查玩家背包/装备。
            if (!playerSource && !sessionSource) return;
            requestedSource = inventory.EventSource;
            previewInventory = inventory;
            if (!ReferenceEquals(source, requestedSource)) BindSource(requestedSource);
            interaction.SelectItem(item, inventory);
            Inspect(item);
        }

        private void Inspect(ItemId id)
        {
            if (source == null || pendingRequestId != 0 || id.IsNone) return;
            if (assemblyMode && !assemblyRootId.IsNone)
                EventBus.Global.Publish(CmdChannels.AssemblyQuery, source, new AssemblyQuery(ItemId.None, sessionId: sessionId, requestId: ++nextRequestId));
            assemblyMode = false; assemblySnapshot = null; assemblyRootId = ItemId.None; pendingAssemblyRequestId = 0;
            assemblyPreview?.Clear();
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
        private void RemoveMagazine() { if (!assemblyMode && snapshot != null && snapshot.CanRemoveMagazine) Submit(WeaponOperationKind.ExchangeMagazine, ItemId.None); }
        private void RemoveMuzzle() { if (!assemblyMode && snapshot != null && snapshot.CanRemoveMuzzle) Submit(WeaponOperationKind.ExchangeAttachment, ItemId.None); }

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
            if (assemblyMode) { InstallAssemblyCandidate(); return; }
            WeaponUiCandidate candidate = SelectedAmmo();
            if (candidate == null || !candidate.CanApply || candidate.MaxCount < 1) return;
            int count = loadQuantity != null ? Mathf.Clamp(Mathf.RoundToInt(loadQuantity.value), 1, candidate.MaxCount) : candidate.MaxCount;
            Submit(WeaponOperationKind.LoadMagazine, candidate.Item.ItemId, count);
        }

        private void Render()
        {
            if (assemblyMode) { RenderAssembly(); return; }
            SetAssemblyIconPose(false);
            SetButtonLabel(removeMagazineButton, "卸下弹匣");
            SetButtonLabel(removeMuzzleButton, "卸下枪口");
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
            if (assemblyMode) RefreshAssemblyStatus(); else RefreshStatus();
        }

        private void RenderAssembly()
        {
            SetAssemblyIconPose(true);
            bool hasItem = assemblySnapshot?.RootItem != null;
            if (contentRoot != null && contentRoot != gameObject) contentRoot.SetActive(true);
            if (title != null) title.text = hasItem ? assemblySnapshot.RootItem.Name + " · 装配检查" : "装配检查";
            if (summary != null) summary.text = string.Empty;
            if (icon != null) { icon.sprite = hasItem ? assemblySnapshot.RootItem.Icon : null; icon.enabled = icon.sprite != null; icon.preserveAspect = true; }
            SetVisible(magazineButton, false); SetVisible(muzzleButton, false);
            if (quantityRoot != null) quantityRoot.SetActive(true);
            if (loadQuantity != null) loadQuantity.gameObject.SetActive(false);
            if (loadQuantityLabel != null) loadQuantityLabel.gameObject.SetActive(false);
            if (magazineLabel != null) magazineLabel.text = "装配槽位";
            if (removeMagazineButton != null)
            {
                TMP_Text label = removeMagazineButton.GetComponentInChildren<TMP_Text>(); if (label != null) label.text = "上一页";
            }
            if (removeMuzzleButton != null)
            {
                TMP_Text label = removeMuzzleButton.GetComponentInChildren<TMP_Text>(); if (label != null) label.text = "下一页";
            }
            if (candidateHeading != null) candidateHeading.text = assemblyQueryPending ? "正在读取装配候选…"
                : string.IsNullOrEmpty(assemblySlotId) ? "选择左侧槽位" : "可安装候选 · 点击预览";
            int slotCount = 0;
            if (hasItem && slotRoot != null && slotPrefab != null)
            {
                foreach (AssemblySlotSnapshot slot in assemblySnapshot.Slots)
                {
                    if (slotCount == slotRows.Count) slotRows.Add(Instantiate(slotPrefab, slotRoot));
                    AssemblySlotRow row = slotRows[slotCount++]; row.gameObject.SetActive(true);
                    row.Bind(this, slot, slot.ParentItemId == assemblyParentId && slot.SlotId == assemblySlotId,
                        InventoryBusy || pendingRequestId != 0 || pendingAssemblyRequestId != 0);
                }
            }
            for (int i = slotCount; i < slotRows.Count; i++) slotRows[i].gameObject.SetActive(false);
            if (slotRoot is RectTransform slotContent)
                slotContent.sizeDelta = new Vector2(slotContent.sizeDelta.x, slotCount * 56f);
            assemblyPreview?.RenderMarkers(assemblySnapshot?.Slots, assemblyParentId, assemblySlotId, SelectAssemblySlot);
            Transform root = assemblyCandidateRoot != null ? assemblyCandidateRoot : candidateRoot;
            int used = 0;
            if (hasItem && !assemblyQueryPending && root != null && candidatePrefab != null)
            {
                for (int i = 0; i < assemblySnapshot.Candidates.Count; i++)
                {
                    AssemblyCandidateSnapshot candidate = assemblySnapshot.Candidates[i];
                    if (used == assemblyRows.Count) assemblyRows.Add(Instantiate(candidatePrefab, root));
                    WeaponCandidateRow row = assemblyRows[used++]; row.gameObject.SetActive(true);
                    row.BindAssembly(SelectAssemblyCandidate, i, candidate, candidate.Item.ItemId == selectedAssemblyCandidate, Blocked);
                    if (row.transform is RectTransform candidateRect) candidateRect.anchoredPosition = new Vector2(0f, -i * 112f);
                }
            }
            for (int i = used; i < assemblyRows.Count; i++) assemblyRows[i].gameObject.SetActive(false);
            if (assemblyCurrentSummary != null) assemblyCurrentSummary.text = assemblySnapshot?.CurrentSummary ?? string.Empty;
            if (assemblyPreviewSummary != null) assemblyPreviewSummary.text = assemblyQueryPending ? "" : selectedAssemblySnapshot?.CombatSummary ?? assemblySnapshot?.PreviewSummary ?? string.Empty;
            AssemblySlotSnapshot selectedSlot = null;
            if (assemblySnapshot != null)
                foreach (AssemblySlotSnapshot slot in assemblySnapshot.Slots)
                    if (slot.ParentItemId == assemblyParentId && slot.SlotId == assemblySlotId) { selectedSlot = slot; break; }
            bool canInstall = selectedAssemblySnapshot != null && selectedAssemblySnapshot.CanApply;
            bool canRemove = selectedAssemblySnapshot == null && selectedSlot != null && selectedSlot.InstalledItem != null && selectedSlot.CanRemove;
            if (assemblyInstallButton != null)
            {
                assemblyInstallButton.interactable = !Blocked && (canInstall || canRemove);
                TMP_Text label = assemblyInstallButton.GetComponentInChildren<TMP_Text>();
                if (label != null) label.text = canInstall ? "安装所选" : canRemove ? "卸下当前" : "选择候选";
            }
            if (assemblyPageLabel != null) assemblyPageLabel.text = assemblyQueryPending ? "读取中…" : assemblySnapshot == null ? "" : assemblySnapshot.TotalCandidateCount == 0 ? "0 / 0" : $"{assemblySnapshot.CandidateOffset + 1}–{assemblySnapshot.CandidateOffset + assemblySnapshot.Candidates.Count} / {assemblySnapshot.TotalCandidateCount}";
            if (assemblyPreviousPageButton != null) assemblyPreviousPageButton.interactable = !Blocked && assemblyOffset > 0;
            if (assemblyNextPageButton != null) assemblyNextPageButton.interactable = !Blocked && assemblySnapshot != null && assemblySnapshot.NextCandidateOffset >= 0;
            RefreshAssemblyStatus();
        }

        private void SetAssemblyIconPose(bool assembly)
        {
            if (icon == null || !(icon.transform is RectTransform rect)) return;
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = assembly ? new Vector2(180f, 180f) : new Vector2(64f, 64f);
        }

        private void RefreshAssemblyStatus()
        {
            if (status == null) return;
            status.text = pendingAssemblyRequestId != 0 ? "正在提交装配操作…"
                : Time.unscaledTime < messageExpires ? message
                : source == null ? "等待本地玩家就绪…"
                : assemblySnapshot == null ? "正在读取装配数据…"
                : !string.IsNullOrEmpty(assemblySnapshot.OperationBlockReason) ? assemblySnapshot.OperationBlockReason
                : string.IsNullOrEmpty(assemblySlotId) ? "选择槽位查看兼容候选"
                : assemblySnapshot.Candidates.Count == 0 ? $"此页没有候选 · 共 {assemblySnapshot.TotalCandidateCount} 件"
                : "选择候选预览；确认后安装 · 点击槽位可卸下已安装物品";
            if (assemblyPreview != null && !string.IsNullOrEmpty(assemblyPreview.StatusMessage)) status.text += " · " + assemblyPreview.StatusMessage;
        }

        private void RenderQuantity()
        {
            WeaponUiCandidate candidate = SelectedAmmo();
            bool visible = snapshot?.Item != null && snapshot.IsMagazine;
            if (quantityRoot != null) quantityRoot.SetActive(visible);
            if (loadQuantity != null) loadQuantity.gameObject.SetActive(visible);
            if (loadQuantityLabel != null) loadQuantityLabel.gameObject.SetActive(visible);
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
            if (visible) SetButtonLabel(loadButton, "装填");
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
        private static void SetButtonLabel(Button button, string value)
        { if (button == null) return; TMP_Text label = button.GetComponentInChildren<TMP_Text>(); if (label != null) label.text = value; }
        private void SetMessage(string value) { message = value; messageExpires = Time.unscaledTime + 3f; }
        private void Touch() => lastInteractionFrame = Time.frameCount;

        public bool BlocksInventoryPointer(Vector2 screenPoint)
        {
            if (!IsVisible) return false;
            if (!sessionOpen || window == null || !window.IsOpen) return false;
            if (lastInteractionFrame == Time.frameCount) return true;
            Camera camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            return panelRect != null && RectTransformUtility.RectangleContainsScreenPoint(panelRect, screenPoint, camera);
        }
    }
}
