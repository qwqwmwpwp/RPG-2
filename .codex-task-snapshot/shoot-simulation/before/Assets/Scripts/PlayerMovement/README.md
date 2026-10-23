# 第三人称肩后相机与角色移动

打开核心场景 `Assets/Scenes/Main.unity`。场景配置了 Player 预制体、InputManager、Cinemachine 相机、地面、遮挡墙、碰撞方块和低台阶，并整合背包窗口、箱子和普通交互物。

角色通过带动态 Rigidbody 的 CapsuleCollider 参与 Unity 物理。Player 的活动状态在 FixedUpdate 中驱动物理移动；Player.Update 负责状态转换与动画。刚体使用项目 Physics.gravity（当前为 -9.81 m/s²），插值开启并冻结 X/Z 旋转；移动更新水平速度时保留刚体当前垂直速度。

- **WASD**：相对观察水平角移动，人物平滑转向移动方向；松开后保留朝向，斜向移动不会加速。
- **鼠标移动**：自由环绕观察，原地转镜头不会改变人物朝向。
- **滚轮**：探索时平滑调整相机距离，当前相机 Prefab 范围 2～3 米；瞄准时忽略滚轮并保留探索距离。
- **按住鼠标右键**：进入瞄准，角色朝向观察水平角，WASD 相对该方向前后及侧向移动；松开回到探索。瞄准移动速度为 2.5 米/秒，斜向不会加速。
- **瞄准镜头与姿态**：距离平滑切到 1.5 米、垂直 FOV 切到 45°、鼠标灵敏度乘 0.65；退出恢复探索的滚轮距离和原 FOV。上身随观察俯仰调整，枪械沿用原右手挂点与左手 IK。
- **Esc**：释放鼠标；左键单击重新捕获。切出应用时释放鼠标，禁用 PlayerInput 时恢复此前光标状态。

## 职责

- **摄像机**：PlayerCameraController 读取 PlayerInput，维护 yaw、pitch 和目标距离，并更新独立 CameraPivot。虚拟相机使用 3rd Person Follow 处理肩后构图、阻尼和避障，Brain 将状态应用到 Main Camera。
- **角色**：Player 是 MonoBehaviour 状态机宿主。PlayerInput 统一玩法输入；Player 的 stateData 持有内容引用、探索及瞄准配置。PlayerRootState 是探索和瞄准共用的根状态；探索父状态与 IdleState、MoveState 共享 `ExplorationState.Data`，活动状态在 FixedUpdate 更新刚体运动，Player.Update 进行状态切换和动画更新。
- **UI**：UIManager 管理窗口，InventoryInputScope 保存和恢复窗口期间的光标状态。窗口打开时及开关当帧，BlocksGameplayInput 屏蔽相机、角色和世界交互输入；物理重力和相机更新继续运行。HasOpenWindows 表示实际窗口状态。

InputManager 独占输入资产实例和映射生命周期；角色根节点上的 `RPG4.Input.PlayerInput` 统一维护 Move、Look、Zoom、AimHeld、InteractPressed 及玩法门控。状态、相机和世界交互读取该组件，不直接访问 InputActions。Look 使用鼠标 delta，Zoom 使用滚轮 Y 并按每刻度 120 单位换算。FixedUpdate 使用最新 Move 输入快照。

窗口占用、光标释放、失去焦点、暂停或组件禁用时取消瞄准并清理输入。恢复玩法后需要松开右键再按下，避免占用期间按住的右键直接重入瞄准。相机镜头恢复使用 unscaledDeltaTime，暂停时仍能恢复；禁用相机控制器会立即恢复探索距离与 FOV。

输入延迟、缓存和重复触发策略由 PlayerInput 负责，本轮没有新增这些机制。物理更新读取的是最新 Move 快照，不是排队的按键缓冲。

## 状态机与数据

Player 要求角色根节点存在 Rigidbody、CapsuleCollider 和自定义 PlayerInput。Player.Update 调用状态机的普通更新，负责状态转换和动画；即使暂停时 deltaTime 为零，仍执行状态转换以及时取消瞄准。StateMachine.FixedTick 调用活动状态的物理更新。PlayerRootState 作为共同根连接 ExplorationState 和 AimState；探索父状态的 IdleState 与 MoveState 共享同一份 `ExplorationState.Data`。

