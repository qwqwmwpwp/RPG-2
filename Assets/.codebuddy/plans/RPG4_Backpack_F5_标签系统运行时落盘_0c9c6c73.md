---
name: RPG4_Backpack_F5_标签系统运行时落盘
overview: 在 RPG_4 工程落盘 F5 标签系统的**运行时部分**：Core/Tags 三文件（BackpackTag 层级标签 + 精确/层级匹配 API、BackpackTagConstants 常量、TagRegistry SO 注册表）与 Data/TagRegistry.asset（9 条种子标签），并同步 F5 文档副本与 F1_F2 目录规范；Editor 三件套（Core.Editor asmdef / 抽屉 / 校验器）与 Editor 域注入本轮明确暂缓。
todos:
  - id: tags-runtime
    content: Use [skill:command-execution-no-tests-no-comments] 照搬蓝本落盘 Core/Tags 三文件及 .meta，仅改命名空间与 CreateAssetMenu 路径
    status: completed
  - id: registry-asset
    content: 新建 Data/TagRegistry.asset 与 .meta：9 条种子，m_Script 指向新 TagRegistry.cs 的 GUID
    status: completed
    dependencies:
      - tags-runtime
  - id: docs-sync
    content: 落盘 F5 契约文档副本（按 F4 副本风格改写，Editor 暂缓标注），并更新 F1_F2 文档的目录状态与下一步
    status: completed
    dependencies:
      - tags-runtime
  - id: verify-memory
    content: Use [subagent:code-explorer] 复核与蓝本逐行差异及同名冲突，核对验收表并同步项目记忆
    status: completed
    dependencies:
      - tags-runtime
      - registry-asset
      - docs-sync
---

## 需求概述

在 RPG_4 工程（`e:/ue/RPG_4`）落盘多人背包系统的 **F5 阶段——标签系统（仅运行时部分）**：移植蓝本的层级标签值类型、精确/层级双匹配 API、常量清单与注册表 ScriptableObject，并新建注册表种子资产；Inspector 抽屉、标签校验菜单、Editor 程序集及「注册表链路注入修复」本轮明确暂缓。

## 核心功能

- **层级标签值类型**：内部仅持一个字符串，以 `.` 分段（如 `GameItems.Equipment.Weapons.Sword`）；值语义、可序列化、不依赖全局注册表，可直进 SO 字段与后续 Mirror SyncList。
- **双匹配 API（语义不可混用）**：
- 精确匹配：整串序数相等，用于堆叠同类型判定、Fragment 查找、装备槽类型匹配、UI 同化。
- 层级匹配：子标签对父标签为真，仅用于装备可装判定（具体武器 vs 泛武器槽）。
- 空标签：两种匹配一律 false（安全失败，空槽位不接纳任何物品）；而 C# 相等契约 `Empty == Empty` 为 true。
- **格式契约**：构造宽松不抛异常；`IsWellFormed` / `TryParse` 提供校验（非空、不以分隔符首尾、无连续分隔符、分段仅字母数字下划线），失败给出原因并返回空标签。
- **常量清单**：`FragmentTags` 5 条（代码有调用点）+ `ItemTypeTags` 4 条示例命名口径（仅数据资产使用）。
- **注册表 ScriptableObject**：存在性查询 O(1)、层级子节点枚举（父节点无需登记）、自身体检（空条目/重复/格式非法）；解析链为显式注入 → Resources 兜底 → 空表兜底，**不参与任何匹配判定**。
- **注册表资产**：`Data/TagRegistry.asset` 登记 9 条叶子标签（Fragment 5 + 物品 4），可在 Inspector 查看与编辑，供后续候选/校验使用。

## 边界（本轮不做）

- Editor 三件套（程序集、Inspector 抽屉、校验器菜单）与 Editor 域注入修复全部暂缓，记入文档待后续轮次。
- 不新增单测；不改动任何 asmdef 的 references；不安装 Mirror / Input System；不动既有资产与场景。

## 技术选型

- 引擎与管线：Unity 2022.3.62f3 LTS + URP 14.0.12（沿用现有工程与目录，不改配置）。
- 语言与程序集：C#，三个新文件全部归属既有 `RPG4.Backpack.Core` 程序集（`references` 保持空数组）。
- 数据资产：ScriptableObject（`TagRegistry`）+ 手写 YAML 资产 `Data/TagRegistry.asset`。
- 元数据：手工生成 `.meta`（32 位十六进制全局唯一 GUID；本工程非 git 仓库，无 .meta 则资产不可见）。
- 依赖策略：零新增依赖，不触碰 Packages。

## 实现方案

### 总体策略

逐行照搬蓝本（`e:/github/ZDXT2/Assets/backpack`）三个运行时文件与资产 YAML，仅做**两处有意分叉**：

