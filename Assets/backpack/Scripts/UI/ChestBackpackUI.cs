using UnityEngine;
using RPG4.Backpack.Core;
using RPG4.Backpack.Inventory;
using RPG4.Backpack.Runtime;
using RPG4.UI;

namespace RPG4.Backpack.UI
{
    /// <summary>箱子双栏窗口：左栏绑定玩家、右栏绑定指定箱子；协调双栏交互会话、标题与窗口布局。</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(RectTransform), typeof(CanvasGroup), typeof(ChestBackpackUIData))]
    [DefaultExecutionOrder(10)]
    public sealed class ChestBackpackUI : UIBase
    {
        [SerializeField] private ChestBackpackUIData ui;

        [Header("背包来源")]
        [Tooltip("留空时寻找本地玩家背包；指定后始终使用这个玩家背包。")]
        [SerializeField] private BackpackInventory playerInventory;
        [SerializeField] private bool resolveLocalPlayer = true;
        [SerializeField, Min(0.05f)] private float bindingCheckInterval = 0.25f;

        [Header("双栏布局")]
        [SerializeField] private bool usePrefabLayout;
        [SerializeField] private Vector2 padding = new Vector2(24f, 24f);
        [SerializeField, Min(0f)] private float paneSpacing = 48f;
        [SerializeField, Min(0f)] private float titleHeight = 40f;

        [Header("标题文本")]
        [Tooltip("使用中文标题时，请在标题的 TextMeshProUGUI 组件上指定包含中文字形的字体。")]
        [SerializeField] private string playerTitleText = "Player Backpack";
        [SerializeField] private string chestTitleText = "Chest Backpack";
        [SerializeField] private string waitingPlayerTitleText = "Player Backpack (waiting)";
        [SerializeField] private string unboundChestTitleText = "Chest Backpack (unbound)";

        private RectTransform windowRect;
        private Vector2 lastPlayerSize;
        private Vector2 lastChestSize;
        private bool layoutDirty = true;
        private bool configurationWarned;
        private float nextBindingCheck;
        private ChestInventory currentChest;
        private readonly InventoryInputScope inputScope = new InventoryInputScope();
        private object boundPlayerEventSource;
        private object boundChestEventSource;
        private object boundEquipmentEventSource;

        public BackpackContainerUI PlayerView => ui != null ? ui.playerView : null;
        public BackpackContainerUI ChestView => ui != null ? ui.chestView : null;
        public BackpackInteractionController Interaction => ui != null ? ui.interaction : null;
        public IBackpackView PlayerInventory => PlayerView != null ? PlayerView.Inventory : null;
        public IBackpackView ChestInventory => ChestView != null ? ChestView.Inventory : null;

        protected override void Awake()
        {
            base.Awake();
            EnsureInitialized();
        }

        private void Update()
        {
            if (!IsOpen) return;
            if (Time.unscaledTime >= nextBindingCheck)
            {
                nextBindingCheck = Time.unscaledTime + Mathf.Max(0.05f, bindingCheckInterval);
                RefreshBindings();
            }
            inputScope.SetSource(PlayerInventory);
        }

        private void LateUpdate()
        {
            if (IsOpen) ArrangeViews();
        }

        /// <summary>显示指定箱子的双栏背包，仅在打开期间保留箱子引用；来源尚未初始化时显示占位格并等待就绪。</summary>
        public bool Open(ChestInventory chest)
        {
            if (chest == null || !EnsureInitialized()) return false;
            if (IsOpen)
            {
                currentChest = chest;
                RefreshBindings();
                return true;
            }

            currentChest = chest;
            if (base.Open()) return true;
            currentChest = null;
            return false;
        }

        protected override bool CanOpen() => currentChest != null && EnsureInitialized();

        protected override void OnOpened()
        {
            nextBindingCheck = Time.unscaledTime + Mathf.Max(0.05f, bindingCheckInterval);
            ui.interaction.enabled = true;
            RefreshBindings();
            inputScope.Capture(PlayerInventory);
            ArrangeViews();
        }

        /// <summary>显式指定玩家与箱子，左右来源不会随鼠标位置或其他背包窗口改变。</summary>
        public bool Open(BackpackInventory player, ChestInventory chest)
        {
            BackpackInventory previousPlayer = playerInventory;
            playerInventory = player;
            if (Open(chest)) return true;
            playerInventory = previousPlayer;
            return false;
        }

        /// <summary>隐藏窗口并释放双方显示订阅；保留格子配置，再次 Open 时重新获取与显示库存。</summary>
        protected override void OnClosed()
        {
            EndInteractionSession();
            if (ui != null && ui.interaction != null)
            {
                ui.interaction.enabled = false;
            }
            currentChest = null;
            boundPlayerEventSource = boundChestEventSource = boundEquipmentEventSource = null;
            if (ui != null && ui.playerView != null)
            {
                ui.playerView.UnbindInventory();
                ui.playerView.SetPresentationVisible(false);
            }

            if (ui != null && ui.chestView != null)
            {
                ui.chestView.UnbindInventory();
                ui.chestView.SetPresentationVisible(false);
            }
            if (ui != null && ui.equipmentView != null)
            {
                ui.equipmentView.UnbindInventory();
                ui.equipmentView.SetPresentationVisible(false);
            }

            inputScope.Release();
        }

        private void EndInteractionSession()
        {
            if (ui != null && ui.interaction != null) ui.interaction.EndSession();
        }

