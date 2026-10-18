# 背包系统架构（当前实现）

> 范围：`Assets/backpack`。核对日期：2026-10-18。以当前代码与场景为准。

---

## 1. 概览

背包系统同时支持**单机**与 **Mirror 联机**，核心特征：

- **权威核心纯 C#**：`InventoryContainer` 持有唯一物品集合与占格映射，`InventoryHostCore` 负责宿主共享编排；两者不依赖 UI、MonoBehaviour、Mirror。
- **数据与表现分离**：UI 通过 `IBackpackView` 读数据、通过事件总线发意图，不直接拥有库存。
- **两套位置**：容器保存用于容量校验的**权威占格**；UI 另有一份可自由拖动的**本地排布**，不上网。
- **服务端权威、客户端镜像**：服务端持容器；客户端只持由 `SyncList<NetworkInventoryEntry>` 恢复的只读 `RuntimeItem` 镜像。
- **原子提交**：先校验 / 规划、再一次性提交；跨容器转移与拆分事务失败时双方零变更。
- **同步事件、方向单一**：客户端意图 → Command → 服务端复核 → 容器提交 → 投影 → 客户端镜像。

当前可用交互：单栏拾取 / 放下 / 堆叠 / 交换 / 右键拆分；双栏玩家-箱子空位转移与拆分。装备、消耗、丢弃、拾取尚未接入。

---

## 2. 分层与程序集

| 程序集 | 目录 | 职责 |
|---|---|---|
| `RPG4.Backpack.Core` | `Scripts/Core` | 事件总线、标签、网格类型 / 工具、日志 |
| `RPG4.Backpack.Items` | `Scripts/Items` | 片段、物品清单、运行时物品、数值快照 |
| `RPG4.Backpack.Inventory` | `Scripts/Inventory` | 权威容器、放置记录、宿主共享核心、视图契约 |
| `RPG4.Backpack.Equipment` | `Scripts/Equipment` | 预留，当前只有 asmdef |
| `RPG4.Backpack.Runtime` | `Scripts/Runtime` | 单机 / 场景宿主与本地玩家索引 |
| `RPG4.Backpack.Network` | `Scripts/Network` | Mirror 生命周期、服务端权威、客户端镜像、投影 |
| `RPG4.Backpack.UI` | `Scripts/UI` | 窗口、网格表现、输入与交互 |

```text
Core ← Items ← Inventory ← { Runtime, Equipment, Network } ← UI
                                  Network 额外依赖 Mirror
                                  UI 额外依赖 Input / Mirror / UI / TMP / InputSystem
```

约束：Core 不反向依赖业务层；Items 不感知容器；Inventory 不引用 Unity UI / Mirror；UI 只依赖接口与事件。

---

## 3. 数据模型

### 3.1 标签

`BackpackTag` 是点分层级值类型（如 `GameItems.Equipment.Weapons.Sword`）：

- `MatchesTagExact`：整串序数相等，用于物品类型、堆叠合并、目录查找。
- `MatchesTag`：层级包含。
- 空标签在匹配 API 中恒为 `false`；标签权威来源是代码常量与资产字段，`TagRegistry` 只做校验 / 下拉 / 层级枚举。

### 3.2 片段

```text
ItemFragment
├─ GridFragment             宽 / 高 / padding；缺省按 1×1
├─ StackableFragment        单堆上限；缺省不可堆叠
└─ InventoryItemFragment
   ├─ ImageFragment         图标
   ├─ TextFragment          说明文本
   ├─ LabeledNumberFragment 可随机化数值（唯一进入数值快照的类型）
   │  ├─ ConsumeModifier
   │  └─ EquipModifier
   ├─ ConsumableFragment    Modifiers
   └─ EquipmentFragment     equipmentType / equipPrefab / socket / Modifiers
```

`ItemManifest`（ScriptableObject）保存类型 + 片段模板：

- 创建：克隆片段并首次随机化。
- 恢复：结构取自模板、数值来自快照，**不重新随机化**。
- `ItemManifestCatalog` 按类型标签索引清单；客户端恢复、宿主拆分都必须能取到模板。

### 3.3 运行时物品

`RuntimeItem` = `ItemId` + `ItemType` + 片段列表 + `StackCount`。

- `ItemId` 不可变；`0` 表示未登记；由 `ItemIdGenerator` 发放，耗尽时抛异常不回绕。
- 堆叠数唯一来源是 `RuntimeItem.StackCount`；`SetStackCount` 只校验 `1..MaxStackSize`。
- `RuntimeItem` 未重写相等，容器字典按**引用**区分实例；跨网络更新后应按 `ItemId` 重新查询。
- 单机共享 `InventoryHostCore.SharedGenerator`；联机注入 `BackpackNetworkManager.ServerItemIds`。

