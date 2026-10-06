---
name: RPG4_Backpack_M6_点击拾取与堆叠操作落盘
overview: 在 e:/ue/RPG_4 按蓝本 ZDXT2 落盘背包系统 M6「点击拾取与堆叠操作」：主控 BackpackGridUI 补齐几何/渲染/携带预览接口，新增独立交互控制器 + 右键弹窗 + 描述面板（视图为序列化手搭），回填网络侧拆分链路，并用 Assets/input/InputActions.cs 的 leftButton/rightButton 经 Assembly-CSharp 输入桥驱动点击。
todos:
  - id: grid-api
    content: 扩展 BackpackGridUI：补 TryResolveTile/GetUpperLeftIndex/QuerySpace、本地位置表、携带预览层与悬停高亮
    status: completed
  - id: interaction-core
    content: 用 [skill:Context Mode] 检索蓝本源码，移植 BackpackHoverState 与 BackpackInteractionController（每帧缓存链、左右键分派、堆叠三分支、取消）
    status: completed
    dependencies:
      - grid-api
  - id: popup-desc
    content: 移植弹窗与描述面板三件套，改写为序列化手搭字段（拆分上限=堆叠数-1、折叠→同化）
    status: completed
    dependencies:
      - interaction-core
  - id: net-split
    content: 按蓝本回填 BackpackNetworkInventory 拆分链路 8 处，并更新文件头剥离清单注释
    status: completed
  - id: input-bridge
    content: 新增 Assets/input/BackpackInputBridge.cs，用 InputActions 的左右键回调推送给交互控制器
    status: completed
    dependencies:
      - interaction-core
  - id: meta-doc-review
    content: 生成 .meta 并查重 GUID，落 M6 文档（手搭接线清单+验收步骤），用 [skill:Open Code Review] 自检移植件
    status: completed
    dependencies:
      - grid-api
      - interaction-core
      - popup-desc
      - net-split
      - input-bridge
---

# RPG_4 背包 M6「点击拾取与堆叠操作」落盘

## 目标

`e:/ue/RPG_4` 的背包网格从「只读可视化」升级为「可点击交互」：悬停 / 拾起 / 交换 / 拆分 / 填充 / 丢弃。交互输入来自 `Assets/input/InputActions.cs`（map `backpack` 的 `leftButton` / `rightButton`）。蓝本 = `e:/github/ZDXT2/Assets/backpack`，前缀改为 `RPG4.Backpack.*`。

## 选型（已确认）

1. 扩主控 + 独立交互控制器；格子与物品框仍内联在 `BackpackGridUI`。
2. 输入桥放 `Assets/input/`（Assembly-CSharp 侧用生成类，推给 UI）；不动 input 目录结构、不改 asmdef。
3. 弹窗「丢弃 / 消耗」按蓝本发 `Cmd.Item.Drop` / `Cmd.Item.Consume`，订阅者留到 M8。
4. 弹窗与描述面板为序列化引用字段，Prefab/层级由用户手搭（沿用 M5 风格）。

## 交付文件

| 文件 | 动作 | 内容 |
| --- | --- | --- |
| `backpack/Scripts/UI/BackpackGridUI.cs` | 改 | 补几何/查询/本地位置表/携带预览/悬停高亮接口；`Refresh` 支持显式位置、跳过隐藏物；转发 `PushClick` |
| `backpack/Scripts/UI/BackpackHoverState.cs` | 新 | 悬停 4 态枚举 + 配色常量 |
| `backpack/Scripts/UI/BackpackInteractionController.cs` | 新 | M6 决策核心（照搬蓝本 834 行骨架）：每帧缓存链、左右键分派、堆叠三分支、弹窗/描述驱动、取消与清理 |
| `backpack/Scripts/UI/BackpackItemPopupView.cs` | 新 | 右键弹窗（改写为序列化手搭） |
| `backpack/Scripts/UI/BackpackItemDescriptionPanel.cs` | 新 | 悬停描述面板（改写为序列化手搭） |
| `backpack/Scripts/UI/BackpackItemDescriptionContent.cs` | 新 | 描述内容纯数据（照搬，无 Unity UI 依赖） |
| `backpack/Scripts/Network/Inventory/BackpackNetworkInventory.cs` | 改 | 回填拆分链路 8 处 + 更新文件头剥离清单注释 |
| `input/BackpackInputBridge.cs` | 新 | Assembly-CSharp 输入桥（实现 `InputActions.IBackpackActions`） |
| `backpack/Docs/里程碑/M6_点击拾取与堆叠操作.md` | 新 | 落盘清单、与蓝本差异、Inspector 接线清单、人工验收步骤 |


