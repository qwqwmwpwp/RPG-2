# UI2 网格主控内部结构整理（变更文档）

> 状态：**已落盘**——2026-10-06 按本文件完成重构；`BackpackInteractionController.cs` 零改动，`dotnet build RPG4.Backpack.UI.csproj` 0 错误 0 警告。
> 日期：2026-10-06
> 影响面：`Assets/backpack/Scripts/UI/BackpackGridUI.cs`（主改动）；`BackpackInteractionController.cs` 默认零改动。
> 关联文档：`Docs/工程架构/F1_F2_程序集与目录命名规范.md` §3.2.1、`Docs/核心契约/UI1_面板接口与多窗口支持.md`（C3 / C4 将复用本次整理后的骨架）
> 性质：**纯结构重构**，不改变任何运行时行为。

---

## 一、背景

`BackpackGridUI.cs` 现为单文件单类，1043 行、约 30 个平铺私有字段、方法无分段。存在三类结构问题：

| # | 问题 | 位置 |
|---|---|---|
| P1 | `slots` / `slotImages` / `slotHoverStates` 三个平行列表必须同序维护，漏同步一处即错位 | 第 26、50、51 行 |
| P2 | 由 `cellSize` + `slotSpacing` 派生的像素换算在 5 处各写一遍，公式重复 | `BuildGrid` / `EnsureItemLayer` / `EnsureCursorLayer` / `ItemPixelSize` / `CellToPixel` / `TryResolveTile` / `TrySetCarriedPreviewScreenPosition` |
| P3 | 输入订阅（3 个 `InputAction` + 2 个标志）、开窗快照（3 个字段）、本机显示状态、重绘缓存全部平铺混放，方法区无分段 | 第 52–79 行 + 全文 |

**引用面核查结论**（改动半径依据）：

| 项 | 结论 |
|---|---|
| 程序化引用 | 全仓仅 `BackpackInteractionController.cs` 一个文件（`[SerializeField] BackpackGridUI grid`） |
| 场景引用 | `backpack/Scenes/New Scene.unity` 通过 GUID `e51a42c8…` 挂载组件 → **序列化字段名不可改** |
| `Toggle()` | 仅本文件定义、无外部调用（M6 文档提及） |
| `Grid` 属性（控制器转发） | 无外部调用 |

---

## 二、变更总览

| 编号 | 变更 | 类型 | 状态 | 影响文件 |
|---|---|---|---|---|
| B1 | 新增 `SlotVisual`，三平行列表合一 | 结构 | 已落盘 | `BackpackGridUI.cs` |
| B2 | 新增 `GridMetrics`（readonly struct），收拢全部像素换算 | 结构 | 已落盘 | 同上 |
| B3 | 新增 `InputBinding`，收拢输入订阅与告警状态 | 结构 | 已落盘 | 同上 |
| B4 | 新增 `WindowInputSnapshot`（struct），收拢开窗快照 | 结构 | 已落盘 | 同上 |
| B5 | 新增 `LocalDisplayState`，收拢本机位置表 / 显示覆盖 / 隐藏物 / 悬停物 | 结构 | 已落盘 | 同上 |
| B6 | 新增 `RenderCache`，收拢重绘缓存与视图索引 | 结构 | 已落盘 | 同上 |
| B7 | 全类按 12 个 `#region` 分区（字段 + 方法） | 可读性 | 已落盘 | 同上 |
| B8 | public 成员签名与行为保持 | 约束 | — | `BackpackInteractionController.cs` 不动 |

---

## 三、逐项方案

### 3.1 内部类型与字段归属（B1–B6）

按 F1/F2 §3.2.1 的通用要求，全部嵌套在 `BackpackGridUI` 内、一律 `private`；`ItemWidget` 已存在，保留原地。语义相关的字段、缓存、订阅句柄及辅助方法 / 属性不得继续以平铺成员存在。