### 3.4 数值快照

`RuntimeItemSnapshot` 只包含：`ItemId + ItemType + StackCount + 数值节点`。

二进制格式（`RuntimeItemSnapshotCodec`）：

| 区域 | 字节 | 内容 |
|---|---:|---|
| Header | 1 + 2 | 版本（当前 1）+ 节点数（小端） |
| Node | 2 + 2 + 4 | 片段下标 + Modifier 下标（-1 = 自身）+ float 位模式 |

限制：最多 64 个节点、最大 515 字节、类型标签最长 128。解码严格校验版本、长度、节点数、有限数值与尾随字节；应用数值时要求节点顺序与模板一致且落在 `Min..Max`。

---

## 4. 库存核心

### 4.1 `InventoryContainer`

唯一权威状态：

1. `Dictionary<RuntimeItem, InventoryPlacement>`：物品 → 放置记录。
2. `InventoryPlacement[] cells`：逐格占用；空格为 `Invalid`。
3. `Revision`：每次成功提交 +1，供网络投影判断变更。

不变量：

- 两份状态只在提交阶段同源写入，不存在中间态。
- 放置记录冻结尺寸；移除按冻结尺寸释放，不重新读模板。
- 行优先索引：`index = y * Columns + x`；上限 `65536` 格。
- 主线程单线程、不加锁；查询尽量复用缓冲。
- 不提供原地移动；移动是 UI 本地行为。

主要能力：

- 查询：`FillItemsByItemId` / `TryGetItem` / `IndexOf` / `TryGetItemById` / `TryGetPlacement` / `TryGetCellIndices`。
- 空间：`QuerySpace`（落点全空 / 单个挡路物品 / 多物品阻塞）、`QueryAvailability` / `FillAvailability`（行优先容量）。
- 变更：`TryPlaceItem` / `TryRemoveItem` / `TryStackItem` / `TrySetStackCount` / `TryReceiveItem` / `TryTransferItem` / `TryTransferItemToEmptySpace` / `TrySplitStackToEmptySpace`。
- 自检：`TryValidateMapping`。

接收规则：先按行优先合并同类堆叠（类型精确匹配、双方可堆叠、未满），剩余量放入首个合法空白矩形；整批成功或整体失败。

原子性：变更先完整校验 / 规划，再一次性提交。跨容器转移在提交前对目标做完整规划；拆分事务可在提交后调用确认钩子，确认失败则撤销新实例、恢复来源堆叠并补偿确认。

### 4.2 `InventoryHostCore`

单机与联机共用：

- 创建 / 持有权威容器（纯客户端不创建）。
- 提供 `Catalog`、`IdGeneratorProvider`、`IsAuthority`。
- 订阅 `SetStackCount` / `RemoveItem` / `SplitStack` 意图：权威端直接执行，非权威端通过 `Forward*` 转发。
- 权威操作：`TryAddItem`（分配标识 + 创建 + 接收）、`TrySetStackCount`、`TryRemoveItem`、`TrySplitStack`、`TrySplitStackToEmptySpace`。
- 提交后调用 `CommitConfirm`；联机时它就是投影同步。确认失败按操作口径回滚：新增 / 设堆叠 / 拆分回滚，移除不回滚。

### 4.3 `IBackpackView`

```csharp
int ConfiguredRows { get; }
int ConfiguredColumns { get; }
object EventSource { get; }
void CopyItems(List<RuntimeItem> destination); // 按 ItemId 升序
```

`EventSource` 同时用于订阅 `Inv.*` 和发布 `Cmd.*`：单机 / 服务端 / 主机是**容器实例**；纯客户端是**宿主自身**。这是同一套 UI 能适配两种形态的关键。

---

## 5. 事件与命令

`EventBus` 按 **频道 + 来源** 定向投递，来源按引用相等：

- 同步派发；回调异常被隔离；单个订阅异常不影响其他订阅者。
- 监听者弱引用，Unity 对象销毁后惰性剔除；`listener = null` 为常驻强订阅并记警告。
- 订阅签名必须与发布泛型参数一致，不一致跳过并记 `Warn`。
- 仅主线程；派发期间新增订阅不参与本轮，派发结束后才压实集合。

频道（`EventChannels.cs`）：