全部配套 `.meta` 手工生成；移植件优先沿用蓝本 GUID（先全量查重）。

## 硬口径（路线图 M6 旁注 + 修正项）

- 每帧预算优先：鼠标 → 格子 → 象限 → 落点索引 → `QuerySpace`；3~8 号分支只读缓存，不重算（E）。
- 悬停仅在有悬停物时着色；无悬停物时格子只切底色。
- 三分支顺序固定：交换堆叠数 → 全部并入 → 部分填充；异类型或不可堆叠→交换位置。判同类型用 `MatchesTagExact` + 双方 `IsStackable`。
- 弹窗：拆分上限 = 堆叠数 − 1、下限 1；鼠标离开 0.35s 自关；拆出部分跟随光标。
- 描述面板：0.45s 计时 → 先折叠 → 精确匹配才同化 → 显示（F）。
- 位置只存本机，不加任何位置同步 RPC（B）；点击拾取而非拖放（C）；不启用 `Ui.GridSlotClicked` / `Ui.Popup.Split` 死键（J）。
- 描述面板背景 `raycastTarget = true`（以蓝本代码为准）；格子与物品框保持 `raycastTarget = false`。
- 装备槽分支本轮不落，留空位方法与注释，M7 直接填充。

## 关键接口（新增公开成员）

- 主控：`TryResolveTile` / `GetUpperLeftIndex`（越界返回 -1，不夹取）/ `QuerySpace`（基于本机排布）/ `TryGetItem` / `TryGetItemView` / `TryGetPlacement` / `SetManualPosition` / `ClearManualPosition` / `PruneManualPositions` / `SetHiddenItem` / `ClearHiddenItem` / `TryShowCarriedPreview` / `SetCarriedPreviewCellPosition` / `TrySetCarriedPreviewScreenPosition` / `HideCarriedPreview` / `ApplyRegionHighlight` / `ClearHoverHighlight` / `PushClick(int mouseButton)`。
- 控制器：`Configure(grid, description, popup)` / `Bind(inventory)` / `Unbind()` / `PushClick(int)`；公开只读属性 `IsCarrying` / `CurrentTile` / `CurrentSpaceQuery` / `CurrentTargetIndex` / `CurrentHoverState`。
- 输入桥：`public BackpackInteractionController target;`，`performed` 时推 `UiChannels.MouseButton.Left/Right`。

## 实现注意

- 控制器加 `[DefaultExecutionOrder]` 晚于主控；窗口关闭（`grid.IsOpen == false`）或未绑定库存时短路并清理携带态（只保留一种判定来源）。
- 携带预览挂新建第三图层 `cursorLayer`（物品层之上）；沿用 `layer = 5`；尺寸口径 = 格数 × `cellSize` + 间隔。
- `InventoryGridLayout.Arrange(items, preferred)` 已支持显式位置，直接复用，不改该文件。
- asmdef 零改动（`RPG4.Backpack.UI` 已引 `Unity.InputSystem`）。
- 日志一律 `BackpackLog` + `LogModule.UI`；标识符全英文、注释简洁中文。

## 验收

Unity 编译零报错 + `read_lints` 干净；双端人工核对：Tab 开包 → 悬停高亮/置灰 → 左键拾起跟手且源格隐藏 → 放回 → 堆叠三分支 → 异类型换位 → 右键弹窗拆分数收口与离开自关 → ESC/右键取消 → 描述面板折叠→同化显示 → 关窗后携带态清理、复制回后位置不回跳。

## 不做

M4 世界拾取、M7 装备槽、M8 消耗与掉落、场景/预制体落盘、单测、Editor 工具。