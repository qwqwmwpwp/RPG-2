# UI1 面板接口与多窗口支持（变更文档）

> 状态：**待实施**——本文件只描述变更范围与方案，不含实现。
> 日期：2026-10-06
> 影响面：`Assets/backpack/Scripts/UI/`；`Scripts/Inventory/` 只读，不改。
> 关联文档：`Docs/里程碑/M6_点击拾取与堆叠操作.md`、`Docs/核心契约/S1_单机背包宿主与索引.md`

---

## 一、背景（已完成，作为基线）

| 项 | 结果 |
|---|---|
| 删除文件 | `BackpackItemDescriptionPanel.cs` + `.meta`、`BackpackItemPopupView.cs` + `.meta`、`BackpackItemDescriptionContent.cs` + `.meta`（GUID `2e4e4810…` / `533d8d83…` / `0a034e7a…` 已释放） |
| 控制器瘦身 | `BackpackInteractionController` 删 `description` 字段、`DescriptionDelay`、`describedItemId`、`describedSince`、`UpdateDescription()`、`IsPointerOverPanel()`、`ResetPointerState` 的 `keepDescription` 形参，连带 8 处调用点 |
| 每帧链收敛 | 指针缓存 → 悬停物品 → 悬停高亮 → 携带预览 → 输入分派 |
| 未动 | 场景 `New Scene.unity`（留 2 个 missing script 占位节点）、M6 文档、`BackpackHoverState` |
| 验证 | 全仓代码零残留引用（仅 M6 文档仍提及旧类型名）；`dotnet build RPG4.Backpack.UI.csproj` 0 错误（1 个 Mirror 既有警告） |

---

## 二、变更总览

| 编号 | 变更 | 类型 | 状态 | 主要影响文件 |
|---|---|---|---|---|
| C1 | 修正 `Update` 摘要中残留的「描述面板」字样 | 注释 | 待做 | `BackpackInteractionController.cs` |
| C2 | 控制器补只读查询面 + 静态 `Instance` | 新增 API | 待决策范围 | `BackpackInteractionController.cs` |
| C3 | 多窗口基础三件套：焦点栈 / 光标引用计数 / 来源可配 | 结构 | 待实施 | `BackpackGridUI.cs` + 2 个新文件 |
| C4 | 抽取 `BackpackWindowCore`（窗口骨架，组合式） | 结构 | 第二阶段 | 新文件 + `BackpackGridUI.cs` |
| C5 | 开箱容器接入 | 功能 | 待决策路线 | 待定 |

---

## 三、C1 注释修正

**问题**：`BackpackInteractionController.cs:117` 的 `Update` 摘要仍写「…携带预览、**描述面板**与输入分派；会话不可交互或指针被**面板拦截**时提前返回」，两项均已不存在。

**改法**（单行替换）：

```csharp
/// <summary>每帧入口：先处理认领与显示覆盖的到期兜底，再依次预计算坐标、悬停反馈、携带预览与输入分派；会话不可交互时提前返回。</summary>
```

---

## 四、C2 控制器只读查询面

**动机**：自研面板需要「当前悬停谁 / 有没有拿着东西 / 该不该显示」，而控制器已不再持有面板类型，须由面板主动查询。

**接口草案**：

| 成员 | 用途 |
|---|---|
| `static BackpackInteractionController Instance` | 面板取实例；`Awake` 建立，`OnDestroy` 判 `Instance == this` 后清空 |
| `ItemId HoveredItemId` | 本帧悬停标识，无效为 `ItemId.None` |
| `float HoveredSince` | 当前悬停目标的确定时刻（延迟显示的计时基准） |
| `bool TryGetHoveredItem(out RuntimeItem item)` | 悬停物品实例，内部走已有 `grid.TryGetItem` |
| `bool IsCarrying` / `int CarriedCount` / `bool IsCarryingPartial` | 携带中不显示描述 |
| `bool IsSessionActive` | 数据源与网格就绪且窗口打开 |

**实现要点**：

