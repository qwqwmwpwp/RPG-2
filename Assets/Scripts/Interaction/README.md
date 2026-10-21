# 玩家准星交互

`PlayerInteraction` 从相机视口中心发射射线，忽略玩家自身与 Trigger，只取最近碰撞体。墙壁等非交互物体会阻挡射线；距离以玩家根节点到命中点计算，默认 3 米。碰撞体可以在物品子节点，交互组件挂在它的父节点。

- 键盘 **E** / 手柄 **西侧按钮（X / Square）** 触发一次交互。
- 准心默认白色，检测到距离内的可交互物品时变绿。
- 打开 `UIManager` 管理的窗口或暂停游戏时，隐藏准心并暂停交互。
- `interactionCamera` 留空使用 `Camera.main`。`raycastLayers` 必须同时包含物品和遮挡物。

## 已接入的资源

`Assets/player/player.prefab` 已挂载玩家交互组件，并引用 `Assets/player/Interaction/CrosshairUI.prefab`。准心预制体自带 Overlay Canvas、CanvasGroup 和居中图形，引用已配置；每个本机玩家在 Awake 时实例化一次，禁用交互组件时隐藏。

`Assets/player/Interaction/ItemInteractable.prefab` 是带非 Trigger BoxCollider 的基础方块物品，已挂载 `ItemInteractable`。`Assets/backpack/Scenes/New Scene.unity` 中放置了该预制体实例，位于现有主相机准心前方、玩家 3 米范围内。

`Assets/backpack/Prefabs/Chest.prefab` 是独立箱子，带非 Trigger BoxCollider、`BackpackInventory` 和实现 `IInteractable` 的 `ChestInventory`。同一场景在玩家右前方放置了实例（1.5, 0.4, 1.8），并仅对该实例启用测试物品填充；预制体默认不自动生成物品。准心瞄准且在距离内时按 E，经 `ChestInventory.OpenUI()` 打开玩家与当前箱子的双栏背包。玩家预制体使用 `CharacterBackpack` 登记本机背包，箱子不会登记为玩家来源。打开窗口期间停止角色方向输入、相机环绕与缩放，并阻止点击重新锁定鼠标；按 B 关闭后恢复。

普通 `ItemInteractable` 仍只输出交互日志并调用 Inspector 的 `On Interacted` 事件，保留在场景中，可重复交互；尚未接入拾取入库或物品销毁。

## 人工验证

进入背包示例场景，确认对准普通物品时准心变绿，按 E 仅触发一次日志；对准箱子按 E 打开双栏，左栏为角色、右栏为该箱子，能向空位双向转移及拆分物品。窗口打开时鼠标保持可见、可点击，移动和相机输入暂停；按 B 关闭后恢复，箱子可再次打开且保留库存。移开准心、超过 3 米、隔着墙壁、打开背包或禁用箱子组件时不能交互。未进入 Unity 或运行直接测试，仅完成代码与序列化引用静态检查。此组件仅供本机玩家使用，联网远端玩家应禁用它。
