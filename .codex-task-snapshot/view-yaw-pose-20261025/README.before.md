# 角色双状态机

`Player` 在同一个角色上持有两个现有 `RPG4.StateMachine.StateMachine` 实例。上身处理武器行为，下身处理姿态、平面运动和腿部动画。`State.cs`、`StateMachine.cs` 保持原框架；本模块不加入弹药、伤害、蓄力或网络系统。

## 操作

| 输入 | 行为 |
| --- | --- |
| WASD | 相机 yaw 相对移动 |
| 鼠标移动 / 滚轮 | 观察 / 探索距离缩放 |
| 按住鼠标右键 | 瞄准镜头、观察方向转身和方向移动 |
| 鼠标左键 | 点按点射，持续按住按配置节拍连射；Normal 可腰射 |
| 按住 Left Ctrl | 蹲伏，松开后有足够空间才站起 |
| 按下 R | 换弹动画模拟；重复按 R 不重启换弹 |
| Escape / 未捕获光标时左键 | 释放 / 重新捕获光标；捕获点击不发射 |

换弹不会扣减或补充弹药。换弹时继续使用 Strafe 移动与独立换弹速度，不进入 ADS 镜头，不允许开火打断；右键意图保留，逻辑完成后按当前右键状态返回 Aim 或 Normal。完成帧不消费射击；下一帧起先观察到左键松开，再按才可发射，换弹期间的按住或点按不会穿透成一次发射。UI、暂停、光标释放等硬中断取消换弹，恢复后需要重新按键。

## 状态和执行顺序

```text
UpperBodyRootState
├─ NormalState       自由运动；有共享开火动作时请求腰射 Strafe
├─ AimState          瞄准运动和镜头；可使用同一个开火动作
└─ ReloadState       进入时取消射击；逻辑计时完成或硬中断时退出

LowerBodyRootState
├─ Standing          PostureState 实例
│  ├─ Free           LocomotionState 实例
│  └─ Strafe         LocomotionState 实例
└─ Crouching         PostureState 实例
   ├─ Free           LocomotionState 实例
   └─ Strafe         LocomotionState 实例
```

每个 Update 先准备上身的本帧时间与可用性，再执行 `upperStateMachine.Tick` → `LocomotionResolver.Resolve` → `lowerStateMachine.Tick`。最后由上身同步武器表现，此时已可使用本帧下身姿态选择站立或蹲伏换弹动画。上身叶负责转换、动作和完整 `UpperBodyDemand`；resolver 结合输入、实际胶囊姿态、起身空间及速度限制生成 `LocomotionPlan`；下身叶消费计划。两机不直接切换对方状态。

所有 `GetTransition` 仅返回同一父状态的兄弟。Standing/Crouching 的 `GetInitialState` 按当前计划选择 Free/Strafe，姿态和风格同帧改变时直接进入正确叶。普通 FixedUpdate 只调用下身 `FixedTick`，正常刚体平面速度、转向和胶囊修改均在下身物理更新中执行；写平面速度保留刚体 y 速度。生命周期 Stop 是清速例外：下身根退出时立即清除水平速度，保留 y，避免禁用组件后无法再执行 FixedUpdate 而残留移动；同时清零移动动画参数并按实际站/蹲胶囊姿态切回自由运动待机，Animator 暂时禁用时也清理可访问的参数和状态。

Strafe 模式（瞄准、开火动作期间或换弹）每个物理步先设置平面速度并清除旧角速度，再将当前刚体 yaw 到计划 `ViewYaw` 的最短有符号角除以本步时长、转换为 rad/s，通过 `Rigidbody.angularVelocity` 提交偏航。目标为本物理步追到观察角，不增加转向平滑或缓冲；实际积分与接触约束仍由物理系统处理。为避免默认角速度上限截断快速转身，Strafe 期间保存原 `maxAngularVelocity`，每步仅按需提高至本步所需值；退出 Strafe、失去控制、不能移动、刚体变为运动学或停止时恢复原值。Free 模式移动时继续使用原有 SmoothDamp 和 MoveRotation 平滑转向行走方向，站立时保持原朝向。

Stop 让两机从叶到根退出，并清理输入；取消同步只 Tick 已活动的链，不会启动停止的状态机。LateUpdate 对 UI 等晚于 Update 的占用只取消和同步，不重复准备或消费本帧射击/换弹。

## 射击、表现与中断

`UpperBodyRootState` 持有唯一 `PlayerFireAction` 和 `PlayerWeaponPresentation`。Normal/Aim 互切不取消开火，不重置冷却、末发、淡出和动画进度。每帧最多产生一发，卡顿不补发，冷却内点按不排队；同一按住手势第二发起使用循环连射表现，松开仍保留末发与淡出。

腰射动作存续（`HasWeaponAction`，含按住等待冷却）使用原 HipFire 移速；Aim 使用原 Aim 移速。单发跨 Hip/ADS 按逻辑 ShotPhase 延续，循环连射只在进入时过渡并保留 Animator 相位。Animator 暂不可用不阻止模式、计时、移动和冷却；射击投影恢复后只响应当前新发，不补播丢失期间的射击。

`Player.Input` 和 `Player.WeaponIKEnabled` 保持公开。武器手部 IK 与 `PlayerAimPose` 俯仰只在实际 `HasShotAction`、武器可用且 dt > 0 时启用。纯 Aim 不新增 IK 或俯仰；换弹仅提高上身动画层权重，不复用普通射击 IK/俯仰。

