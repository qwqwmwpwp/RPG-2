# 角色意图迁移审查约束

基线：2026-10-26 任务开始时的工作树（包含用户既有未提交修改），不是 Git HEAD。本文与同目录脚本仅作本次审查支持。

## 必须保持的时间关系

- `PlayerInput.Update(-200)` 采样后，`PlayerViewController.Update(-100)` 必须能读本帧 Look/Zoom 与许可。不能把所有快照准备移至 Player.Update(150)。
- View.Update 先还原上一帧骨架与俯仰，然后更新独立逻辑观察角；Player.Update(150) 执行上身状态 → 合并运动约束 → 下身状态 → 同步表现。移动始终使用逻辑观察偏航，不从相机避障位置反推。
- FixedUpdate 可能在本帧 Update 前执行，也可能每渲染帧零次/多次。Move/Crouch/Aim/FireHeld 保留最近有效采样；Look/Zoom/Interact/Reload/FirePressed 仅对采样所在 frameCount 有效。
- Animator/手部 IK 后，View.LateUpdate(-100) 叠加上身俯仰与骨架偏航；Camera.LateUpdate(50) 调距离/FOV；Brain.LateUpdate(100) 解算实际相机。Brain 的 100 来自包内 `CinemachineBrain.cs.meta:8`。
- Interaction.LateUpdate(100) 与 Brain 同优先级，相对顺序没有保证；Interaction 可能开窗口，Player.LateUpdate(150) 必须据实时门控取消。原架构已经不能保证这种晚开 UI 当帧相机重新解算，不应假定可以撤销已完成的相机帧。

## 快照、取消和帧末

- `CanReadGameplayInput` 原来是实时检查并清理输入的门控，条件含启用、焦点、timeScale、InputManager、UIManager。UI 打开和关闭当帧均屏蔽；不能只缓存采样时许可。
- 光标捕获点击消耗 Fire，重新捕获/解除 UI 占用后的首个可读 Look/Zoom 丢弃。解锁、失焦、硬取消后 Aim/Fire 必须先观察释放再重新武装。
- Interaction 在调用目标后立即复核门控和目标有效性，避免开窗后仍显示准心/可交互目标。
- 硬取消只同步已经活动的状态链，不 PrepareFrame、不重复消费 fire/reload、不重新启动已 Stop 的链。
- 晚取消时 View 已应用姿态：原 `SetWeaponMode(false,false,false)` 同步调用 `View.SetMode(false,false)→RestorePose` 及 `AimPose.SetAiming(false)→ResetPose→RestorePose`。只写 BB 的 bool 而不触发这些撤销，会遗留一帧骨架补偿。
- AimPose 必须保留本帧 final IK layer 的 frameCount 栅栏，避免无本帧 IK 时叠加俯仰；WeaponIK 原来在 Update 和 OnAnimatorIK 都实时检查有效性并清零权重，不能仅看过期动作状态。

## 开火、换弹和蹲伏

- `PlayerFireAction.Cancel()` 保留 `nextShotTime`。硬取消/换弹/Stop 不应重建动作对象或重置冷却。
- 开火本帧最多一发，不补帧；冷却内新点按不排队，不继承连射计数。松键不改变已发出一发/淡出的表现类型。
- 换弹完成帧不消费 fire，并保留 `fireRearmFrame`；下一帧起先看到释放，再允许新开火。普通换弹不 ClearWeaponInput，保留右键意图；硬取消要求松键重按。
- 换弹退出保留原 `SwitchSocketByString("To_Hand_R_Socket")` 恢复，防止动画尾事件被跳过。
- 蹲伏请求不等于实际胶囊姿态。Update 合并实际 PhysicallyCrouched/CanStand，Fixed 再检查站起；失败时保持蹲伏并限速。
- Stop 保持胶囊高度，只清平面速度（保留 Y），恢复 Strafe 临时角速度上限；恢复时仍遵守低顶阻挡。

## 纯静态编译入口

独占脚本：`compile-intent-static.ps1`。已全文审查旧 `shoot-simulation/static-check/compile-static.ps1`：旧脚本只调用 dotnet+csc，不加载编译产物，安全但只覆盖四个固定程序集。

新入口发现 Assets/Scripts 下全部 asmdef，按依赖拓扑编译并将每个 `.cs` 分配给最近 asmdef，因此包括新 PlayerIntent 程序集和新增源码；依赖采用实际 asmdef references，外部包只用 Library/ScriptAssemblies DLL 作为编译器元数据。另将 Character_Weapon_Controller 单独编译以检查默认程序集消费入口，不加载产物。默认检查运行分支，`-EditorSymbols` 检查 UNITY_EDITOR 分支。遇到不能忠实覆盖的复杂 asmdef 设置显式失败。

命令（代码就绪后执行）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .codex-task-snapshot\intent-blackboard-20261026\audit\compile-intent-static.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .codex-task-snapshot\intent-blackboard-20261026\audit\compile-intent-static.ps1 -EditorSymbols
```

工具已存在：Unity 2022.3.62f3 自带 NetCoreRuntime/dotnet.exe、DotNetSdkRoslyn/csc.dll、NetStandard 2.1 与 Engine/Editor 引用。脚本 PowerShell 纯解析检查通过；按开发代理要求，业务尚未就绪前不执行编译。

边界：不启动 Unity、不导入、不运行场景/游戏、构建、测试、分析器或生成器，不反射/加载/执行业务 DLL。上述编译不能证明场景绑定、实际帧时序和运行行为正确，也不等同完整项目或整个默认程序集编译。
