---
name: F6_事件总线移植_RPG4
overview: 把蓝本 ZDXT2 的 F6 事件总线（EventBus / EventSubscription / EventChannels，最小集 9 条频道键）逐行移植到 RPG_4 的 Assets/backpack/Scripts/Core/Events/，仅做命名空间改名与去掉 Editor 自检，配套同步 F1_F2 规范与 F6 核心契约文档，验收方式为 Unity 编译 + 人工核对。
todos:
  - id: port-f6-events
    content: 照搬 Core/Events 三文件（EventBus / EventSubscription / EventChannels）并手写三个 .meta：命名空间改 RPG4.Backpack.Core、频道键裁到最小集 9 条、沿用蓝本 GUID
    status: completed
  - id: sync-f6-docs
    content: 新增 Docs/核心契约/F6_事件总线.md 与 .meta（去 Editor 自检、验收改编译+人工核对），并回写 F1_F2 规范第 27/65/174 行与文末进度段
    status: completed
    dependencies:
      - port-f6-events
  - id: verify-f6-parity
    content: 用 [subagent:code-explorer] 逐行比对蓝本与目标三文件、核对 GUID 唯一与 asmdef 零改动、搜索裸字符串键，输出差异报告
    status: completed
    dependencies:
      - port-f6-events
      - sync-f6-docs
  - id: handover-verify
    content: 用 [skill:ADHD友好输出] 交付 Unity 人工验收清单与下一步（Console 无 CS 报错、三频道键、定向投递、弱引用自动解绑）
    status: completed
    dependencies:
      - verify-f6-parity
---

# F6 事件总线移植（RPG_4）

把蓝本 ZDXT2 已跑通的 F6 事件总线照搬到 `e:/ue/RPG_4/Assets/backpack/Scripts/Core/Events/`，作为后续 M5/M6/M7 的通信基座。**只落运行时三文件 + 文档，不建 Editor 程序集、不写单测、不改任何 asmdef。**

## 交付物

| 文件 | 动作 | 要点 |
| --- | --- | --- |
| `Scripts/Core/Events/EventBus.cs` | 新增（照搬 787 行） | 仅 `namespace` 改 `RPG4.Backpack.Core`；其余逐行一致 |
| `Scripts/Core/Events/EventSubscription.cs` | 新增（照搬 34 行） | 同上改名 |
| `Scripts/Core/Events/EventChannels.cs` | 新增（蓝本 50 行裁到 9 条键） | 删 `Inv.PickupPrompt` / `Inv.Equipment.Changed`，尾部注释登记待启用 |
| 上述三个 `.cs.meta` | 新增（手写） | 沿用蓝本 GUID：`3a7f9c21d5be4a0f92e6b83c47d10a54` / `9f4c2a6e13d84b7f8a05c96e2b47d3f9` / `5d1b8e34a9c74f26b0e59d7a2c86f431`（已核对 RPG_4 内 0 冲突） |
| `Docs/核心契约/F6_事件总线.md` + `.meta` | 新增 | 由蓝本 `Docs/规范/核心契约/F6_事件总线.md` 改写：去自检菜单、验收改编译+人工核对、路径改 RPG4 |
| `Docs/工程架构/F1_F2_程序集与目录命名规范.md` | 修改 | 第 65 行 `Events/ 已落（空）`→`已落（三文件）`；第 27 行 Core.Editor 注「F6 本轮仍未建」；第 174 行 F6 状态；文末追加 F6 进度条目 |


## 关键决策

1. 频道键只落**最小集 9 条**（`Inv.*` / `Cmd.*` / `Ui.*` 各 3 条 + 嵌套 `MouseButton`），键值以蓝本源码原文为准，逐个核对后再写入。
2. 不建 `RPG4.Backpack.Core.Editor` 程序集、不移植 `EventBusSelfCheck.cs`；路线图的「通过单测」在本轮降级为「编译 + 人工核对」，该偏差须写进 F6 文档与 F1_F2 进度段。
3. `RPG4.Backpack.Core.asmdef` 保持 `references: []`，5 个 asmdef 全部零改动。
4. 不新增目录（`Core/Events/` 与目录 meta 已在位）、不引入任何包。

## 硬约束

- **`.meta` 手写**（非 git 仓库）：`.cs.meta` 照抄 `Core/Tags/BackpackTag.cs.meta` 模板，`.md.meta` 照抄 `Docs/核心契约/F5_标签系统.md.meta` 模板（GUID 不得与既有重复）。
- **照搬纪律**：不优化、不重排、不增删注释；与蓝本的差异只允许「命名空间前缀」+「频道键 11→9」两类，其他漂移必须在文档登记。
- **静态自检**：三文件不得残留 `ZDXT`；全工程搜 `Subscribe("` / `Publish("` 字面量为 0；IDE 诊断零 error。
- 本轮无消费方（全工程无 `EventBus|EventChannels|EventSubscription` 引用），blast radius 为零。

## 验收

1. Unity 刷新后 Console 无 CS 报错（用户执行）。
2. 人工核对三项语义：三类频道键集中定义、source 定向投递（同键不同实例不串台）、监听者销毁自动解绑。
3. 落盘后由 `code-explorer` 逐行比对蓝本与目标，输出差异报告。

## 明确不做

World 级来源通配、监听顺序控制、请求-响应、订阅次数上限、Editor 自检、单测、Mirror/Input System 相关改动。