---
name: InventoryContainer 精简重构
overview: 在不改动 `InventoryContainer` 公共 API 与可观察行为的前提下，压缩其冗长 XML 注释、合并重复的「行列遍历 / 边界与归属校验」实现，删除冗余防御性检查，并同步更新 M2 契约文档中受影响的表述（行数、逐行照搬口径、验收项）。
todos:
  - id: slim-comments
    content: 压缩 InventoryContainer 类摘要与各公开方法注释，删复述型行内注释，保留契约关键词
    status: completed
  - id: dedup-region-scan
    content: 抽出零分配区域游标，去重 QuerySpace/TryValidateRegion/ClaimRegion/IsRegionEmpty/WriteCells/ClearCells 六处遍历骨架
    status: completed
    dependencies:
      - slim-comments
  - id: trim-redundant-guards
    content: 删 TryPlaceItem 与 TryAddItem 的冗余守卫，抽统一方法收敛「物品不在容器内」样板校验
    status: completed
    dependencies:
      - dedup-region-scan
  - id: parity-audit
    content: 用 [subagent:code-explorer] 核对全部外部引用与公共 API 未变，人工审计广播、原子性、零 GC 对齐 M2 契约
    status: completed
    dependencies:
      - trim-redundant-guards
  - id: sync-m2-doc
    content: 同步 M2 契约文档的移植口径、行数、性能与验收表，并更新 F1_F2 文档中 M2 行数表述
    status: completed
    dependencies:
      - parity-audit
---

## 需求概述

对 `Assets/backpack/Scripts/Inventory/InventoryContainer.cs`（708 行，背包库存数据层的唯一真相源）进行**原地精简**：删除重复与冗余、压缩注释、提升可读性，同时**不改变任何对外能力与运行结果**——所有公开成员签名、拒绝原因语义、变更成功后的通知行为全部保持原样，6 处外部调用方零改动。

## 已确认口径（用户四问选项）

1. **力度**：注释瘦身 + 内部实现去重（删冗余 XML 注释、合并重复的行列遍历骨架）；公共 API 与行为完全不变，调用方零改动。
2. **契约**：允许删掉经核实的重复防御校验（同一失败原因已被其它校验覆盖），其余校验与错误原因保留。
3. **指标**：不设行数硬指标，可读性优先，能删则删。
4. **文档**：需要同步更新 `backpack/Docs/核心契约/M2_库存真相源与空间查询.md`。

## 核心内容

1. **注释瘦身**：压缩类摘要（现为一条 5 个契约点的超长单行）与各公开方法摘要，删除复述代码本身的行内注释；保留中文注释风格与关键契约关键词（纯数据层、两份状态同源、堆叠数单一来源、失败零变更、上左格、零 GC）。
2. **实现去重**：合并 6 处重复的「上左格换算 + 双层行列遍历」骨架；复用 Core 既有坐标换算工具，消除手写取模与整除。
3. **删重复防御**：移除可被既有校验完全覆盖的重复守卫与恒真判断，并用统一方法收敛「物品不在容器内」的样板校验。
4. **契约保持**：对外成员签名、失败即零变更、版本号自增、变更成功路径的广播次数与顺序、热路径零分配特征逐条不变。
5. **文档同步**：更新 M2 契约文档中已失效的「逐行照搬蓝本」口径与行数、性能与验收表表述，并同步工程架构文档中的行数描述。

## 视觉表现

无界面变化，纯数据层代码整理。

## 技术栈与既有约束

- 工程现状：Unity 2022.3.62f3 LTS（C# 9），纯 C# 数据层程序集 `RPG4.Backpack.Inventory`（`references = Core / Items`），不引用 UI / MonoBehaviour / 网络。
- 目标文件：`Assets/backpack/Scripts/Inventory/InventoryContainer.cs`（708 行，`sealed class`，无继承、无序列化字段）。
- 依赖契约（必须保持字节级语义）：`Core/Types` 的 `GridCoord` / `SlotAvailability` / `SlotAvailabilityResult` / `SpaceQueryResult`；`Core/Utils/GridUtils`（`GetPositionFromIndex` / `GetIndexFromPosition` / `IsInGridBounds`）；`Core/Events` 的 `EventBus` / `InvChannels`；`BackpackLog` / `LogModule`；`Items/Runtime` 的 `RuntimeItem` / `ItemId` / `GridFragment`。
- 验收口径：项目零单测（2026-10-05 选型），验收为「Unity 刷新无 CS 报错 + 人工核对」。

## 实施方案

### 1. 冻结面（先立不变量，再动手）