| 频道 | 载荷 / 语义 |
|---|---|
| `Inv.Item.Added` / `Inv.ItemRemoved` | 物品进入 / 离开容器；当前无订阅者 |
| `Inv.Stack.Changed` | 堆叠变化（`SlotAvailabilityResult`）；当前无订阅者 |
| `Inv.Changed` | 粗粒度脏信号；UI 置脏、帧末合并重绘 |
| `Cmd.Item.SetStackCount` / `Cmd.Item.Remove` / `Cmd.Item.Split` | 已接入的写入意图 |
| `Cmd.Item.Drop` / `Cmd.Item.Consume` / `Cmd.EquipSlot.Clicked` | 已定义，无处理者 |
| `Ui.*` | 已定义；当前点击走 `PushClick` 直接调用，未使用 |

主数据流：

```text
UI → Publish(Cmd.*, source = EventSource)
   → InventoryHostCore
       ├─ 权威端：直接执行
       └─ 非权威端：Forward* → Request* → [Command] → 服务端
   → InventoryContainer 校验 / 规划 / 原子提交 → 广播 Inv.*（source = 容器）
   → UI 置脏 → LateUpdate 重绘
```

纯客户端没有容器：服务端容器变化 → `SyncList` → 客户端镜像 → `BackpackNetworkInventory.LateUpdate` 代发 `Inv.Changed`（source = 宿主）→ UI 重绘。

例外：双栏转移 / 拆分由交互层直接调用权威容器 / 宿主 API，不经过 `Cmd.*`（见 8.4）。

---

## 6. 单机运行时

- `BackpackInventory`：组合 `InventoryHostCore`；`Awake` 创建容器，`OnEnable` 订阅命令；`EventSource = Container`；尺寸非法回退 6×8。
- `CharacterBackpack`：角色归属；`isLocalPlayer` 时登记到 `BackpackRegistry`；含测试用 ContextMenu。
- `ChestInventory`：箱子归属；不登记到 Registry，由场景引用或双栏窗口指定。
- `BackpackRegistry`：`Primary` 本地玩家背包；重复登记保留先注册者。

启动链：

```text
BackpackInventory.Awake → 核心 + 容器
BackpackInventory.OnEnable → 订阅命令
CharacterBackpack.OnEnable → 登记 Primary
BackpackGridUI.Update（每 0.25s）→ 解析本地玩家 → 绑定显示与交互
BackpackInteractionController.Bind → 订阅 Inv.Changed
```

---

## 7. 网络层

### 7.1 拓扑

```text
服务端 / 主机
  ├─ InventoryHostCore + InventoryContainer（权威）
  ├─ SyncList<NetworkInventoryEntry>
  └─ ServerItemIds 发放物品标识

纯客户端
  ├─ InventoryHostCore（无容器，只转发意图）
  ├─ SyncList（只读）
  └─ 客户端镜像 Dictionary<ulong, RuntimeItem>
```

数据流单向：客户端意图 → Command → 服务端复核 → 容器提交 → 投影差异 → 客户端镜像。不允许反向写回。

### 7.2 组件

| 组件 | 职责 |
|---|---|
| `BackpackNetworkManager : NetworkManager` | 网络生命周期日志；`OnStartServer` 创建 `ServerItemIds`，`OnStopServer` 释放；可配置自动 Host / Client / Server |
| `BackpackNetworkPlayer : NetworkBehaviour` | 网络身份与归属配色；不含移动 / 输入 / 库存 |
| `BackpackNetworkInventory : NetworkBehaviour, IBackpackView` | 服务端权威容器 / 纯客户端镜像；命令收口；投影同步；`Changed` 事件与诊断 |
| `InventoryNetworkProjection` | 纯 C# 投影：抓条目、恢复物品、容器差异同步、整体重建镜像 |
| `NetworkInventoryEntry` | 同步条目：`itemId / itemType / stackCount / fragmentState`（public 字段供 Weaver） |

生命周期：

- 服务端：创建核心与容器；`CommitConfirm = SyncEntries`；订阅命令（source = 容器）；客户端连接后通过 `SyncList` 收到快照。
- 纯客户端：创建无容器核心；`IsAuthority = false`；`RebuildMirror()`；订阅命令（source = 自身）；条目回调增量维护镜像。
- 主机：`isServer == true`，不建镜像，权威容器就是本地视图。

### 7.3 同步内容与投影

只同步：标识、类型标签、堆叠数、数值快照。不同步：网格位置、Unity 引用、非数值模板字段、片段对象。

- 客户端凭自己的 `ItemManifestCatalog` 按类型恢复模板，应用快照数值，不重新随机化。
- `SyncContainer` 只产生实际 Add / Set / Remove；`StateEquals` 逐字节比较载荷，未变化不下发。
- 投影拒绝（标识无效、载荷不可编码、模板缺失）计入 `DesyncCount` 与 `LastError`。
- 服务端操作只有在 `requiredItem` 成功进入投影时才算成功，否则核心回滚。
- 外部直接改服务端容器后必须调用 `SyncNow`。

