---
name: M5_网格UI落盘_RPG4
overview: 在 e:/ue/RPG_4 落盘 M5 网格 UI：把手写的 BackpackGridUI 迁入 Scripts/UI 并改造为背包主控（绑定库存、渲染占格与堆叠、Tab 开关、光标/输入接管、HUD），配套移植布局算法与叶子视图（InventoryGridLayout / GridSlotView / SlottedItemView / 调色板 / BackpackUiFont / BackpackHudView 的 M5 子集），UI 程序集补 Network+Mirror+InputSystem 依赖；不落盘场景、预制体、测试 UI 与文档。
todos:
  - id: port-ui-leaves
    content: 移植 6 个 M5 叶子脚本到 Scripts/UI 并配 .meta、补 UI asmdef 依赖；用 [subagent:code-explorer] 与 [skill:Context Mode] 核差异、查 GUID 冲突
    status: completed
  - id: grid-ui-main
    content: 迁移 BackpackGridUI 到 Scripts/UI 并改造为主控：格子层与物品层、库存绑定渲染、溢出栏、配置校验
    status: completed
    dependencies:
      - port-ui-leaves
  - id: toggle-hud
    content: 实现 Tab 开关与 MenuToggled 事件、光标与 PlayerInput 接管还原、HUD 显隐、幂等注入与清理
    status: completed
    dependencies:
      - grid-ui-main
  - id: verify-wiring
    content: 用 [skill:lsp-code-analysis] 核对引用闭合，输出手搭接线与验收清单（[skill:ADHD友好输出] 格式）
    status: completed
    dependencies:
      - toggle-hud
  - id: memory-sync
    content: 更新项目记忆：M5 落盘进度、Input System 1.14.2 已装纠正、下一步 M6 待办
    status: completed
    dependencies:
      - verify-wiring
---

# M5 网格 UI 落盘（RPG_4）

## 目标

以 `Assets/backpack/New Folder/BackpackGridUI.cs` 为背包主界面，按蓝本 ZDXT2 路线图落盘 M5：**背包可视化占格正确 + 开关正常**。

## 范围

**交付**：只交付脚本 —— `Scripts/UI/` 下 6 个移植件 + 1 个改造件 + UI asmdef 依赖调整。
**不做**：场景 / 预制体（用户手搭）、测试与冒烟 UI、M5 文档、`Actions.inputactions` 改动、网络与数据层改动。

## 落盘清单

| 文件（`Assets/backpack/Scripts/UI/`，命名空间 `RPG4.Backpack.UI`） | 来源 | 处置 |
| --- | --- | --- |
| `InventoryHoverState.cs` | 蓝本 55 行 | 移植：悬停态枚举 + `InventoryFeedbackPalette` 配色 |
| `InventoryGridLayout.cs` | 蓝本 178 行 | 移植：`Arrange` 占位算法（ItemId 升序 + 保位 + 行优先 + 溢出） |
| `GridSlotView.cs` | 蓝本 65 行 | 移植：单格底色（空闲 / 占用 / 悬停四态） |
| `SlottedItemView.cs` | 蓝本 150 行 | 移植：物品框 + 图标 / 类型名 + 堆叠数 |
| `BackpackUiFont.cs` | 蓝本 54 行 | 移植：字体统一入口，未注入则回落 TMP 默认字体 |
| `BackpackHudView.cs` | 蓝本 88 行 | 移植 **M5 子集**：提示文本 + `MenuToggled` 显隐；删拾取提示（M4） |
| `BackpackGridUI.cs` | `New Folder/` 迁移 | 主控改造（保留中文 API 名称与签名） |
| `RPG4.Backpack.UI.asmdef` | 修改 | references 追加 `RPG4.Backpack.Network` / `Mirror` / `Unity.InputSystem` |


- 迁移 `.cs` 与 `.meta` 成对移动，保 GUID `e51a42c8…`；新增件优先沿用蓝本同文件 GUID，写盘前全量查重。
- 不移植 `InventoryUiBootstrap` / `InventoryGridView` / `InventoryMenuView`（职责并入主控）。

## 关键决策

1. **位置本地化**（修正项 B）：坐标只存本机 `InventoryGridLayout`，不进网络复制。
2. **网格分层**：格子层挂 `GridLayoutGroup`（沿用现有格子生成），物品层像素定位手摆；同父级会被布局组件当成网格元素，必须分层；格子层缺省回退面板自身。
3. **像素公式**：`x = 格.x × (格子边长 + 间隔.x)`、`y = -格.y × (格子边长 + 间隔.y)`；物品框尺寸 = `格数 × 格子边长 + (格数-1) × 间隔`。
4. **输入**：Tab 用 Input System `Keyboard.current.tabKey.wasPressedThisFrame`（空引用保护）；不改输入资产。M6 左右键改走序列化 `InputActionAsset` 引用（生成的 `Actions` 类在 Assembly-CSharp，asmdef 无法直接引用）。
5. **事件**：开关统一 `EventBus.Global.Publish(InvChannels.MenuToggled, 库存实例, bool)`；主控与 HUD 各自以库存实例为 source 订阅，键已存在于 F6。
6. **幂等与清理**（修正项 I / M5 旁注）：先注入后监听；重复注入同实例直接返回；`OnDisable` / `OnDestroy` 走同一 `Unbind`（退订 + 还原光标与输入 + 清显示）。
7. **刷新时机**：仅首次绑定、`Changed`、行列变更时重排；不逐帧读物品，视图按 `ItemId` 复用。
8. **配置校验**：UI 行列 ≠ `ConfiguredColumns/ConfiguredRows` → `BackpackLog.Error(LogModule.UI, …)` + 可见警告，不参与容量判定。

## 主控契约（保留既有 + 新增注入点）

```text
既有：GameObject 格子UI; List<GameObject> 背包格子列表; Vector2 格子之间的间隔;
      void 背包格子生成(int x, int y);
新增：RectTransform 格子层 / 物品层; float 格子边长 = 100;
      BackpackHudView 提示视图; TMP_Text 溢出文本;
      BackpackNetworkInventory 绑定库存 (只读);
      void 绑定库存(BackpackNetworkInventory source);  void 开关背包();
行为：每 0.25s 重查本地玩家库存并按实例变化 Bind/Unbind；打开时接管光标并暂停 PlayerInput，
     关闭时还原；放不下的物品进溢出栏。
```

## 验收（人工，Unity 编辑器）

1. Host：Tab 开关 → 窗口 / HUD 显隐、光标释放、PlayerInput 停用并在关闭后还原。
2. 服务端加物：火蕨果实、小红药×N、1×3 剑 → 背景占格（剑占 3 格）与堆叠数正确；改堆叠 / 删物立即刷新。
3. Client：只显示本地玩家物品；服务端改动后刷新；晚加入首次绑定即显示。
4. 断线 / 换玩家 / 禁用组件：订阅与输入占用清理干净，无空引用日志。
5. 边界：行列不一致报 Error + 警告；放不下进溢出栏不丢物品。

> 前置：`Data/` 为空，验收所需的物品 Manifest 沿用 M3 决定由用户手搭（至少 1 个 1×3 不可堆叠物 + 1 个可堆叠物）。