- **公共 API 冻结**：构造函数、`Rows` / `Columns` / `SlotCount` / `ItemCount` / `Items` / `Revision`、`FillItemsByItemId` / `IsIndexValid` / `IsIndexOccupied` / `GetUpperLeftIndexAt` / `GetItemAt` / `Contains` / `TryGetPlacement` / `TryGetItemById` / `GetItemDimensions`（静态）/ `QuerySpace`（两个重载）/ `QueryAvailability` / `FillAvailability` / `TryPlaceItem` / `TryRemoveItem` / `TryStackItem` / `TrySetStackCount` / `TryAddItem`，签名与语义一律不动。已核实的实际外部调用方为 `Runtime/BackpackInventory.cs`、`Network/Inventory/BackpackNetworkInventory.cs`、`Network/Inventory/InventoryNetworkProjection.cs`、`UI/BackpackInteractionController.cs`（仅用静态 `GetItemDimensions`），它们不依赖任何私有实现细节。
- **可观察行为冻结**：失败即零变更；`BumpRevision` 为版本号唯一自增入口；4 个广播注入点与 3 个通知辅助方法（`NotifyItemAdded` / `NotifyItemRemoved` / `NotifyStackChanged`）的触发条件、次数、顺序、`source = this` 全部保持（`BackpackNetworkInventory` 用 `Revision` 短路比较，次数变化会引发多余投影）。
- **零 GC 冻结**：热路径禁用 LINQ、闭包与逐格临时集合；`FillAvailability` 继续复用调用方结果实例；`claimed[] + claimStamp` 轮次戳、被占非上左格剪枝、`Remainder` 归零提前退出等优化一律保留。

### 2. 注释瘦身策略

- 类摘要由「一条超长单行」改为 2–3 行短句，覆盖：纯数据层（无 UI / MonoBehaviour / 网络）、两份状态同源且只在提交阶段写入、堆叠数不缓存、单线程无锁零 GC 查询、变更成功经 F6 广播；详细不变量说明留在 M2 文档（DRY，避免代码与文档两份长篇）。
- 公开方法摘要压缩为 1–2 行，只保留「契约级信息」：原子性 / 失败零变更、上左格口径、单堆上限、部分并入与 `movedCount` 语义、广播触发条件；删除复述形参名与实现步骤的句子。
- 保留 `Reject`、`CommitPlacement`、`ClearCells`、`BumpRevision` 等关键私有方法的短摘要（它们是契约锚点，文档 §3 / §5 有引用）。
- 行内尾部注释只保留「非显然」的（如 `claimStamp` 初值语义、`Invalid` 与 `UpperLeftIndex == 0` 的歧义说明），删除与成员名同义的行内注释。

### 3. 区域遍历去重（最大收益项）

现状有 6 处相同骨架：`originX = startIndex % Columns` / `originY = startIndex / Columns` 加双层行优先 `for`，分别位于 `QuerySpace`、`TryValidateRegion`、`ClaimRegion`、`IsRegionEmpty`、`WriteCells`、`ClearCells`。

建议形态（可等价替换，签名级示意）：

```
private ref struct RegionCursor
{
    public RegionCursor(InventoryPlacement[] cells, int columns, int originX, int originY, GridCoord dimensions);
    public bool TryNext(out int index, out InventoryPlacement cell);
}
```

- 作为**局部变量**使用的 `private ref struct`（C# 7.2 特性，Unity 2022.3 的 C# 9 可用），零堆分配、无委托、无闭包，满足零 GC 冻结；
- **禁止**改用 `GridUtils.ForEach2D` 的 `Action` 重载：lambda 会捕获局部变量并每次分配闭包，直接破坏零 GC 契约（该工具适用于低频路径，不适用于逐格容量扫描）；
- 起点换算统一改用 `GridUtils.GetPositionFromIndex(startIndex, Columns)`（复用 Core 既有工具，消除手写取模/整除）；
- 若实施时评估游标反而降低可读性，退化为「保留局部双层循环 + 统一用 `GridUtils.GetPositionFromIndex`」，此时仅取得可读性收益、不减行数。

**明确的非目标**：不合并 `QuerySpace` 与 `TryValidateRegion`。两者契约不同（前者允许多物品挡路时返回空结果、并回传「可交换候选 + 该物品上左格」用于高亮锚点；后者要求被占格归属必须等于区域起点，否则整块不可用），强行合并会把两条语义耦合成带布尔开关的复合函数，可读性与可测试性双降，且无性能收益（同为 O(W×H)）。

### 4. 删冗余防御与样板收敛（逐条有据）

| 目标 | 判断依据 | 影响 |
| --- | --- | --- |
| `TryPlaceItem` 中的 `IsIndexValid(startIndex)` 守卫 | `GridUtils.IsInGridBounds` 内部已含 `startIndex` 越界判定（同一索引区间），该守卫严格冗余 | 仅错误文案从「落点索引越界」变为「落点 + 尺寸越界」；两者都会被上层记入日志，语义等价 |
| `CanInsert` 的操作名前缀 `"插入"` | 与其它方法统一使用 `nameof(调用方)`，日志中可精确定位入口（只有 `TryPlaceItem` / `TryAddItem` 两处调用） | 拒绝文案由「插入 被拒绝」变为「TryPlaceItem 被拒绝」，更可诊断 |
| `TryAddItem` 提交段的 `IsIndexValid(slot.Index)` | 明细条目由 `FillAvailability` 产出，其 `Index` 已过 `IsInGridBounds` 校验，恒真 | 无行为变化 |
| 「物品不在容器中」样板（`TryStackItem` / `TrySetStackCount` / `TryRemoveItem`） | 三处样板一致，抽出统一校验方法 | `TryRemoveItem` 需要取 `InventoryPlacement` 故保留 `TryGetValue` 分支；文案与拒绝原因保持区分 |
| `TryAddItem` 提交段的 `IsIndexOccupied(slot.Index)` | 明细条目之间由 `claimStamp` 保证互不重叠，理论上恒假，但属**跨批次状态**防御 | 建议**保留**：成本 O(1)，可防止将来 `FillAvailability` 语义变化时出现「重复占用同一空白区」的隐性错误 |
| 构造函数三段校验 | 分别提供 `paramName`，便于定位非法参数 | 建议**保留**（8 行换可诊断性） |