1. `hoveredSince` 在 `UpdateHoveredItem()` 的 `if (next != hoveredItemId)` 分支里记一次时刻（与面板类型无关的通用状态）。
2. 多实例语义：控制器与窗口同物体，**不能**学 `InputManager` 那样 `Destroy(gameObject)`（会连窗口一起删）；采用「先到者胜 + Warn，后到者不覆盖 `Instance`」。
3. 全部只读、零分配；不新增任何「无人读」成员。
4. 悬空项：原 `IsPointerOverPanel` 的**点击穿透保护**要不要以 `IBackpackPointerBlocker` 形式复刻——见第十节。

**面板侧最小用法**（供参考，非本次实现）：

```csharp
void LateUpdate()
{
    BackpackInteractionController ctrl = BackpackInteractionController.Instance;
    if (ctrl == null || !ctrl.IsSessionActive || ctrl.IsCarrying) { Hide(); return; }

    if (ctrl.HoveredItemId != tracked) { tracked = ctrl.HoveredItemId; Hide(); return; }
    if (!tracked.IsValid) return;
    if (IsVisible) { FollowPointer(/* 指针坐标自取 InputManager */); return; }
    if (Time.unscaledTime - ctrl.HoveredSince < Delay) return;
    if (!ctrl.TryGetHoveredItem(out RuntimeItem item)) return;
    Show(item);
}
```

指针坐标面板直接从 `InputManager.Instance.backpackInput.pointerPosition` 读，不经控制器（少一条依赖）；延迟秒数（策略）留在面板，控制器只给「悬停起点」（事实）。

---

## 五、C3 多窗口基础三件套（第一阶段）

多窗口必然冲突的三处，先解掉，改动量约 80 行。

### 5.1 输入焦点栈

**问题**：每个 `BackpackGridUI` 各自订阅 `backpack/BackPackUI`、`leftButton`、`rightButton`；两个窗口 = 双订阅——B 键同时开关两个窗口，左右键两个控制器都会按各自那侧的悬停处理。

**方案**：新增 `BackpackUiFocus`（静态）：

1. 维护「已打开窗口」的栈（后开的在上）。
2. 每个窗口的输入回调首行判 `BackpackUiFocus.ShouldHandle(this)`，非栈顶直接返回。
3. 窗口关闭 / `OnDisable` 时出栈；不引入完整路由器，订阅方仍各自持有。

### 5.2 光标与 `PlayerInput` 引用计数

**问题**：`ApplyOpenState` / `RestoreInput` 每个窗口各存一份 `inputWasEnabled` / `cursorWasVisible`，两窗口互相覆盖（后开的保存的是「已被前者改过」的状态，关闭顺序不一致时会还原错）。

**方案**：新增 `BackpackCursorScope`（静态）：

1. 只由**第一个**打开的窗口记录原始状态；
2. 只由**最后一个**关闭的窗口还原；
3. `PlayerInput` 暂停与 `Cursor` 可见性 / 锁定同口径。

### 5.3 数据源可配

**问题**：`ResolveSource()` 写死「联机本地玩家优先、单机 `BackpackRegistry.Primary` 兜底」，且 `Update` 每 `bindingCheckInterval`（默认 0.25s）重查一次，箱子窗口会被改回玩家的包。

**方案**：

1. `BindInventory(IBackpackView)` 由 `private` 提为公开入口（建议命名 `SetSource`）。
2. 新增 `[SerializeField] bool autoResolveSource = true`；为 `false` 时跳过重查循环。
3. 默认值保持现状，玩家背包窗口行为零变化。

---

## 六、C4 `BackpackWindowCore`（第二阶段，暂不实施）

**触发条件**：出现第二种窗口（M7 装备槽 / 商店 / 拾取）。

**形态**：普通类（非 `MonoBehaviour`）、组合持有、构造注入 + 委托接缝，对齐 `InventoryHostCore` 既有范式；**零继承**。

**职责**（从 `BackpackGridUI` 迁出，约 300 行）：

1. 数据源绑定 / 解绑 / 脏标记帧末合并刷新（刷新动作由窗口注入）
2. 来源解析策略（可留空 = 不自动解析）
3. 输入订阅生命周期与焦点判定
4. 开关状态 + 光标 / `PlayerInput` 接管（走 5.2 的引用计数）
5. CanvasGroup 显隐与 HUD 提示文案