Player 的 `stateData.content` 保存 Animator、Rigidbody（字段名 `body`）和 PlayerCameraController 引用；相机引用由场景显式配置。`stateData.exploration` 保存移动速度、转向平滑时间、输入阈值、动画名称和过渡时间。缺少 Rigidbody、Animator 等角色自身引用时由 Player 初始化补齐。CameraTarget 是角色身上的位置锚点；角色不修改实际相机，也不读取最终镜头旋转。

PlayerContext 在 Player 初始化时为每个角色单独创建，统一传给各状态，保存输入、内容数据和 ExplorationState.Data 的运行时引用。输入来自 PlayerInput，观察角来自 CameraController.ViewYaw；移动方向在当前子状态局部计算。

`AimState` 与探索状态切换，通过 `PlayerCameraController.SetAiming(bool)` 维护镜头模式。`stateData.aim` 保存瞄准移动、转向和动画配置，`stateData.content.aimPose` 显式引用根节点的 `PlayerAimPose`，与 Animator 在同一物体。姿态读取逻辑 `ViewPitch`，只调整 Spine/Chest；俯仰限制默认 -30°～45°，平滑时间 0.1 秒。俯仰不改变角色根节点或碰撞体。

`Base Layer.AimLocomotion` 使用 AimMoveX/AimMoveY 的 2D Simple Directional Blend Tree。参数取角色局部水平速度除以瞄准速度，范围为 [-1, 1]；中心使用 R_AimIdle，六个方向使用 R_AimWalk_F/B/FL/FR/BL/BR，斜向节点为单位向量 (±0.70710677, ±0.70710677)。纯左/右输入由前后斜向步行动画混合。保留 Idle/Run，Root Motion 关闭，Base Layer 开启 IK Pass，让现有 Character_Weapon_Controller 继续驱动左手 IK。

IdleState 在移动输入长度超过配置阈值时转入 MoveState，MoveState 在输入低于或等于阈值时转回 IdleState。默认阈值为 0.01。玩法输入受屏蔽或观察相机不可用时回到 IdleState。状态转换由 State.GetTransition 和 StateMachine.Tick 完成，探索父状态在转换期间保持激活。

IdleState 使用物理更新清除水平速度分量并保留垂直速度，播放 idle；MoveState 根据 ViewYaw 计算方向，在物理更新中写入水平速度并保留垂直速度，播放 run。Unity 重力由 Rigidbody 自动施加。玩家旋转通过刚体旋转更新，冻结 X/Z 旋转避免碰撞造成俯仰或翻滚。动画跟随状态转换，同名动画不会重复过渡；Animator 暂不可用时后续更新会重试。

## 预制体与参数

`Assets/player/player.prefab` 使用半径 0.3 米、高 1.8 米、Y 轴方向、中心为 `(0, 1, 0)` 的非触发 CapsuleCollider；Prefab 与 Main 场景均为此尺寸。Rigidbody 为动态刚体，质量 1、使用重力、插值开启，冻结 X/Z 旋转并使用 Continuous Dynamic 碰撞检测。Unity Rigidbody 胶囊不提供旧 CharacterController 的自动台阶处理；斜坡、低台阶通行与接地表现仍需在运行场景中人工确认。此迁移未增加专用台阶或接地系统。

Player 的 State Data → Exploration 默认移动速度为 4 米/秒，当前 Prefab 配置为 6 米/秒；转向平滑时间 0.12 秒、输入阈值 0.01；此 Prefab 实际配置的待机动画为 `Base Layer.Idle`、移动动画为 `Base Layer.Run`，过渡时间为 0.25 秒。代码默认动画名称仍为小写 `Base Layer.idle` 和 `Base Layer.run`。Content 保存 Animator、Body、Camera Controller 和 Aim Pose 引用。角色 Animator 关闭 Root Motion，更新模式为 Normal。新角色模型保留 MagicaCloth、武器控制和面部阴影组件。

State Data → Aim 配置为：移动速度 2.5 米/秒、转向平滑时间 0.12 秒、输入阈值 0.01、动画 `Base Layer.AimLocomotion`、状态过渡 0.15 秒、方向参数平滑 0.1 秒。

本次角色 Prefab 迁移以 `Assets/player/player1.prefab` 的完整新角色模型替换旧角色，并将 Rigidbody、CapsuleCollider、PlayerInput、Player、PlayerInteraction、BackpackInventory、CharacterBackpack 和 CameraTarget 配置迁入最终的 `Assets/player/player.prefab`；Main 场景已重新绑定相机、输入与交互引用。最终资源路径为 `Assets/player/player.prefab` 和 `Assets/player/player.controller`，Prefab 保留原 `player1` GUID；旧 `player.prefab`、旧 Controller 及 `idle.anim`、`run.anim` 已清理，Sci-Fi 模型素材包保留。

