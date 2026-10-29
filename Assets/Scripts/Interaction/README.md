# 玩家准星交互

`PlayerInteraction` 从相机视口中心发射射线，忽略玩家自身与 Trigger，只取最近碰撞体。墙壁等非交互物体会阻挡射线；距离以玩家根节点到命中点计算，默认 3 米。碰撞体可以在物品子节点，交互组件挂在它的父节点。

- 键盘 **E** / 手柄 **西侧按钮（X / Square）** 触发一次交互。
- 输入由玩家根节点上的自定义 `RPG4.Input.PlayerInput` 统一采样，`PlayerInteraction` 从该角色的共享 `PlayerBlackboard` 读取 `CanInteract` / `InteractPressed`；原 PlayerInput 字段只用于结构绑定与明确阶段刷新，未绑定时从同节点获取该组件。
- 准心默认白色，检测到距离内的可交互物品时变绿。
- `InputIntentProcessor` 统一控制焦点、暂停和 UI 输入占用并写入共享许可；输入被阻止或交互相机不可用时隐藏准心并暂停交互。检测/交互前后显式刷新许可，开窗当帧使黑板表现失效，Player 的既有 Late 阶段同步撤销已施加的骨架补偿；目标结果经同一输入处理器写回黑板。
- `interactionCamera` 留空使用 `Camera.main`。`raycastLayers` 必须同时包含物品和遮挡物。

## 已接入的资源

`Assets/player/player.prefab` 已挂载玩家交互组件，并引用 `Assets/player/Interaction/CrosshairUI.prefab`。准心预制体自带 Overlay Canvas、CanvasGroup 和居中图形，引用已配置；每个本机玩家在 Awake 时实例化一次，禁用交互组件时隐藏。

`Assets/player/Interaction/ItemInteractable.prefab` 是带非 Trigger BoxCollider 的基础方块物品，已挂载 `ItemInteractable`。核心场景 `Assets/Scenes/Main.unity` 中放置了该预制体实例，位于现有主相机准心前方、玩家 3 米范围内。

`Assets/backpack/Prefabs/Chest.prefab` 是独立箱子，带非 Trigger BoxCollider、`BackpackInventory` 和实现 `IInteractable` 的 `ChestInventory`。同一场景在玩家右前方放置了实例（1.5, 0.4, 1.8），并仅对该实例启用测试物品填充；预制体默认不自动生成物品。准心瞄准且在距离内时按 E，经 `ChestInventory.OpenUI()` 打开玩家与当前箱子的双栏背包。玩家预制体使用 `CharacterBackpack` 登记本机背包，箱子不会登记为玩家来源。打开窗口期间停止角色方向输入、相机环绕与缩放，并阻止点击重新锁定鼠标；按 B 关闭后恢复。

普通 `ItemInteractable` 仍只输出交互日志并调用 Inspector 的 `On Interacted` 事件，保留在场景中，可重复交互；尚未接入拾取入库或物品销毁。

`Assets/player/Interaction/WorldPickup.prefab` 是独立的真实物品拾取预制体，`WorldPickup` 在 Inspector 绑定 `ItemManifest` 与数量。Main 的玩家左前方放置了小型红药 ×3 与火蕨果实 ×12，沿用现有方块视觉；瞄准后准星下显示物品名、数量和按键。交互使用玩家的本地 `BackpackInventory` 权威整批接收，先合并同类堆叠再放入空格；成功后禁用世界物体，容量不足时保留世界物体并显示失败原因。首次尝试分配共享全局 ItemId 并冻结运行时实例，失败重试不重新随机化。

成功、失败反馈复用 `CrosshairUI.prefab` 中已接线的 TMP 文本（现有 NotoSansSC 字体），不在运行时拼装固定 UI。拾取目前仅支持单机：Mirror 客户端、Host、服务器或带网络身份/网络库存的玩家会收到明确提示，世界物体及库存均不变；未来网络拾取需要独立的服务端验证协议。

## 人工验证

进入核心场景 `Assets/Scenes/Main.unity`，确认对准普通物品时准心变绿，按 E 仅触发一次日志；对准箱子按 E 打开双栏，左栏为角色、右栏为该箱子，能向空位双向转移及拆分物品。窗口打开时鼠标保持可见、可点击，移动和相机输入暂停；按 B 关闭后恢复，箱子可再次打开且保留库存。移开准心、超过 3 米、隔着墙壁、打开背包或禁用箱子组件时不能交互。未进入 Unity 或运行直接测试，仅完成代码与序列化引用静态检查。此组件仅供本机玩家使用，联网远端玩家应禁用它。