### 7.4 客户端命令

`CmdRequestSetStackCount` / `CmdRequestRemoveItem` / `CmdRequestSplitStack`，均 `requiresAuthority = true`。发送前检查：客户端已启动、`isOwned`、标识有效。

拆分由服务端创建新实例：取模板 → 抓源快照 → 新标识恢复（数值继承）→ 找空白区域 → 扣减来源并放置 → 投影。

### 7.5 当前限制

- 位置不同步；本地拖动仅本机可见。
- 纯客户端无容器，双栏转移 / 拆分不可用。
- 双栏“整件转移”直接调用容器 API，不经过 `CommitConfirm`；主机 / 服务端形态需要额外 `SyncNow` 补齐投影。
- 当前网络组件为裁剪版：不含拾取；装备 / 消耗网络分片未编入。
- 客户端目录必须与服务端模板一致（数值节点数量 / 顺序一致）。

---

## 8. UI 与交互

### 8.1 组件

| 组件 | 职责 |
|---|---|
| `BackpackGridUI` | 单栏窗口兼容壳：解析本地玩家、开关、输入、光标、CanvasGroup 显隐 |
| `BackpackContainerUI` | 可复用网格显示：格子、物品框、溢出提示、携带预览、本地显示状态 |
| `InventoryGridLayout` | 纯本地排布：`ItemId` 排序 → 显式位置 → 上次位置 → 行优先寻位 |
| `BackpackItemUI` | 物品框：图标、数量、悬停色、透明度 |
| `BackpackInteractionController` | 单栏 / 双栏交互：拾取、放下、堆叠、交换、右键拆分 |
| `ChestBackpackUI` | 双栏窗口：玩家 + 箱子，共用携带会话与光标层 |

### 8.2 绑定与重绘

单栏数据源优先级：

1. `NetworkClient.localPlayer` 上的 `BackpackNetworkInventory`；
2. `BackpackRegistry.Primary`；
3. 无 → 窗口保持未绑定，每 0.25s 重试。

重绘链：

```text
Inv.Changed → dataDirty
→ LateUpdate → Redraw
→ CopyItems → GridFragment 尺寸 → InventoryGridLayout.Arrange
→ 格子配色 → 回收已消失 / 放不下的物品框
→ 复用 / 创建物品框 → 位置、尺寸、图标、数量、悬停 → 溢出文本
```

### 8.3 本地显示状态

`BackpackContainerUI` 保存四类**不上网、不进容器**的状态：

| 状态 | 用途 |
|---|---|
| `manualPositions` | 物品本地落点；拖动 / 交换 / 拆分认领后写入 |
| `stackDisplayOverrides` | 右键拆分时源物品显示“总数 − 已取” |
| `HiddenItemId` | 携带期间隐藏但仍占格 |
| `HoveredItemId` | 悬停高亮 |

**权威占格 ≠ 显示位置**：容器放置记录用于容量 / 转移 / 拆分校验；UI 用本地排布显示。单栏拖动只改本地位置，所以“界面看起来放得下”不一定等于服务端一定接受，失败会通过命令拒绝、拆分认领超时或显示覆盖超时体现。

### 8.4 交互

单栏：

| 操作 | 行为 | 触达数据层 |
|---|---|---|
| 左键点物品 | 拾起，本地隐藏 | 否 |
| 左键点空位 | 写本地位置 | 否 |
| 左键点同类可堆叠 | 合堆 / 部分填充 / 必要时移除 | 是（Cmd） |
| 左键点异类 / 不可堆叠 | 本地交换位置 | 否 |
| 左键点同类已满 | 交换堆叠数；双方都满则退化为本地交换位置 | 交换堆叠数为 Cmd |
| 右键可堆叠物品 | 部分携带，每次再取 1 个 | 否 |
| 部分携带落空位 | `SplitStack`，回传后认领新标识并写本地位置 | 是（Cmd） |
| 部分携带落同类 | 合堆 / 必要时移除 | 是（Cmd） |
| 整件携带右键 / ESC | 取消携带 | 否 |

部分携带的拆分认领超时 0.5s；提交后显示覆盖最多保留 1s，超时还原库存真值。

双栏：