**接缝**：`Func<IBackpackView> ResolveSource`、`Action OnRefresh`、`Action<int> OnClick`、`Action<bool> OnOpenChanged`、`Func<bool> CanOpen`。

**驱动方式**：窗口 `Update` → `core.Tick()`、`LateUpdate` → `core.LateTick()`（沿用「宿主驱动」口径）。

**边界**：网格构建、物品框渲染、`InventoryGridLayout` 排布、本地位置表、悬停高亮、携带预览、显示数量覆盖**不进** Core（背包窗口专属，约 700 行）。

---

## 七、C5 开箱容器接入（待决策）

**现状**：数据层与事件层**天然支持多容器**——每份数据源有自己的 `EventSource`、各发各的 `Inv.Changed`；卡点全在 UI 层。

| 卡点 | 说明 |
|---|---|
| 来源写死 | 见 5.3 |
| 控制器单网格 | `QuerySpace` / `TryGetItem` / `SetManualPosition` 全走单一 `grid` 字段 → **跨容器拖拽未实现**，落点永远算在自己这一侧 |
| 意图单去向 | `PublishSetStackCount` / `PublishRemoveItem` / `PublishSplitStack` 固定用绑定时那一个 `eventSource`；跨容器合并需两条命令各带各的容器键 |
| 输入双触发 | 见 5.1 |
| 注册表 | `BackpackRegistry` 只登记单机宿主 `BackpackInventory`；联机版 `BackpackNetworkInventory` 无索引 |

**三条路线**：

| 路线 | 说明 | 改动量 |
|---|---|---|
| 甲（推荐先做） | 不同屏：打开箱子时窗口**切换绑定**到箱子的包，交互链路零改动 | 小（仅 5.3） |
| 乙 | 同屏两栏：两窗口各带控制器 + 跨容器落点判定 + 按容器分别投递意图 | 大 |
| 丙 | 一窗双格：一个窗口两套网格、共用控制器（控制器持有「己方 / 对方」两个格子） | 中，动控制器核心 |

---

## 八、明确不做

| 项 | 理由 |
|---|---|
| 合并 `BackpackGridUI` 与 `BackpackInteractionController` | 合并后 1891 行单文件；`[DefaultExecutionOrder(100)]` 的时序约定消失（需手工维护「刷新 → 决策」顺序）；决策层与表现层混层；挡住双窗口 |
| 抽 UI 父类（继承） | 双窗口是同一个类的两份实例，用不上继承；Unity 单继承会锁死后续扩展；项目 UI 层现为**零继承** |
| 清理 `BackpackHoverState.TextMuted` | 失去唯一调用方但无副作用，超本轮范围 |
| 同步 M6 文档的过期描述 | 待本变更落地后统一回填 |

---

## 九、验收清单

1. C1：`BackpackInteractionController` 全文检索「描述面板」「面板」零命中。
2. C2：`dotnet build RPG4.Backpack.UI.csproj` 0 错误；`Instance` 在 `Awake` / `OnDestroy` 成对建立与清空。
3. C3：开一个窗口 → 光标与 `PlayerInput` 被接管；开两个 → 只有栈顶收 B 键与左右键；全关 → 状态还原为打开前。
4. C3：`autoResolveSource = false` 的窗口连续运行 5 秒，绑定对象不被改写（日志中无重复的「已绑定本地玩家库存」）。
5. 场景与 Unity 侧：刷新编译 + 人工核对（本项目零单测口径）。

---

## 十、待决策项

1. C2 是否复刻点击穿透保护（`IBackpackPointerBlocker`）。
2. 自研面板取数走「控制器状态」还是「主控快照」。
3. C5 选甲 / 乙 / 丙；箱子是**同机对象**还是**联机对象**（决定 `BackpackRegistry` 是否要扩到网络宿主）。
4. C3 与 C4 的节奏：只做第一阶段 / 一 + 二阶段一起 / 先只做 5.3。