- 删除原则：只删「同一失败结果已被其它校验覆盖」的检查；每条保留的校验都对应 M2 契约中的一条拒绝原因，不得出现某条失败原因静默消失。

### 5. 兼容性与风险控制

- 仅改一个文件加两份文档；不动 `.cs.meta`（工程非 git 仓库，GUID 手工维护，改动会导致引用丢失）、不动 asmdef、不新增文件、不引入新依赖。
- 公共 API 冻结 + 私有实现重写，编译期即可发现全部越界改动（外部调用方零改动是硬验收）。
- 已知既有瑕疵（本轮**不修**，仅登记）：`GetItemDimensions` 在 `GridFragment` 宽或高为 0 时会返回零尺寸，`IsInGridBounds` 对零尺寸判定为「在内」，可能提交零格记录——属 M2 既有行为，超出「精简且不改行为」范围。

## 性能与复杂度

- 复杂度不变：单格读取 O(1)、`QuerySpace` O(W×H)、`FillAvailability` / `TryAddItem` 最坏 O(R×C×W×H)、`FillItemsByItemId` O(n log n)。
- 新增 `ref struct` 游标为栈上结构、方法体极小，逐格调用开销可忽略（相比每格 2–3 次数组访问）；不引入任何堆分配、不新增字段、不改变缓冲复用策略。
- `FillItemsByItemId` 可改为 `List.AddRange` 直接吃字典键集合（走 `ICollection` 拷贝快路径，无枚举器分配），如对该路径的零分配特性无十足把握则保持原样显式循环——两者均为 O(n log n) 主导，收益仅 3 行。

## 目录结构与改动清单

```
e:/ue/RPG_4/Assets/backpack/
├─ Scripts/Inventory/
│  └─ InventoryContainer.cs                 # [MODIFY] 主目标：注释瘦身 + 六处区域遍历去重 + 删冗余防御 + 统一归属校验样板；
│                                           #          新增单文件私有 ref struct 区域游标；公共 API、广播、Revision、零 GC 全部冻结
└─ Docs/
   ├─ 核心契约/M2_库存真相源与空间查询.md      # [MODIFY] 移植口径（不再「逐行照搬」）、行数、写入口表述、零 GC 口径与验收表第 2 项同步
   └─ 工程架构/F1_F2_程序集与目录命名规范.md   # [MODIFY] §5 中 M2 条目的行数表述（609 行照搬 + 共 643 行）更新为精简后的实际行数与口径
```

- 不改动：`Inventory/InventoryPlacement.cs`、`Backpack.cs` / `BackpackData.cs`（原型、不接入）、`Runtime/*`、`Network/*`、`UI/*`、所有 `.meta` 与 asmdef。
- 相关但**不需改**：`Docs/核心契约/F6_事件总线.md`（广播键表与 source 语义不变）、`Docs/核心契约/S1_单机背包宿主与索引.md`（只引用容器契约，不引行数）。

## 验收方式

1. Unity 刷新 Console 无 CS 报错（公共 API 冻结下编译即验证调用方零改动）。
2. 人工核对四步（项目既有 C# 审计路径，Open Code Review 不覆盖 C#）：公开成员签名逐个对照 M2 §2 表；广播键与触发路径对照 §5 表；原子性（每个 `Try*` 的失败路径无写入）逐方法走查；零 GC（无 LINQ / 无闭包 / 无新分配）与复杂度对照 §6。
3. 精简后行数记录进 M2 文档与 F1_F2 §5。

## Agent Extensions

### SubAgent

- **code-explorer**
- Purpose: 在精简前后独立核对「谁依赖了这个文件」，覆盖 CodeBuddy 文本搜索之外的隐式依赖（例如是否有代码消费 `out error` 文案、是否有测试或编辑器脚本引用被删除的私有实现、是否还有其他文档存在已失效的行数/口径描述）。
- Expected outcome: 输出一份可核验的引用清单（文件 + 行号 + 依赖的成员/文案），确认除 `BackpackInventory`、`BackpackNetworkInventory`、`InventoryNetworkProjection`、`BackpackInteractionController` 外无其他依赖；并列出所有需要同步的文档位置，作为「调用方零改动」与文档同步完整性的证据。