| 新类型 | 形态 | 收拢原成员 |
|---|---|---|
| `SlotVisual` | `sealed class` | `slots` + `slotImages` + `slotHoverStates` → `List<SlotVisual> slotViews`；字段 `root` / `image` / `hover` |
| `GridMetrics` | `readonly struct` | 无字段（值类型），由 `cellSize` + `slotSpacing` 现取现建 |
| `InputBinding` | `sealed class` | `toggleAction` / `primaryClickAction` / `secondaryClickAction` / `inputBound` / `inputWarned` |
| `WindowInputSnapshot` | `struct` | `inputWasEnabled` / `cursorWasVisible` / `cursorWasLocked` |
| `LocalDisplayState` | `sealed class` | `manualPositions` / `stackDisplayOverrides` / `pruneBuffer` / `hiddenItemId` / `hoveredItemId` |
| `RenderCache` | `sealed class` | `snapshot` / `displayItems` / `obsolete` / `itemsById` / `widgets` / `overflowMessage` |
| `ItemWidget` | `sealed class` | 已存在，保留 |

**形态取舍**：

1. `SlotVisual` 必须是 `class`：`List<struct>` 元素无法原地改字段（`slotViews[i].hover = …` 改的是副本），只能整体替换，更易错；格子数等于网格格数，仅在 `BuildGrid` 时分配，开销可忽略。
2. `GridMetrics` 必须是 `struct` 且不缓存实例：`cellSize` / `slotSpacing` 是 Inspector 序列化字段，运行时会被改动，缓存会失效；用 `private GridMetrics Metrics => new GridMetrics(cellSize, slotSpacing);` 现取现建，零堆分配。
3. `LocalDisplayState` / `RenderCache` 必须是 `class`：内部持 `List` / `Dictionary` / `StringBuilder` 复用缓冲，跨帧复用、不每帧新建。
4. `WindowInputSnapshot` 用 `struct`：值语义天然「采集一份快照」，避免与当前状态混用。

### 3.2 `GridMetrics` 公式等价对照（B2）

| 新方法 | 原表达式 | 原位置 |
|---|---|---|
| `PaneSize(columns, rows)` | `cellSize * n + spacing.x * (n - 1)` | `BuildGrid` 面板尺寸 |
| `LayerSize(columns, rows)` | `cellSize * n + spacing.x * Mathf.Max(0, n - 1)` | `EnsureItemLayer` / `EnsureCursorLayer` |
| `ItemPixelSize(width, height)` | `width * cellSize + spacing.x * (width - 1)` | `ItemPixelSize` |
| `CellToPixel(cellPosition)` | `new Vector2(x * (cellSize + spacing.x), -y * (cellSize + spacing.y))` | `CellToPixel` |
| `GrabCenterOffset(grabOffset)` | `grabOffset.x * (cellSize + spacing.x) + cellSize * 0.5f` | `TrySetCarriedPreviewScreenPosition` |
| `TryResolveLocal(local, columns, rows, out tile)` | 步距 `Mathf.Max(1f, cellSize + spacing)`、`Mathf.Clamp01` 象限判定 | `TryResolveTile`（局部点之后的纯数学） |

`TryResolveTile` 中「屏幕点 → 图层局部点」依赖 `RectTransformUtility` 与 `itemLayer`，**保留在 `MonoBehaviour`**，只把局部点之后的换算下沉；对外行为不变。

### 3.3 输入与开窗状态（B3–B4）

| 拆分依据 | `InputBinding` | `WindowInputSnapshot` |
|---|---|---|
| 生命周期 | 长期：启用即绑，宿主消失即退订，`Update` 逐帧重试 | 瞬时：开窗采集、关窗还原 |
| 归属 | 订阅句柄 + 每实例一次的告警 | 3 个还原值 |

`BindInputActions` / `UnbindInputActions` 改为委托 `InputBinding.TryBind(host, 三个回调)` / `InputBinding.Unbind()`（内部先退订再订阅，回调以同一委托实例增删）；`ApplyOpenState` 改为 `windowSnapshot = new WindowInputSnapshot(playerInput)` + `windowSnapshot.SuspendInput(playerInput)`，`RestoreInput` 改为 `windowSnapshot.Restore(playerInput)`。

**约束**：`InputBinding.Unbind()` 只增删回调，**绝不触碰映射启用状态**（宿主独占，原注释已明确）；宿主缺失时「只告警一次」的语义保留。

### 3.4 本机显示状态与重绘缓存（B5–B6）

`LocalDisplayState` / `RenderCache` 把原实现的方法体整体搬入，`BackpackGridUI` 的对应方法保留签名、一行转发。