视觉帧由 `PlayerCameraController` 统一调度。`PlayerAimPose.Initialize` 向对应相机注册后，不再单独运行 Update/LateUpdate：相机 Update 先调用 `BeginFrame` 撤销上一帧俯仰，再读取输入生成观察角；角色 Update 消费观察角并更新状态与动画；Animator 完成本帧动画和手部 IK 后，相机 LateUpdate 先调用 `ApplyPose(viewPitch)`，再更新 CameraPivot、距离和 FOV，最后由执行顺序为 100、使用 LateUpdate 的 CinemachineBrain 输出相机。IK Pass 仍由 Animator 驱动，未收到最终 IK 层回调时不叠加俯仰。相机或姿态组件禁用时立即撤销残留补偿。刚体继续在 FixedUpdate 中移动和转向并使用插值；俯仰平滑参数、射击姿态开关和动画表现不变。

换弹由 Reload 叶记录逻辑起止时间，不读取 Animator 当前状态决定完成。`ReloadSpeed` = 当前换弹 clip 长度 / 配置 duration；站蹲换弹互切使用同一逻辑归一化进度。保留既有 SwitchSocket 动画事件，换弹退出时额外通过既有 `SwitchSocketByString("To_Hand_R_Socket")` 接口恢复右手挂点，防止中断或跳过尾帧事件后武器留在辅助挂点。

取消射击、换弹、中断和停机均保留 `PlayerFireAction` 冷却；仅新建运行时对象初始化冷却。普通换弹不调用 `ClearWeaponInput`，避免清除右键意图；硬中断仍清理武器输入并要求松开重按。

## 蹲伏与配置

下身保存启动时的站立胶囊高度/中心。蹲伏改变高度和中心并保持底部位置；站起前使用非分配胶囊重叠检测，排除自身、同一刚体碰撞体和 trigger，检测形状向内缩 0.01 米以排除正常地面接触。32 项检测缓冲满时保守保持蹲伏；Fixed 写入前再次检查空间。停止、失去控制或恢复组件不强制扩张胶囊，低顶下松 Ctrl 仍保持蹲伏。相机保持原站立 CameraTarget 偏移，本次不动态创建 UI 或调整摄像机节点。

现有内联序列化配置继续使用 `stateData.content`、`exploration`、`attack`、`aim`、`hipFire`；原状态内嵌 Data 类移为独立数据类，不再运行旧单机状态。Aim 的 `turnSmoothTime` 已移除，跟随视角时直接采用目标朝向。Exploration 的旧 idleAnimation/moveAnimation 字符串仍保留序列化值，实际自由运动使用新的 Blend Tree。

| 新配置路径 | 默认值 / 用途 |
| --- | --- |
| `stateData.crouch.moveSpeed` | 2 米/秒，作为蹲伏各运动模式的速度上限 |
| `stateData.crouch.height` | 1.2 米，最小为胶囊直径，最大为初始站立高度 |
| `stateData.reload.duration` | 1.6 秒，逻辑换弹时长 |
| `stateData.reload.moveSpeed` | 2 米/秒，独立换弹移速 |
| `stateData.reload.reloadClip` | 站立 R_Reload |
| `stateData.reload.crouchReloadClip` | 蹲伏 R_CrouchReload |

基础层状态协议为 `Base Layer.FreeLocomotion`、`AimLocomotion`、`CrouchFreeLocomotion`、`CrouchAimLocomotion`。自由运动参数 `MoveSpeed` 为输入幅度 0..1，蹲伏树 Idle/Walk/Jog 阈值为 0/.5/1；方向树使用 `AimMoveX`、`AimMoveY`、`AimLocomotionSpeed`。上层 `UpperBody Shooting` 保留 Empty、HipFire、ADSFire、HipAutoFire、ADSAutoFire，新增 Reload、CrouchReload；参数为原 `ShotSpeed` 与新增 `ReloadSpeed`。

## 简单相机对照测试

`PlayerCamera` 预制体的 `Main Camera` 默认启用 `SimplePlayerCameraTest`。运行时它保存并禁用 CinemachineBrain 与 Player Follow 虚拟相机，再在执行顺序 200 的 LateUpdate 中，直接将 Main Camera 放在 `CameraPivot.position + CameraPivot.rotation * (0.45, 0.35, -2)`，旋转完全采用 CameraPivot；没有位置/旋转平滑、避障或额外输入采样，FOV 使用 Main Camera 当前值。

`PlayerCameraController` 继续读取鼠标、维护观察角和更新同一个 CameraPivot，角色和输入流程保持原状。测试模式下，滚轮距离和瞄准 FOV 仍只写虚拟相机，不改变简单相机的固定偏移与 FOV。

运行时取消勾选 Main Camera 上的 `SimplePlayerCameraTest` 即恢复两个 Cinemachine 组件接管前的启用状态，并清除虚拟相机的历史状态；重新勾选可再次进入简单模式。预制体中 Brain 和虚拟相机保留原启用配置，避免破坏原控制器的初始化依赖。对比站立转鼠标时的画面顿挫；此组件不输出临时日志。

## 静态验证边界

本次允许静态差异审阅、类型/符号编译和资源引用核对；未启动 Unity、导入资源、构建、进入 Play Mode 或执行单元/场景测试。最终静态编译与资源报告由本轮任务统一记录在 `.codex-task-snapshot/body-fsm-20261025`。旧 README 中此前任务的编译历史不作为本次结果。

仍需人工运行确认：站蹲与 Free/Strafe 同帧变化、低顶起身和地面/斜坡接触、射击节拍和 Hip/ADS 连续性、换弹站蹲相位与挂点、换弹中断及左键重新按下、Animator 禁用恢复、UI/暂停/失焦/组件停启。现有 Rigidbody 不增加专用台阶或接地系统。