`Assets/player/PlayerCamera.prefab` 包含 Main Camera、Cinemachine Brain、CameraPivot 和带 PlayerCameraController 的虚拟相机。CameraPivot 与人物分离，虚拟相机 Follow 指向 CameraPivot，Look At 留空，Aim 为 Do Nothing。

| 配置位置 | 参数 | 当前值 |
| --- | --- | --- |
| PlayerCameraController | 初始 yaw / pitch | 0° / 12° |
| PlayerCameraController | 水平 / 垂直灵敏度 | 0.15 / 0.12 度每输入单位 |
| PlayerCameraController | 俯仰范围 | -35°～65°，负数向上 |
| PlayerCameraController | 探索初始距离 / 距离范围 / 缩放平滑时间 | 2 米 / 2～3 米 / 0.1 秒 |
| PlayerCameraController | 瞄准距离 / FOV / 灵敏度倍率 / 过渡平滑时间 | 1.5 米 / 45° / 0.65 / 0.15 秒 |
| 3rd Person Follow | Shoulder Offset / Vertical Arm Length | (0.45, 0.1, 0) / 0.25 米 |
| 3rd Person Follow | Camera Side / Damping | 1（右肩）/ (0.2, 0.2, 0.2) |
| 虚拟相机 Lens | 垂直 FOV / 近远裁剪 | 60° / 0.1～1000 米 |

3rd Person Follow 使用 Default 层碰撞检测并忽略 Player 标签。碰撞半径 0.2 米，进入碰撞阻尼为 0，恢复阻尼为 0.35。需要避障的物体必须有碰撞体并位于检测层；角色根保持 Player 标签。避障只修正实际镜头位置，不改写滚轮设定的目标距离。Brain 的 Update Method 与 Blend Update Method 均使用 LateUpdate。

## 在其他场景使用

放入 Player、PlayerCamera 和 InputManager 预制体。角色根节点保留 Player、动态 Rigidbody、CapsuleCollider 和自定义 PlayerInput；Player 的 Player Input 指向该输入组件。State Data → Content 的 Body、Animator 和 Camera Controller 分别指向角色刚体、Animator 和相机控制器；相机控制器绑定角色子节点 CameraTarget、相机预制体内 CameraPivot 及同一个 PlayerInput。世界交互也绑定此输入组件，交互相机引用实际 Main Camera。

禁用 Player 时由 Player.Stop 退出活动状态并清理输入快照；PlayerInput 仍启用时继续维护相机和交互输入。重新启用 Player 后，状态机在下一次有效 Update 自动启动并按输入选择状态。PlayerMovement 与 PlayerMotor 已不再是角色移动组件。

本模块供本机玩家使用。PlayerRootState 共同根下包含 ExplorationState 父状态、IdleState/MoveState 子状态及 AimState；本次瞄准只提供姿态、镜头和移动，不包含射击、伤害、弹药、跳跃或冲刺。

## 验证记录

本次瞄准整合仅做代码与资源静态核对：检查输入/状态/镜头接口、七个动画的 meta GUID 与 AnimationClip fileID、Blend Tree 参数和单位方向、PlayerAimPose 与状态数据引用、Root Motion 和 IK Pass，以及 Main 场景继承的相机绑定。未启动 Unity、导入资源、执行编辑器脚本或运行任何直接测试。运行验证由人工进行，需确认右键进出与占用恢复、瞄准转身和六方向混合、上下看时持枪与左手 IK，以及遮挡时退出瞄准能恢复原探索镜头距离。

本次角色 Prefab 迁移仅做资源与场景序列化引用的静态核对；未运行 Unity 或项目代码。运行验证需人工确认移动和重力、窗口输入屏蔽、相机与交互绑定，以及斜坡、低台阶和接地表现。

此前 Rigidbody 迁移的 prefab YAML 与场景序列化引用曾做静态核对。使用项目现有 Unity 元数据引用和 C# 9 静态编译 RPG4.UI、RPG4.StateMachine、RPG4.Input、RPG4.PlayerMovement、RPG4.Interaction 五个程序集的 18 个源文件，全部通过；遵循现有 asmdef 引用，未运行 Unity、项目代码、分析器或生成器。未导入资源、进入 Play Mode、构建或运行直接测试。该记录属于此前迁移，不代表本次角色 Prefab 迁移的运行或编译结果。