| 原方法 | 去向 |
|---|---|
| `SetManualPosition` / `ClearManualPosition` / `SetStackDisplay` / `ClearStackDisplay` / `ClearAllStackDisplays` / `HasStackDisplayOverride` / `SetHiddenItem` / `ClearHiddenItem` / `SetItemHovered` | `LocalDisplayState` |
| `PruneManualPositions` / `PruneStackDisplayOverrides` / `ResolveDisplayStack` | `LocalDisplayState`（需 `RenderCache.itemsById` 作为入参） |
| `Refresh` 的快照收集、索引重建、待回收收集、物品框取用 | `RenderCache` |
| `UpdateOverflow` 的文本拼装缓冲 | `RenderCache`（`StringBuilder`） |

**保留的既有优化**：`SetSlotHoverState` 的「状态未变化直接 return」（避免多余 Canvas 重建）不变；`ApplySlotColors` 遍历上界由「两个列表取小」改为「单个列表长度」。

### 3.5 方法与属性分区（`#region` / `#endregion`，B7）

区名用中文，一层深度，`#region` 与 `#endregion` 成对；区域顺序固定为「序列化字段 → 内部类型 → 运行时状态与属性 → 生命周期 → 数据源绑定 → 能力方法 → 私有工具」。被取代的 `// ── M6 交互支撑 … ──` 横幅行移除。属性与方法必须按所属职责归区，不能在区域外散落；内部类型正文不再嵌套新的 `#region`。

| # | 区名 | 内容 |
|---|---|---|
| 1 | `序列化字段` | `slotPrefab` / `initialColumns` / `initialRows` / `cellSize` / `slotSpacing` / `slotLayer` / `itemLayer` / `hudHint` / `overflowText` / `uiFont` / `bindingCheckInterval` / `openKeyHint` / `cursorLayer` / `interaction` 及 `[Header]` |
| 2 | `内部类型` | `SlotVisual` / `GridMetrics` / `InputBinding` / `WindowInputSnapshot` / `LocalDisplayState` / `RenderCache` / `ItemWidget` |
| 3 | `运行时状态与属性` | `slotViews` / `Metrics` / `Inventory` / `ItemViews` / `Columns` / `Rows` / `IsOpen` / `playerInput` / `dataSubscription` / `dataDirty` / `nextBindingCheck` / `binding` / `windowSnapshot` / `local` / `render` / `rectTransform` / `layout` / `carriedPreview` |
| 4 | `生命周期` | `Awake` / `Start` / `OnEnable` / `Update` / `LateUpdate` / `OnDisable` / `OnDestroy` |
| 5 | `数据源绑定` | `ResolveSource` / `BindInventory` / `SyncGridSize` / `Unbind` / `OnInventoryChanged` / `DescribeSource` / `OpenKeyLabel` |
| 6 | `网格构建与图层` | `BuildGrid` / `EnsureItemLayer` / `EnsureCursorLayer` |
| 7 | `重绘管线` | `Refresh` / `Redraw` / `UpdateOverflow` / `ApplySlotColors` / `SetSlotHoverState` / `SlotColor` / `IsPlaced` |
| 8 | `输入与开关` | `BindInputActions` / `UnbindInputActions` / `OnToggleAction` / `OnPrimaryClickAction` / `OnSecondaryClickAction` / `PushClick` / `Toggle` / `ApplyOpenState` / `RestoreInput` / `SetWindowVisible` / `SetHint` |
| 9 | `本机显示状态` | `SetManualPosition` / `ClearManualPosition` / `SetStackDisplay` / `ClearStackDisplay` / `ClearAllStackDisplays` / `HasStackDisplayOverride` / `SetHiddenItem` / `ClearHiddenItem` / `SetItemHovered` / `ClearHoverHighlight` / `ApplyRegionHighlight` / `CopyItemIds` / `TryGetItem` / `TryGetPlacement` |
| 10 | `携带预览` | `TryShowCarriedPreview` / `SetCarriedPreviewCellPosition` / `TrySetCarriedPreviewScreenPosition` / `HideCarriedPreview` |
| 11 | `M6 查询与几何换算` | `TryGetCellOwner` / `QuerySpace` / `TryResolveTile` / `GetUpperLeftIndex` |
| 12 | `物品框视图工具` | `CreateWidget` / `ApplyWidget` / `ApplyWidgetHover` / `SetWidgetAlpha`（UI3 起物品框改为实例化 `itemPrefab`，`CreateRect` / `CreateText` 已删除，见 `UI3_物品框改用itemPrefab.md`） |

