# 角色双状态机

`Player` 在同一个角色上持有两个现有 `RPG4.StateMachine.StateMachine` 实例。上身处理武器行为，下身处理姿态、平面运动和腿部动画。`State.cs`、`StateMachine.cs` 保持原框架；本模块不加入弹药、伤害、蓄力或网络系统。

## 操作

| 输入 | 行为 |
| --- | --- |
| WASD | 逻辑观察角 yaw 相对移动 |
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

## 视角、角色与相机

```text
player（动态 Rigidbody、Player、Animator）
├─ 可见骨架
└─ AimPivot（PlayerViewController，Cinemachine 的 Follow 目标）

PlayerCamera（独立相机预制体）
├─ Main Camera（CinemachineBrain）
└─ Player Follow（虚拟相机、3rd Person Follow、PlayerCameraController）
```

`AimPivot` 在角色预制体中配置位置和初始局部旋转，位置由角色层级继承。`PlayerViewController` 保存独立的世界观察角，并通过 `Inverse(parent.rotation) * desiredWorldRotation` 更新节点局部旋转。角色转身不会额外带偏视角；无需从另一个 CameraTarget 复制位置。Main 场景把虚拟相机的 Follow 和相机控制器的 viewController 绑定到同一个 AimPivot。

Free 模式中，鼠标旋转视角，静止角色保持朝向，移动角色转向实际移动方向。Strafe 模式（瞄准、射击动作期间、换弹）中，角色以同一个 ViewYaw 作为转向目标，左右移动使用横移动画。ADS 只影响视角灵敏度、相机距离和 FOV；上身俯仰仍只在射击动作期间启用。角色通过 `stateData.content.viewController` 获取观察角，不引用相机控制器。

执行顺序：

1. `PlayerInput.Update`（-200）采样输入。
2. `PlayerViewController.Update`（-100）撤销上一帧骨架与俯仰补偿，读取输入更新观察角和节点局部旋转。
3. `Player.Update`（150）更新上下身状态、移动计划与动画，并向视角组件同步 Free/Strafe、ADS 状态。角色刚体在独立的 FixedUpdate 物理步中读取最近的计划；它不固定排在本帧 Update 之后。
4. Animator 与手部 IK 求值后，`PlayerViewController.LateUpdate`（-100）统一应用上身俯仰、Strafe 骨架偏航补偿，再依据父节点的显示旋转更新 AimPivot。
5. `PlayerCameraController.LateUpdate`（50）仅处理距离与 FOV，`CinemachineBrain.LateUpdate`（100）负责实际相机的位置、旋转、构图和避障。滚轮只调整目标距离，不从避障后的位置回写距离。

`PlayerAimPose` 由视角组件直接调度，不再向相机注册。未收到本帧最终 IK 层回调时不叠加俯仰。输入占用、暂停、失焦、失控或组件停用时撤销不再有效的姿态补偿。

骨架偏航补偿已合并进 `PlayerViewController`：仅在 Strafe 且可控时，把插值物理根的显示 yaw 到本帧 ViewYaw 的最短角差叠加到可见骨架，不写物理根。动态刚体以 0.02 秒固定步更新并启用 Interpolate，而视角按渲染帧更新；子节点结构不能消除这两种时钟的相位差，因此保留此同步。Strafe 动画同样以 ViewYaw 为基准，Free 仍以物理朝向为基准。

迁移前的人工反馈确认过正式 Cinemachine 模式下站立瞄准转镜头平滑；该反馈只覆盖旧实现，不代表本次迁移已通过运行验证。临时简易相机、独立骨架偏航组件以及旧的相机姿态注册接口均已由当前结构取代。

换弹由 Reload 叶记录逻辑起止时间，不读取 Animator 当前状态决定完成。`ReloadSpeed` = 当前换弹 clip 长度 / 配置 duration；站蹲换弹互切使用同一逻辑归一化进度。保留既有 SwitchSocket 动画事件，换弹退出时额外通过既有 `SwitchSocketByString("To_Hand_R_Socket")` 接口恢复右手挂点，防止中断或跳过尾帧事件后武器留在辅助挂点。

取消射击、换弹、中断和停机均保留 `PlayerFireAction` 冷却；仅新建运行时对象初始化冷却。普通换弹不调用 `ClearWeaponInput`，避免清除右键意图；硬中断仍清理武器输入并要求松开重按。

## 蹲伏与配置

下身保存启动时的站立胶囊高度/中心。蹲伏改变高度和中心并保持底部位置；站起前使用非分配胶囊重叠检测，排除自身、同一刚体碰撞体和 trigger，检测形状向内缩 0.01 米以排除正常地面接触。32 项检测缓冲满时保守保持蹲伏；Fixed 写入前再次检查空间。停止、失去控制或恢复组件不强制扩张胶囊，低顶下松 Ctrl 仍保持蹲伏。AimPivot 保持配置的站立高度偏移，不随胶囊高度自动改变。

现有内联序列化配置继续使用 `stateData.content`、`exploration`、`attack`、`aim`、`hipFire`。视角灵敏度、俯仰范围和 ADS 灵敏度倍率位于 AimPivot 的 `PlayerViewController`；初始朝向直接使用该节点的旋转。相机距离、FOV 和过渡时间位于 `PlayerCameraController`。自由运动使用 Blend Tree，旧的独立 Idle/Move 动画字符串配置已移除。

| 新配置路径 | 默认值 / 用途 |
| --- | --- |
| `stateData.crouch.moveSpeed` | 2 米/秒，作为蹲伏各运动模式的速度上限 |
| `stateData.crouch.height` | 1.2 米，最小为胶囊直径，最大为初始站立高度 |
| `stateData.reload.duration` | 1.6 秒，逻辑换弹时长 |
| `stateData.reload.moveSpeed` | 2 米/秒，独立换弹移速 |
| `stateData.reload.reloadClip` | 站立 R_Reload |
| `stateData.reload.crouchReloadClip` | 蹲伏 R_CrouchReload |

基础层状态协议为 `Base Layer.FreeLocomotion`、`AimLocomotion`、`CrouchFreeLocomotion`、`CrouchAimLocomotion`。自由运动参数 `MoveSpeed` 为输入幅度 0..1，蹲伏树 Idle/Walk/Jog 阈值为 0/.5/1；方向树使用 `AimMoveX`、`AimMoveY`、`AimLocomotionSpeed`。上层 `UpperBody Shooting` 保留 Empty、HipFire、ADSFire、HipAutoFire、ADSAutoFire，新增 Reload、CrouchReload；参数为原 `ShotSpeed` 与新增 `ReloadSpeed`。

## 静态验证边界

验证采用静态 Roslyn 编译、GUID/fileID 与场景预制体引用核对，不启动 Unity、导入资源、构建或执行单元/场景测试。旧静态验证历史与迁移前人工反馈不代表本次迁移的运行结果。

本次迁移需要人工运行确认：自由观察时角色转身不会带偏镜头，瞄准/腰射/换弹的转向与横移连续，快速转镜头及移动瞄准的角色显示稳定，UI/暂停/失焦/组件停启后无残留姿态。现有 Rigidbody 不增加专用台阶或接地系统。