1. 命名空间前缀：`ZDXT.Backpack.Core` → `RPG4.Backpack.Core`（每文件 1 处）。
2. `TagRegistry` 的 `CreateAssetMenu` 菜单路径：`ZDXT/Backpack/Tag Registry` → `RPG4/Backpack/Tag Registry`（与既有 `LogSettings.cs` 的 `RPG4/Backpack/Log Settings` 口径一致）。

其余内容（XML 注释、中文说明、常量值、算法实现、匹配语义）保持与蓝本逐字节一致，确保 M 阶段仍可与蓝本逐行对照排查。

### 关键决策与取舍

| 决策 | 内容 | 理由 |
| --- | --- | --- |
| Editor 暂缓 | 不落 `Core/Editor/` 任何文件；注册表链路注入修复一并延后 | 用户明确「Editor 相关内容先不进行实现」；缺口在两份文档中显式记录，不让其静默 |
| 种子 9 条 | 资产与常量完全对齐；蓝本多出的 2 条（Consume.Heal / Equip.Damage）留到 M1/M8 | 用户决策；避免常量与资产条数漂移复刻到本工程 |
| 菜单路径沿用 | Editor 轮采用蓝本 `Tools/Backpack/*`（本轮只记入文档） | 用户决策：与蓝本零差异便于对照 |
| .meta 新造 GUID | 不复用蓝本 GUID；资产 `m_Script` 指向本轮新 `TagRegistry.cs` 的 GUID | 沿用 F1–F4 已核实惯例（本工程 GUID 均为独立手工生成） |
| 不动 asmdef | `RPG4.Backpack.Core` 的 `references` 必须仍为空 | F1 完成标志；一旦引用上层即成环破坏分层 |


### 性能与可靠性要点

- 层级匹配为零分配实现：长度短路 → 分隔符位校验 → 定长 `CompareOrdinal` 前缀比较，无 Substring。
- 注册表用序数 `HashSet` 做存在性查询 O(1)；标签列表懒建缓存、`OnValidate` 触发失效并递增版本号；`GetChildren` 复杂度为 O(标签数)。
- `GetSegments` 不做缓存，文档注明热点调用点需自持结果。
- 边界契约：匹配 API 遇空标签恒 false；相等契约自反（集合去重可用）；域重载时静态门面复位，避免上一局残留。

### 验证口径（沿用零单测选型：人工核对 + Unity Console）

1. 三个 `.cs` 与蓝本逐行比对，结论应为「仅命名空间/菜单路径差异」。
2. Unity 打开工程：Console 零 CS 报错、零程序集引用缺失。
3. `Data/TagRegistry.asset` 在 Inspector 可见且恰为 9 条，与常量清单一致。
4. 按 F5 契约文档验收表逐条走查，Editor 两行标注为暂缓。

## 架构与目录

标签层为纯数据 + 纯函数，无 UI/网络依赖，位于 Core 程序集（叶子层），向上为 M1（Fragment 查找）、M2（按类型查找）、M6（堆叠与同化）、M7（装备槽）与 F6（事件载荷）提供判定能力。

