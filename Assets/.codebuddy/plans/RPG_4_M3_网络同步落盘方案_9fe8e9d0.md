---
name: RPG_4_M3_网络同步落盘方案
overview: 精简为纯代码实现：两个批次共 6 个文件（Items 快照层 3 + Network 同步层 3），核心文件按已核实行号裁剪 M4/M6/M8；不做冒烟面板、验收、Review 与文档。
todos:
  - id: items-snapshot
    content: 照搬落盘 Items 快照层三件：RuntimeItemSnapshot / RuntimeItemSnapshotCodec / 回迁 ItemManifest.TryRestoreRuntimeItem（含 .meta）；遵循 [skill:command-execution-no-tests-no-comments]
    status: completed
  - id: network-sync-layer
    content: 落盘 Network 同步层三件：NetworkInventoryEntry / InventoryNetworkProjection / BackpackNetworkInventory 按行号裁剪 M4/M6/M8（含目录与 .meta）
    status: completed
    dependencies:
      - items-snapshot
---

## 需求概述

为 RPG_4 多人背包系统落盘 M3 网络同步代码：服务端权威库存以增量方式同步给客户端，两端物品列表、堆叠数与随机数值一致；客户端仅提交操作意图，不篡改库存；网格位置不参与同步。

## 本轮范围

- 只做代码实现（6 个文件 + 目录与文件 .meta）
- 不做：冒烟面板、测试、Review、文档、场景/预制体/资产

## 技术栈

Unity 2022.3.62f3 + Mirror 96.0.1（工程内置，不新增包）；既有程序集 `RPG4.Backpack.Items` / `RPG4.Backpack.Network`（asmdef 均不改）。

## 实现方式

逐文件照搬蓝本 e:/github/ZDXT2 + 前缀替换 `ZDXT.Backpack.*` → `RPG4.Backpack.*`；核心文件按已核实行号裁剪为 M3-only，文件头登记剥离清单与蓝本行号。依赖方向 `Items → Inventory → Network`，数据层不引 Mirror。

## 落盘清单（按序执行）

| # | 目标文件（Assets/backpack/…） | 蓝本来源 | 要点 |
| --- | --- | --- | --- |
| 1 | `Scripts/Items/Runtime/RuntimeItemSnapshot.cs` [NEW] | 165 行全等 | FragmentNumberState + RuntimeItemSnapshot（版本 1 / 节点上限 64 / 类型串上限 128）；仅依赖 Core |
| 2 | `Scripts/Items/Runtime/RuntimeItemSnapshotCodec.cs` [NEW] | 389 行全等 | TryCapture L18 / TryApplyNumbers L52 / TryEncodeNumbers L101 / TryDecodeNumbers L153；包头 3B、节点 8B、载荷上限 515B |
| 3 | `Scripts/Items/Manifest/ItemManifest.cs` [MODIFY] | 回迁蓝本 L124 | `TryRestoreRuntimeItem(RuntimeItemSnapshot, out RuntimeItem, out string)`；其余创建重载已齐不动 |
| 4 | `Scripts/Network/Inventory/NetworkInventoryEntry.cs` [NEW] | 154 行全等 | SyncList 值类型元素（itemId / itemType / stackCount / fragmentState）+ TryValidate / StateEquals |
| 5 | `Scripts/Network/Inventory/InventoryNetworkProjection.cs` [NEW] | 272 行全等 | 纯投影 + ProjectionSyncReport；不依赖 Mirror |
| 6 | `Scripts/Network/Inventory/BackpackNetworkInventory.cs` [NEW] | 877 行裁剪版 | M3 保留面如下；文件头登记剥离清单 + 蓝本行号 |


## 裁剪登记（核心 877 行 → M3 边界）

- M4 拾取 12 处全剥：DefaultPickupDistance L20 / pickupDistance L35-38 / PickupDistance L61 / PickupRejectionCount L63 / LastPickupResult L65 / OnStartServer 重置 L124-125 / RequestPickup L194-204 / CmdRequestPickup L206-216 / CanSendPickupRequest L285-301 / TryServerPickup L355-404 / ClearPickupPrompt L406-410 / RegisterPickupRejection L848-853（`Inv.PickupPrompt` 缓落）
- M6 拆分全剥：RequestSplitStack L187 / CmdRequestSplitStack L243-244 / TryServerSplitStack L488-566 / TryBuildSplitInstance L569 / FindFirstFreeRegion L587 / SplitRejectionCount L67 + LastSplitResult L69
- M8 钩子全剥：Subscribe/UnsubscribeCommandChannels、ResetConsumeDiagnostics、InitializeDropPipeline 及调用点（L100/128/129/152/163）；不编入 .Equipment.cs（171 行）/ .Consumables.cs（438 行）
- 保留面：SyncList entries L44、两个 Command（CmdRequestSetStackCount L219-220 / CmdRequestRemoveItem L231-232）、RequestSetStackCount L173 / RequestRemoveItem L180、TryServerAddItem L306 / TryServerSetStackCount L413 / TryServerRemoveItem L459 / TryServerClearInventory L598 / SyncNow L636 / RefreshClientMirror L654、TryGetItem L664 / CopyItems L673 / DescribeState L693、私有 SyncEntries L703 / RebuildMirror L738 / OnEntryChanged L760 / ApplyToMirror L788 / TryFindServerItem L802 / ResolveGenerator L823 / RegisterFailure L842 / RaiseChanged L863 / LateUpdate L869、生命周期回调 L105/138/148/161、UseCatalog L85、Changed L55、LastError L57、DesyncCount L59、IsServerInventory L73、ItemCount L75、ServerContainer L77、Catalog L79

## 关键实现要点

- 客户端恢复只回填数值（TryRestoreRuntimeItem → TryApplyNumbers → TryApplyRuntimeNumber），不调 IItemRandomSource
- 投影按「本次操作的物品是否进入投影」判成败，其它拒绝只累计 DesyncCount / LastError
- 同步按 ItemId 升序差异下发（Add / Set / RemoveAt），无变化零下发；Changed 脏标记 + LateUpdate 合并触发

## 硬约束

- 全工程不得引用 `Unity.InputSystem`（未安装）；Network asmdef 不加引用
- 命名空间/前缀整体替换；`.meta` 手工生成，GUID 沿用蓝本同名文件（先全量搜索确认无冲突），新目录 `Scripts/Network/Inventory/` 需目录 meta
- 不写测试、不新增额外注释（注释随蓝本照搬）

## Agent Extensions

### Skill

- **command-execution-no-tests-no-comments**
- Purpose: 保持「零单测」选型——落盘过程不运行测试、不新增蓝本之外的注释
- Expected outcome: 改动面仅限照搬与裁剪，无多余测试与注释引入