# F7 · Mirror 网络基座

> 对应路线图《Unity多人背包系统执行顺序方案》（蓝本仓库 `e:\github\ZDXT2\Assets\backpack\Docs\里程碑\路线图\`）第一节 **F7：Mirror 网络基座**（前置 F1）。
> 落盘：`Assets/backpack/Scripts/Network/`，命名空间 `RPG4.Backpack.Network`；网络库 `Assets/Mirror`（96.0.1，已在工程内，无需安装）。
> 移植口径：**逐行照搬**蓝本 `e:\github\ZDXT2\Assets\backpack\Scripts\Network\` 的两个脚本，除命名空间前缀 `ZDXT` → `RPG4` 外逐字节一致（`BackpackNetworkManager.cs` 192 行 / `BackpackNetworkPlayer.cs` 90 行，前缀替换后全等校验通过）。
> **本轮范围**：只落代码（asmdef + 2 脚本）。**场景与预制体由使用者在 Unity 手搭**（清单见 §4），**不写单测**、**不装 Unity.InputSystem**、**不登记 Build Settings**。
> 本文只记录 F7 的落盘内容、边界、场景配置与验收步骤；**M3（物品列表同步）不在本范围**。

---

## 1. 完成标志对照

| 方案中的完成标志 | 本工程落盘实现 |
|---|---|
| 空场景可连主机 / 客户端 | `Scripts/Network/BackpackNetworkManager.cs` + `BackpackNetworkPlayer.cs`，配合 §4 手搭的 `PF_NetworkPlayer.prefab` 与 `F7_NetworkBootstrap.unity` |
| 物品 Id 生成器可用 | `Scripts/Items/Runtime/ItemId.cs` + `ItemIdGenerator.cs`（M1 已落盘，与蓝本逐字节一致，本轮无需改动） |

---

## 2. 落盘清单

| 文件 | 程序集 / 资产 | 职责 |
|---|---|---|
| `Scripts/Network/RPG4.Backpack.Network.asmdef` | — | 引用 `Core` / `Items` / `Inventory` / `Mirror`；**唯一持有 Mirror 依赖的业务程序集** |
| `Scripts/Network/BackpackNetworkManager.cs` | `RPG4.Backpack.Network` | 生命周期诊断日志 + 服务端 `ItemIdGenerator` 托管 + `startMode` 自动启动 |
| `Scripts/Network/BackpackNetworkPlayer.cs` | `RPG4.Backpack.Network` | 最小联网玩家：`SyncVar ownerConnectionId`，本地/远端配色，无移动/输入/相机 |
| `Prefabs/PF_NetworkPlayer.prefab` | 资产（**使用者手搭**） | 胶囊 + `NetworkIdentity` + `BackpackNetworkPlayer` |
| `Scenes/F7_NetworkBootstrap.unity` | 资产（**使用者手搭**） | 联机冒烟场景（相机 + 平行光 + NetworkManager 节点） |

引用关系：`Items（纯标识） → Network（Mirror 适配） → 场景 / 预制体`；反向依赖不存在。
`.meta` 沿用蓝本 GUID（工程内已核对唯一）：`BackpackNetworkManager.cs` = `d84bc1ad66b54df98641eb145ce863bb`、`BackpackNetworkPlayer.cs` = `2671b5a6c2bc491b9309b6f9956d6320`、`RPG4.Backpack.Network.asmdef` = `705f0d7191a442ff8a1632a8361821c0`。

---

## 3. 契约要点

### 3.1 `ItemId`（`RPG4.Backpack.Items`）

- 底层 `ulong`，`0` 固定表示 `None`（`default(ItemId)` 即 `None`），**有效标识从 1 开始**。
- 实现 `IEquatable<ItemId>` / `IComparable<ItemId>`、六个比较运算符、`explicit` 转换到 `ulong`。
- 不感知 Mirror：跨端传输由网络适配层显式读写 `Value`（M3 若 Weaver 不能直接处理，只在 Network 层补 Reader/Writer）。

### 3.2 `ItemIdGenerator`（`RPG4.Backpack.Items`）

- `Next()` 返回递增标识；`LastIssued` 用于会话诊断与续接；`FirstValue = 1`。
- 只在**服务端主线程**使用，一个服务端会话一个实例（`BackpackNetworkManager.OnStartServer` 创建、`OnStopServer` 释放）。
- 达到 `ulong.MaxValue` 抛 `OverflowException`，**绝不回绕**（回绕会产生重复标识）。

### 3.3 `BackpackNetworkManager`（`RPG4.Backpack.Network`）

- 继承 `NetworkManager`，仅做「生命周期日志 + 标识生成器托管」，不含任何背包业务规则，也不持有库存状态。
- `startMode`（Inspector）：`None`（默认，由 HUD 手动启动）/ `Host` / `Client` / `Server`，`Start()` 时按值自动启动。
- 诊断日志覆盖：主机/服务端/客户端启动停止、连接与断开、玩家生成、传输错误与异常（复用 `BackpackLog` + `LogModule.Network`）。

### 3.4 `BackpackNetworkPlayer`（`RPG4.Backpack.Network`）

- `[SyncVar] int ownerConnectionId` 在服务端赋值（`connectionToClient.connectionId`）。
- 本地玩家（`isOwned`）绿色、远端玩家红色，通过 `MaterialPropertyBlock` 同时写入 `_BaseColor`（URP Lit）与 `_Color`（内置管线）。
- 刻意**不含**移动、相机、输入、背包同步：这些属后续阶段，避免与离线玩家（`SampleScene` + `Assets/Prefabs/player.prefab`）的权威与输入逻辑冲突。

---

## 4. 场景与预制体（使用者手搭清单）

本轮**不落盘** `.prefab` / `.unity` 文本资产——蓝本现状的这两个资产已被 M3–M8 扩展（含 `BackpackNetworkInventory` / `BackpackNetworkEquipment` / `BackpackPickupController` / `PF_WorldItem` / `PF_InventoryUI` / `InventoryUiSpawner` / `PF_EquipActor` 等引用），照搬会产生大量无法解析的引用。

### 4.1 `Assets/backpack/Prefabs/PF_NetworkPlayer.prefab`

1. 新建 Capsule（`3D Object > Capsule`），改名 `PF_NetworkPlayer`。
2. 添加组件 `NetworkIdentity`（Mirror）。
3. 添加组件 `BackpackNetworkPlayer`，把 `Body Renderer` 指向自身的 `MeshRenderer`。
4. 拖入 `Assets/backpack/Prefabs/` 保存为预制体（从场景删除实例）。

### 4.2 `Assets/backpack/Scenes/F7_NetworkBootstrap.unity`

| 节点 | 组件 | 关键配置 |
|---|---|---|
| `NetworkManager` | `BackpackNetworkManager` | `startMode = None`、`transport` → `KcpTransport`、`playerPrefab` → `PF_NetworkPlayer`、`networkAddress = localhost`、`autoCreatePlayer` ✓、`autoStartServerBuild` ✗、`editorAutoStart` ✗ |
| `NetworkManager` | `KcpTransport` | `port = 7777`（可改端口做多实例测试） |
| `NetworkManager` | `NetworkManagerHUD` | 开发期 Host / Client / Server 按钮，**不作为生产登录或正式 UI** |
| `Main Camera` | Camera + URP `UniversalAdditionalCameraData` | 位置 `(0, 5, -14)`，俯角约 19.65°，用于观察出生点 |
| `Directional Light` | Light + URP `UniversalAdditionalLightData` | 默认平行光 |

- 场景**未**引入任何背包 UI、库存对象或离线玩家，故不影响 `SampleScene` 的既有玩法。
- 玩家出生位置由 Mirror 的 `playerSpawnMethod`（默认 `RandomCircle`，原点附近）决定；后续如需固定点位，在场景内放置 `NetworkStartPosition`。
- 手搭产生的 `.prefab` / `.unity` GUID 由 Unity 生成，属本工程自有资产，不影响脚本与 asmdef 的 GUID 台账。

---

## 5. 验收步骤

1. Unity 打开工程，确认 Console **无 CS 报错**、无 “Assembly Definition reference not found”。
2. 按 §4 搭好预制体与场景，Play → 点击 HUD 的 **Host**：
   - `Console` 出现「服务端已启动：KcpTransport，端口 7777」「主机已启动」「本地玩家已就绪」；
   - 场景中出现绿色胶囊（本地玩家）。
3. 第二个实例（编辑器 Play 或独立构建）点击 HUD 的 **Client**：
   - 主机端出现「客户端已连接」「已为 connectionId=… 生成玩家对象」；
   - 客户端出现「已连接到服务端」「玩家已出现…isOwned=False」并显示红色胶囊。
4. 客户端退出：主机端出现「客户端已断开」，且**不残留**玩家对象与连接计数；Host 停止后再启动仍可正常连接（状态清理校验）。
5. 回归：打开并 Play `Assets/Scenes/SampleScene.unity`，离线玩法与 F7 之前一致。

> **物品 Id 生成器确认**：`Host` 启动日志中「物品标识从 1 开始发放」即证明服务端 `ItemIdGenerator` 已按会话创建；停止日志「本次会话最后发放的物品标识 …」证明释放路径生效。

---

## 6. 与蓝本的有意偏差

| # | 项 | 蓝本 | 本工程 | 理由 |
|---|---|---|---|---|
| 1 | 命名空间 / 菜单前缀 | `ZDXT.Backpack.Network`、`AddComponentMenu("ZDXT/...")` | `RPG4.Backpack.Network`、`AddComponentMenu("RPG4/...")` | F1_F2 §3.1 命名空间一层到底 + 前缀统一 |
| 2 | `Unity.InputSystem` 引用 | Network asmdef 含 `Unity.InputSystem` | **不含** | RPG_4 未安装该包，写了会触发 “Assembly Definition reference not found”；F7 代码零输入引用，待 M4/M6 真用到时再装再加 |
| 3 | 场景 / 预制体 | `Prefabs/PF_NetworkPlayer.prefab`、`Scenes/F7_NetworkBootstrap.unity` 已落盘 | **由使用者手搭**（§4） | 蓝本现状资产已含 M3–M8 的组件引用，照搬会产生无法解析的引用 |
| 4 | 单测 | `Tests/ItemIdTests.cs`（已不在蓝本工作树） | **不落盘** | RPG_4 零单测口径；回归网待 M 阶段统一补 |
| 5 | 场景登记 | 已登记 Build Settings | **不登记** | 验收只在编辑器内做两个 Play 实例 |
| 6 | XML 注释引用 | `<see cref="BackpackNetworkInventory"/>` 指向同程序集类型 | 保留原样，**该类型属 M3、本轮不存在** | 照搬纪律；Unity 默认不生成 XML 文档，不产生编译错误，仅 IDE 灰显 |
| 7 | `Equipment` / `UI` 的 `Network`/`Mirror` 引用 | 两层均已加 | **本轮不加** | 当前无代码消费，等 M3/M5/M7 真用到时再加，避免空引用边 |

---

## 7. 未做事项与风险

- **不做** `SyncList` / 物品列表同步：M3 范围；`RuntimeItem`、库存容器的引用身份语义在 F7 未改动。
- **不做** PlayMode 自动联网测试：依赖真实端口与时序，易抖动；连接链路用 §5 的双端冒烟验收。
- **KCP 端口**：默认 `7777`；若被占用或跨机测试，需要同时调整端口、防火墙放行与 `networkAddress`。
- **未修改**：`Assets/Mirror/**`、`Packages/manifest.json`、`Packages/packages-lock.json`、`ProjectSettings/EditorBuildSettings.asset`、既有 5 个 asmdef、`Assets/Scenes/SampleScene.unity`。

---

## 8. 相关文档

- 命名与程序集：《F1_F2_程序集与目录命名规范》§1.2（Network 程序集登记）、§2（目录树 `Scripts/Network/`）。
- 日志口径：《F4_工具与日志分级》§5（`LogModule.Network`）。
- 标识契约：`Items/Runtime/ItemId.cs`、`ItemIdGenerator.cs`（M1 落盘，本文件 §3.1 / §3.2 为摘要）。
- 下一步：M3 网络同步（`SyncList<struct>` + 唯一 `ItemId`），依赖 M2 与 F7。