```
e:/ue/RPG_4/Assets/backpack/
├─ Scripts/Core/
│  ├─ RPG4.Backpack.Core.asmdef                 # 不改：references 保持空
│  └─ Tags/
│     ├─ BackpackTag.cs                         # [NEW] 层级标签 struct：Separator / Empty / Name / IsEmpty / IsValid /
│     │                                         #   GetSegments / MatchesTagExact / MatchesTag / IsWellFormed / TryParse /
│     │                                         #   Equals / == / != / GetHashCode / ToString；照搬蓝本，仅改命名空间。
│     │                                         #   硬约束：私有序列化字段名必须为 name（Editor 抽屉依赖，禁改名）。
│     ├─ BackpackTag.cs.meta                    # [NEW] MonoImporter 模板 + 新造唯一 GUID
│     ├─ BackpackTagConstants.cs                # [NEW] FragmentTags 5 条（Grid/Icon/Stackable/Consumable/Equipment）
│     │                                         #   + ItemTypeTags 4 条示例（Sword/RedCloak/Small 红药/FireFernFruit）；
│     │                                         #   照搬蓝本，仅改命名空间。
│     ├─ BackpackTagConstants.cs.meta           # [NEW] 同上
│     ├─ TagRegistry.cs                         # [NEW] SO 注册表：Current（注入 → Resources 兜底 → 空表）、Version、AllTags、
│     │                                         #   Contains、GetChildren、Validate、InvalidateCache、CreateRuntimeDefault、
│     │                                         #   OnValidate、域重载复位；文件末尾含 TagValidationIssue 结构体。
│     │                                         #   依赖 BackpackLog.Debug(LogModule.Inventory, ...)（本工程已就绪）。
│     │                                         #   分叉：CreateAssetMenu 菜单改 RPG4/Backpack/Tag Registry。
│     └─ TagRegistry.cs.meta                    # [NEW] MonoImporter 模板 + 新造唯一 GUID（被资产 m_Script 引用）
├─ Data/
│  ├─ TagRegistry.asset                         # [NEW] NativeFormatImporter 资产：m_Name=TagRegistry、m_Script 指向新 TagRegistry.cs GUID；
│  │                                            #   tags 恰 9 条：FragmentTags.Default.GridFragment / .IconFragment / .StackableFragment、
│  │                                            #   FragmentTags.Consumable.ConsumableFragment、FragmentTags.Equip.EquipmentFragment、
│  │                                            #   GameItems.Equipment.Weapons.Sword / .Cloaks.RedCloak /
│  │                                            #   .Consumables.Potions.Red.Small / .Craftables.FireFernFruit；
│  │                                            #   不得写入 Consume.Heal / Equip.Damage
│  └─ TagRegistry.asset.meta                    # [NEW] guid 独立于脚本 GUID
└─ Docs/
   ├─ 核心契约/
   │  ├─ F5_标签系统.md                          # [NEW] 蓝本规范改写副本：顶部「落盘根/命名空间/程序集」+ 上游权威文档与路线图引用 +
   │  │                                          #   「本工程落盘状态（2026-10-05）」横幅；表格增「本工程状态」列；
   │  │                                          #   Editor 抽屉 / Editor 校验 / 程序集行标注「本工程暂缓」；
   │  │                                          #   显式记录两项待办：Editor 三件套、注册表链路注入修复；
   │  │                                          #   命名空间、目录、菜单路径全部改为本工程口径
   │  └─ F5_标签系统.md.meta                      # [NEW] 手造唯一 GUID
   └─ 工程架构/
      └─ F1_F2_程序集与目录命名规范.md            # [MODIFY] 目录树 Tags 行改为「已落（三文件 + 注册表资产）」；
                                                  #   目录树 Data 行补 TagRegistry.asset；Editor 行维持「空目录」；
                                                  #   §1.2 表 Core.Editor 行补「本轮暂缓」；§5 下一步重写为
                                                  #   「F5 运行时部分已落 → 后续轮：Editor 三件套 + Editor 域注入 → F6 → F7」
```

## 关键契约（下游硬约束，供 M/F6 使用）

| 成员 | 语义 |
| --- | --- |
| `BackpackTag.MatchesTagExact(other)` | 整串序数相等；空标签任一侧为 false |
| `BackpackTag.MatchesTag(parent)` | 子对父层级匹配；空标签任一侧为 false |
| `BackpackTag.IsWellFormed / TryParse` | 格式校验；失败返回原因，TryParse 失败返回空标签 |
| `TagRegistry.Current` | 静态门面：显式注入优先 → Resources 兜底 → 运行时默认空表（不抛异常） |
| `TagRegistry.Contains / GetChildren` | 存在性查询 O(1)；子节点枚举，父节点无需登记 |
| `TagValidationIssue` | Context + Message 可读问题条目，随 TagRegistry.cs 同文件 |


## 执行注意（防回归）

- 除两处分叉外**禁止**改动任何文案、注释、空白与算法顺序，否则逐行比对失效。
- 三份 `.cs.meta` 用 MonoImporter 模板、资产 `.meta` 用 NativeFormatImporter 模板；全部 GUID 新造且与工程既有 51 份不重复。
- 资产 `m_Script` 的 GUID 必须与 `TagRegistry.cs.meta` 严格一致，且 `fileID: 11500000`、`type: 3`。
- 不新增 Tests、不装包、不改 asmdef、不动 `Data/LogSettings.asset` 与既有场景/预制体。
- 文档副本必须与落盘实际一致（9 条、菜单工具名仍为蓝本口径、Editor 明确暂缓），避免复刻蓝本「文档写 9、资产 11」的漂移。

## Agent Extensions

### Skill

- **command-execution-no-tests-no-comments**
- Purpose: 执行落盘与核对阶段，不运行测试、不擅自新增注释，符合本工程「零单测 + 与蓝本逐行一致」的选型。
- Expected outcome: 新增文件仅含从蓝本照搬的原有注释（差异仅命名空间与菜单路径），且不产生任何测试运行与测试文件。

### SubAgent

- **code-explorer**
- Purpose: 独立复核 `Scripts/Core/Tags/` 落盘内容与蓝本的逐文件差异清单，并扫描 RPG_4 工程内是否存在同名类型或残留引用冲突。
- Expected outcome: 输出逐文件差异结论（预期仅命名空间 1 处、菜单路径 1 处）与「零同名冲突、零残留引用」的确认。