        /// <summary>重新获取双方数据源；这里只解析玩家来源，箱子由本次 Open 参数指定。</summary>
        public void RefreshBindings()
        {
            if (!IsOpen || !EnsureInitialized()) return;
            if (currentChest == null)
            {
                Close();
                return;
            }

            IBackpackView player = ResolvePlayer();
            IBackpackView equipment = LocalBackpackSourceResolver.ResolveEquipment(player);
            BackpackInventory chest = currentChest != null ? currentChest.Inventory : null;
            if (chest != null && !chest.IsInitialized) chest = null;

            object playerEventSource = player?.EventSource;
            object chestEventSource = chest?.EventSource;
            object equipmentEventSource = equipment?.EventSource;
            bool sourceChanged = !ReferenceEquals(ui.playerView.Inventory, player)
                || !ReferenceEquals(ui.chestView.Inventory, chest)
                || !ReferenceEquals(ui.equipmentView.Inventory, equipment)
                || !ReferenceEquals(boundPlayerEventSource, playerEventSource)
                || !ReferenceEquals(boundChestEventSource, chestEventSource)
                || !ReferenceEquals(boundEquipmentEventSource, equipmentEventSource);
            // 任一侧来源或事件键变化，都要在视图改绑前清理旧交互。
            if (sourceChanged) EndInteractionSession();
            ui.playerView.BindInventory(player);
            ui.chestView.BindInventory(chest);
            ui.equipmentView.BindInventory(equipment);
            boundPlayerEventSource = playerEventSource;
            boundChestEventSource = chestEventSource;
            boundEquipmentEventSource = equipmentEventSource;
            if (!ui.interaction.IsSessionActive)
            {
                if (equipment != null) ui.interaction.BeginSession(ui.playerView, ui.chestView, ui.equipmentView);
                else ui.interaction.BeginSession(ui.playerView, ui.chestView);
                // 箱子窗口保留原有背包 ↔ 箱子的快捷转移；装备槽只接受手动拖放。
                ui.interaction.SetQuickTransferPair(ui.playerView, ui.chestView);
            }
            ui.playerView.SetPresentationVisible(true);
            ui.chestView.SetPresentationVisible(true);
            ui.equipmentView.SetPresentationVisible(true);
            ui.playerTitle.text = player != null ? playerTitleText : waitingPlayerTitleText;
            ui.chestTitle.text = chest != null ? chestTitleText : unboundChestTitleText;
            inputScope.SetSource(PlayerInventory);
            layoutDirty = true;
        }

        private IBackpackView ResolvePlayer()
        {
            // 接口的普通 null 判断不识别已销毁的 Unity 组件，显式来源先用具体组件判断存活。
            if (!ReferenceEquals(playerInventory, null))
            {
                return playerInventory != null && playerInventory.IsInitialized ? playerInventory : null;
            }

            if (!resolveLocalPlayer) return null;

            return LocalBackpackSourceResolver.Resolve();
        }

        private bool EnsureInitialized()
        {
            if (windowRect == null) windowRect = GetComponent<RectTransform>();
            if (ui == null) ui = GetComponent<ChestBackpackUIData>();
            if (ui == null || ui.playerView == null || ui.chestView == null || ui.equipmentView == null
                || ui.playerView == ui.chestView || ui.equipmentView == ui.playerView || ui.equipmentView == ui.chestView || ui.interaction == null
                || ui.playerTitle == null || ui.chestTitle == null || ui.interaction.CursorLayer == null)
            {
                if (!configurationWarned)
                {
                    configurationWarned = true;
                    BackpackLog.Warn(LogModule.UI, "请在 ChestBackpackUIData 中接线不同的背包、箱子和装备视图、交互组件和标题，并在交互组件中接线携带预览层。");
                }
                return false;
            }

            configurationWarned = false;
            if (!ui.playerView.EnsureInitialized() || !ui.chestView.EnsureInitialized() || !ui.equipmentView.EnsureInitialized()) return false;
            if (!IsOpen) ui.interaction.enabled = false;
            return true;
        }

        private void ArrangeViews()
        {
            if (usePrefabLayout) return;
            if (ui.playerView == null || ui.chestView == null || windowRect == null) return;
            RectTransform playerRect = ui.playerView.GetComponent<RectTransform>();
            RectTransform chestRect = ui.chestView.GetComponent<RectTransform>();
            Vector2 playerSize = playerRect.sizeDelta;
            Vector2 chestSize = chestRect.sizeDelta;
            if (!layoutDirty && playerSize == lastPlayerSize && chestSize == lastChestSize) return;

            layoutDirty = false;
            lastPlayerSize = playerSize;
            lastChestSize = chestSize;
            float left = Mathf.Max(0f, padding.x);
            float top = Mathf.Max(0f, padding.y);
            float spacing = Mathf.Max(0f, paneSpacing);
            float header = Mathf.Max(0f, titleHeight);
            float rightX = left + playerSize.x + spacing;

            PlaceRect(playerRect, new Vector2(left, -top - header));
            PlaceRect(chestRect, new Vector2(rightX, -top - header));
            PlaceRect(ui.playerTitle.rectTransform, new Vector2(left, -top));
            PlaceRect(ui.chestTitle.rectTransform, new Vector2(rightX, -top));
            ui.playerTitle.rectTransform.sizeDelta = new Vector2(playerSize.x, header);
            ui.chestTitle.rectTransform.sizeDelta = new Vector2(chestSize.x, header);
            windowRect.sizeDelta = new Vector2(
                left * 2f + playerSize.x + spacing + chestSize.x,
                top * 2f + header + Mathf.Max(playerSize.y, chestSize.y));
            ui.interaction.CursorLayer.SetAsLastSibling();
        }

        private static void PlaceRect(RectTransform rect, Vector2 position)
        {
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = position;
        }
    }
}