- 右键从目标堆叠取 1 个；继续右键源物品再取 1 个。
- 左键：空位放下；部分携带 → 拆分到目标空位；跨栏整件 → `TryTransferItemToEmptySpace`（不合堆）。
- 需要两侧都能拿到 `InventoryContainer`，因此仅单机 / 服务端 / 主机可用。
- 目标栏为网络库存时，拆分传入 `ConfirmSplitProjection`；整件转移不经过 `CommitConfirm`，需 `SyncNow` 补齐投影。

### 8.5 悬停、输入与窗口

- 悬停四态：`None` / `Available`（绿）/ `Blocked`（红）/ `Swap`（黄）。
- 落点与悬停全部由指针坐标 + 网格几何计算，不依赖 uGUI 射线；格子 `raycastTarget = false`。
- 输入来自 `InputManager.backpackInput`：`BackPackUI`、`leftButton`、`rightButton`、`cancel`、`pointerPosition`。
- 打开窗口时采集并暂停 `PlayerInput`、解锁光标；关闭时原样还原。
- 双栏窗口打开会关闭单栏窗口，并用 `BlocksPlayerBackpackInput` 阻止同帧重复打开。

---

## 9. 关键不变量

1. 物品集合、权威占格、`Revision` 只在 `InventoryContainer`；堆叠数只在 `RuntimeItem`。
2. 变更先规划后提交，失败即零变更；放置记录尺寸冻结。
3. 接收 / 转移先合堆再落格；类型必须 `MatchesTagExact`。
4. 物品标识由权威端发放；容器拒绝重复标识；客户端不接受 `None` 条目。
5. 本地位置、显示覆盖、携带隐藏、悬停态都不进容器、不上网。
6. 事件订阅 / 发布必须使用同一 `EventSource`；频道键只能来自 `EventChannels.cs`。
7. 主线程单线程访问，不加锁；`EventBus` 同步派发。
8. 客户端不创建 `RuntimeItem`、不发放标识、不直接改容器；服务端投影成功才算操作成功。
9. 跨网络更新后不要长期持有 `RuntimeItem` 引用，按 `ItemId` 重新查询。

---

## 10. 配置、当前范围与关键文件

### 10.1 资产与场景

- 物品模板：`Data/Items/*.asset`；目录：`Data/Items/物品清单目录.asset`；标签：`Data/TagRegistry.asset`；日志：`Data/LogSettings.asset`。
- 当前场景：
  - `player`：`BackpackInventory`（6×8）+ `CharacterBackpack`。
  - `Cube (1)`：`BackpackInventory`（6×8）+ `ChestInventory`。
  - `UI`：`BackpackGridUI` + `BackpackInteractionController`。
  - `UI_ChestBackpack` 预制体实例：`ChestBackpackUI` + 两个 `BackpackContainerUI` + 交互组件，默认打开。
- 联机组件当前**未挂到场景**；需要按第 7 节自行接线，客户端与服务端使用同一目录。

### 10.2 当前未接入

| 项 | 状态 |
|---|---|
| 装备 | `Equipment` 程序集只有 asmdef；`EquipmentFragment` 有数据模型，无装备 / 卸下流程 |
| 消耗 | `ConsumableFragment` 有数据模型，无消耗处理者与效果系统 |
| 丢弃 / 拾取 | `Cmd.Item.Drop` 无处理者；网络组件不含拾取链路 |
| `Ui.*` 事件 | 已定义，当前点击走直接调用 |
| 具体物品事件 | `Inv.Item.Added` / `Stack.Changed` / `ItemRemoved` 已广播但无订阅者 |
| 自动化测试 | `Tests/` 仅空目录，无测试程序集 |
| 位置同步 | 按设计不同步 |

### 10.3 关键文件

| 层 | 入口文件 |
|---|---|
| 事件 | `Core/Events/EventBus.cs`、`EventChannels.cs` |
| 物品 | `Items/Runtime/RuntimeItem.cs`、`Manifest/ItemManifest.cs`、`Runtime/RuntimeItemSnapshotCodec.cs` |
| 库存 | `Inventory/InventoryContainer.cs`、`InventoryHostCore.cs`、`IBackpackView.cs` |
| 单机 | `Runtime/BackpackInventory.cs`、`CharacterBackpack.cs`、`BackpackRegistry.cs` |
| 网络 | `Network/Inventory/BackpackNetworkInventory.cs`、`InventoryNetworkProjection.cs` |
| UI | `UI/BackpackContainerUI.cs`、`BackpackInteractionController.cs`、`ChestBackpackUI.cs` |

---

**一句话总结**：权威容器决定“有什么、占多大、放不放得下”；客户端镜像决定“它现在是什么样”；UI 本地排布决定“它显示在哪”；事件总线用来源隔离把三者串起来。