分区规则是本次方案的结构约束，而不是注释建议：新增属性必须进入对应职责区；新增方法先归入既有职责区，只有职责边界确实新增时才扩展区域表。每个 `#region` 必须有且只有一个匹配的 `#endregion`，区域不得跨越 `BackpackGridUI` 或任何内部类型的边界。

### 3.6 public API 不变承诺（B8）

`BackpackInteractionController.cs` 实际用到的成员（全文枚举，共 28 项），本次整理**签名与语义全部不变**，控制器零改动：

`Columns`、`IsOpen`、`TryGetItem`、`TryGetPlacement`、`TryGetCellOwner`、`QuerySpace`、`GetUpperLeftIndex`、`TryResolveTile`、`SetManualPosition`、`ClearManualPosition`、`SetStackDisplay`、`ClearStackDisplay`、`ClearAllStackDisplays`、`HasStackDisplayOverride`、`SetHiddenItem`、`ClearHiddenItem`、`CopyItemIds`、`Redraw`、`SetItemHovered`、`ClearHoverHighlight`、`ApplyRegionHighlight`、`TryShowCarriedPreview`、`SetCarriedPreviewCellPosition`、`TrySetCarriedPreviewScreenPosition`、`HideCarriedPreview`（另 `isActiveAndEnabled` / `GetComponentInParent<Canvas>` 来自 Unity 基类）。

---

## 四、明确不做

| 项 | 理由 |
|---|---|
| 拆分为多个 `.cs` 文件 | 本次要求「内部类 / 内部结构体包装」，保持单文件内聚 |
| 改 `public` 命名或可见性 | 40+ 处调用点；且 UI1 的 C3 / C4 已规划对同文件做结构改造，此时改签名会与后续方案冲突 |
| 新增 `public` / `internal` 成员 | 不扩大对外契约 |
| 合并 `BackpackGridUI` 与 `BackpackInteractionController` | UI1 第八节已否决 |
| 实现 UI1 的 C3 / C4（焦点栈、`BackpackWindowCore`） | 属后续里程碑，不在本轮 |
| 修正 M6 文档中的过期描述 | 留给 UI1 统一回填 |
| 额外添加解释性注释 / 跑单元测试 | 纯重构；工程无测试工程，只做编译核对 |

---

## 五、验收清单

1. 结构：`BackpackGridUI.cs` 内新增 6 个 `private` 嵌套类型；`List<SlotVisual> slotViews` 取代三个平行列表；平铺私有字段由约 30 个降为约 8 组。
2. 分区：全文 12 个 `#region` / `#endregion` 成对，顺序与 3.5 表一致；字段、属性、方法均位于对应职责区；元素内零残留 `// ──` 分段横幅。
3. 语义：`BackpackInteractionController.cs` **零改动**（diff 为空）；public 成员签名与序列化字段名逐字未变。
4. 公式：3.2 表中 6 处换算逐字等价（步距下限、`Mathf.Max(0, n-1)` 差异、象限 `Clamp01` 口径均保留）。
5. 性能：`GridMetrics` 为值类型、零 GC；`RenderCache` / `LocalDisplayState` 的缓冲全部跨帧复用，未新增每帧分配。
6. 编译：`dotnet build RPG4.Backpack.UI.csproj` 0 错误（Mirror 既有警告 1 条不变）。
7. Unity 侧：`New Scene.unity` 的组件引用与 Inspector 赋值不丢（GUID `e51a42c8…` 未变，字段名未改）。

---

## 六、待决策项

1. 是否把转发入口改成属性分组风格（`grid.Local.SetPosition(…)` / `grid.Carry.Show(…)`），需同步改控制器 20 余处调用点。**本轮不做**，避免新旧两套写法并存。
2. `#region` 区名是否改用英文（影响 Rider / VS 折叠显示与团队检索习惯）。
3. 是否把 `GridMetrics` 提升为程序集内可见（供 `BackpackInteractionController` 复用同一套换算，消除两侧重复）。**本轮不做**，需另评控制器改动面。
