# 第三人称肩后相机与角色移动

打开核心场景 `Assets/Scenes/Main.unity`。场景配置了 Player 预制体、InputManager、Cinemachine 相机、地面、遮挡墙、碰撞方块和低台阶，并整合背包窗口、箱子和普通交互物。

角色通过带动态 Rigidbody 的 CapsuleCollider 参与 Unity 物理。Player 的活动状态在 FixedUpdate 中驱动物理移动；Player.Update 负责状态转换与动画。刚体使用项目 Physics.gravity（当前为 -9.81 m/s²），插值开启并冻结 X/Z 旋转；移动更新水平速度时保留刚体当前垂直速度。

- **WASD**：相对观察水平角移动，人物平滑转向移动方向；松开后保留朝向，斜向移动不会加速。
- **鼠标移动**：自由环绕观察，原地转镜头不会改变人物朝向。
- **滚轮**：探索时平滑调整相机距离，当前相机 Prefab 范围 2～3 米；瞄准时忽略滚轮并保留探索距离。
- **按住鼠标右键**：进入瞄准，角色朝向观察水平角，WASD 相对该方向前后及侧向移动；未开火时松开回到探索，正在开火时转为腰射。瞄准移动速度为 2.5 米/秒，斜向不会加速。
- **鼠标左键**：不按右键也可腰射；短按仍播放一发完整的 R_Shoot。同一次按住手势的第一发使用点射动画，实际第二发起切入 R_AimIdle_AutoShoot 连射循环，后续逻辑发射不逐发重播循环。腰射保持观察 yaw 朝向，以 6 米/秒相对镜头移动；腰射指不开镜开火，复用现有持枪姿态。
- **右键 + 左键**：瞄准时射击。持续左键时按下或松开右键，保持点射或连射的当前播放进度和下一发时间，不因模式切换额外开火。松左留右在末发及淡出收尾后只瞄准；全部松开后同样保留末发并平滑退出。这里的 ADS 是肩后收近镜头，并非枪械瞄具视角。
- **瞄准镜头与姿态**：距离平滑切到 1.5 米、垂直 FOV 切到 45°、鼠标灵敏度乘 0.65；退出恢复探索的滚轮距离和原 FOV。单纯瞄准播放 AimLocomotion 动画，不叠加手 IK 或上身俯仰；实际单发动作及淡出收尾期间才启用这些叠加，枪械沿用原右手挂点。
- **Esc**：释放鼠标；左键单击重新捕获。切出应用时释放鼠标，禁用 PlayerInput 时恢复此前光标状态。

## 职责

- **摄像机**：PlayerCameraController 读取 PlayerInput，维护 yaw、pitch 和目标距离，并更新独立 CameraPivot。虚拟相机使用 3rd Person Follow 处理肩后构图、阻尼和避障，Brain 将状态应用到 Main Camera。
- **角色**：Player 是 MonoBehaviour 状态机宿主。PlayerInput 统一玩法输入；Player 的 stateData 持有内容引用、探索、瞄准及腰射配置。PlayerRootState 是探索与持枪状态组的共同根，只选择初始组并维护角色公共表现；ExplorationState 组织 IdleState/MoveState，AttackState 组织 AimState/HipFireState。活动状态在 FixedUpdate 更新刚体运动，Player.Update 进行状态切换和动画更新。
- **UI**：UIManager 管理窗口，InventoryInputScope 保存和恢复窗口期间的光标状态。窗口打开时及开关当帧，BlocksGameplayInput 屏蔽相机、角色和世界交互输入；物理重力和相机更新继续运行。HasOpenWindows 表示实际窗口状态。

InputManager 独占输入资产实例和映射生命周期；角色根节点上的 `RPG4.Input.PlayerInput` 统一维护 Move、Look、Zoom、AimHeld、FirePressed、FireHeld、InteractPressed 及玩法门控。FirePressed 保留本帧点按，即使同帧已松开；状态、相机、Player 宿主和世界交互读取该组件，不直接访问 InputActions。Look 使用鼠标 delta，Zoom 使用滚轮 Y 并按每刻度 120 单位换算。FixedUpdate 使用最新 Move 输入快照。

窗口占用、光标释放、失去焦点、暂停或组件禁用时取消瞄准和射击表现。恢复玩法后需要松开相应左右键再按下，避免占用期间按住的键直接重入。相机镜头恢复使用 unscaledDeltaTime，暂停时仍能恢复；禁用相机控制器会立即恢复探索距离与 FOV。

输入延迟、缓存和重复触发策略由 PlayerInput 负责，本轮没有新增这些机制。物理更新读取的是最新 Move 快照，不是排队的按键缓冲。

## 状态机与数据

Player 要求角色根节点存在 Rigidbody、CapsuleCollider 和自定义 PlayerInput。Player 的执行顺序为 150，晚于 PlayerInput(-200)、相机(-100)及背包开关输入(50)；Update 先调用 PlayerRootState.PrepareFrame 准备本帧开火动作与模式，再运行 HSM Tick 更新状态及动画。PrepareFrame 推进动作并解析模式；射击动画播放在 HSM Tick 中由 AttackState.OnUpdate 负责，Player 不再在状态机之后单独投影射击动画。LateUpdate 再检查交互窗口(100)造成的占用，只取消动作并以 Tick(0) 同步模式、镜头和姿态，不再次消费开火输入。即使暂停时 deltaTime 为零，仍执行清理与状态转换。StateMachine.FixedTick 只调用已活动状态链的物理更新，不启动或切换状态。

活动状态链固定为三层，持枪组表示瞄准或腰射，进入瞄准不要求正在开火：

```text
PlayerRootState
├── ExplorationState
│   ├── IdleState
│   └── MoveState
└── AttackState
    ├── AimState
    └── HipFireState
```

Root 的 GetInitialState 按当前 Mode 选择初始状态组，不在 GetTransition 中代替子状态切换。ExplorationState 在 Mode 不再是 Exploration 时通过强类型父状态的 root.Attack 返回兄弟组；AttackState 在 Mode 为 Exploration 时通过 root.Exploration 返回兄弟组。Idle/Move 通过 exploration.Move/Idle 返回兄弟叶，Aim/HipFire 按统一 Mode 通过 attack.HipFire/Aim 返回兄弟叶。初始选子状态不属于同级转换：Exploration 按当前移动输入直接选 Idle 或 Move，Attack 按当前 Mode 直接选 Aim 或 HipFire，因此组切换当 Tick 就进入正确的叶状态。

StateMachine.Tick 从根到叶检查转换，每 Tick 最多执行一次转换。每个非空目标先检查 machine/root 归属，再检查目标 Parent 与发起转换的状态 Parent 相同；即使目标已经是 ActiveLeaf 也不能绕过层级检查。返回自身不重入。退出旧链、进入新链和递归进入初始子状态都由通用 StateMachine 完成；Stop 仍从叶到根退出。

探索父状态与 Idle/Move 共享 `ExplorationState.Data`。AimState 与 HipFireState 都直接继承 State，叶状态进入时只配置自己的模式和步频；共用的观察方向运动、转向及腿部动画由 AttackState 实现，每个物理帧按当前模式读取对应移动速度。Attack 在叶互切期间持续激活，保留局部 animationMove，不重播腿部；转向平滑速度在叶进入时按原行为复位。配置仍保存在 `AimState.Data`、`HipFireState.Data` 及原有 stateData 字段路径中。

Player 的 `stateData.content` 保存 Animator、Rigidbody（字段名 `body`）和 PlayerCameraController 引用；相机引用由场景显式配置。`stateData.exploration` 保存移动速度、转向平滑时间、输入阈值、动画名称和过渡时间。缺少 Rigidbody、Animator 等角色自身引用时由 Player 初始化补齐。CameraTarget 是角色身上的位置锚点；角色不修改实际相机，也不读取最终镜头旋转。

PlayerContext 在 Player 初始化时为每个角色单独创建，统一传给各状态，保存输入、内容数据、配置及动作标记和模式；它不持有开火对象。PlayerRootState 提供 PrepareFrame 与 CancelWeaponAction，由 Player 生命周期调用。PrepareFrame 推进 AttackState 持有的 PlayerFireAction 并解析模式；射击动画播放由 HSM Tick 中的 AttackState.OnUpdate 负责。PlayerContext 仍通过 SetWeaponMode 更新镜头、俯仰和上半身射击层权重。Player 不直接持有 fireAction，也不单独管理射击动画投影。ResolveMode 按 Aim→HipFire→Exploration 优先级确定模式；根状态、状态组、叶状态和运动读取同一模式。HipFire 依据 fireHeld、shotActive 或 fadingOut 的逻辑动作存续，不读取 Animator 播放状态。CanControl 判断输入、刚体和观察相机，CanUseWeapon 额外要求光标锁定；二者均不依赖 Animator。输入来自 PlayerInput，观察角来自 CameraController.ViewYaw；移动方向由当前状态组的运动逻辑计算。

`stateData.attack` 保存射击片段与节拍/动画过渡配置；`stateData.aim` 保存瞄准移动、转向和动画配置，`stateData.hipFire.moveSpeed` 默认 6 米/秒。根状态按 Aim 模式切换镜头，按实际射击动作切换俯仰；射击中的 Hip/ADS 互切保持俯仰平滑状态，只有 ADS 收近镜头。`stateData.content.aimPose` 显式引用与 Animator 同物体的 `PlayerAimPose`。姿态读取逻辑 `ViewPitch`，只调整 Spine/Chest；默认范围 -30°～45°、平滑时间 0.1 秒，不改变角色根节点或碰撞体。PlayerAimPose 保留执行顺序 -50，在 Update 中先还原上一帧的俯仰；最终动画层唯一 IK Pass 只记录本帧已求值，第三方武器组件仍负责原有手 IK。实际单发动作及淡出收尾期间，仅当最终层本帧完成 IK 时，LateUpdate 才在动画与 IK 输出的姿态上依次直接旋转 Spine、Chest；两臂与挂在右手下的枪随共同父骨骼一起俯仰，避免手 IK 取样与新增俯仰处于不同姿态。Chest 缺失时由 Spine 承担完整角度，范围和平滑参数不变。动作收尾结束、取消或禁用时立即恢复叠加前的骨骼旋转；暂停、Animator 停止求值或本帧无最终层 IK 时不叠加，即使仍保持 Aim 模式也不叠加额外俯仰。此方案针对当前枪与双臂共享 Spine/Chest 父层级、枪的 ParentConstraint 手持源与实际父节点同属右手的配置，保持新增俯仰前后的握持相对关系，不修正既有握持点或基础 IK 偏移。

PlayerFireAction 的只读 `HasShotAction` 仅表示 shotActive 或 fadingOut；原有 `HasWeaponAction` 仍包含 fireHeld，继续用于模式存续，不能用来判断实际射击 IK。PlayerContext 的 `ShotPoseActive` 统一要求实际射击动作、非 Exploration 模式、CanUseWeapon 为真且本帧缩放时间大于零；根状态的上层/俯仰投影和 Player 的公开只读 `WeaponIKEnabled` 共用此条件，后者还要求角色启用。不直接使用左右键或 Animator 播放状态判断，单纯 Aim 和仅按住左键等待冷却均不启用 IK，末发与淡出收尾继续保持 IK。

第三方 Character_Weapon_Controller 缓存所属 Player，射击姿态未启用时清本组件所有配置目标的 IK 位置/旋转权重与 currentWeight，不再设置目标；即使零权重层不回调 IK，Update 也会清插值权重。targetWeight、动画事件与默认武器挂点保持原有语义，未由本项目 Player 管理的素材实例保留原行为。

`Base Layer.AimLocomotion` 使用 AimMoveX/AimMoveY 的 2D Simple Directional Blend Tree。参数取角色局部移动方向与输入长度，范围为 [-1, 1]；中心使用 R_AimIdle，六个方向使用 R_AimWalk_F/B/FL/FR/BL/BR，斜向节点为单位向量 (±0.70710677, ±0.70710677)。纯左/右输入由前后斜向步行动画混合；Hip/ADS 切换继承当前方向参数，避免归零或重播腿部状态。AimLocomotionSpeed 控制步频，ADS 为 1、腰射为 1.35。保留 Idle/Run，Root Motion 关闭，Base Layer 关闭 IK Pass，仅最终射击层开启，避免第三方手 IK 重复插值。

`PlayerFireAction` 是 AttackState 为每个角色持有的纯 C# 运行对象，不依赖 MonoBehaviour、Animator 或 PlayerInput。AttackState 传入本帧 FirePressed/FireHeld、缩放时间和 `AttackState.Data` 配置；动作持有 nextShotTime、末发时长与淡出收尾，返回本帧新发、开始淡出、单发进度和 IsAutomatic 表现标记。同次按住手势的第一发为点射，第二发及以后为连射；松开或新的 FirePressed 切断手势计数，快速重复点按不会累计成连射。冷却内点按不排队，每帧最多一发，卡顿后不补发；新按下仍保持到冷却结束时，实际第一发仍为点射。持续按住等待下一发时仍保持腰射模式。松开后 IsAutomatic 保留至当前末发与淡出结束，或被新的实际点射/取消取代，避免收尾突然跳回点射；腰射运动、姿态与 IK 仍按真实动作存续。

`AttackState.Data` 序列化在 Player 的 `stateData.attack` 中，保存 shotClip（R_Shoot）、autoShotClip（R_AimIdle_AutoShoot）、shotInterval=0.16 秒、shotAnimationDuration=0.24 秒、layerFadeInDuration=0.06 秒和 layerFadeOutDuration=0.12 秒；Inspector 参数配置位置是 Player → State Data → Attack。AttackState 持有 PlayerFireAction 并负责射击动画投影，统一使用 `stateData.content.animator`，不再需要独立射击组件。点射沿用 ShotSpeed=片段原长度/单发时长；连射首次进入时 CrossFade 一次，以素材本身速度 1 循环，不使用 ShotSpeed，也不假设一个循环对应一发。逻辑射速仍由 shotInterval 决定，连射每次 ShotStarted 不重播动画；释放后依原末发时长及淡出配置退出。若调大射击间隔使逻辑动作已进入淡出，下一真实发射会重新进入连射循环。

Hip/ADS 互切时，点射按逻辑 ShotPhase 延续；连射读取当前 auto 状态的 normalizedTime，在正过渡到 auto 时读取过渡目标的 normalizedTime，再切换对应 auto 状态以保留相位。这些 Animator 信息只用于动画连续性，不决定模式或发射。autoShotClip 未配置时回退原每发点射投影。Animator 或必需素材不可用时只清理投影，逻辑模式、IK 门控和节拍继续；恢复时先归 Empty，仅响应当帧新发，不补播缺失期间的射击。快速重新按下产生的真实点射会从 auto 切回正确的 HipFire/ADSFire。

Player Prefab 的 Attack 数据保存两类片段引用，Main 场景沿用该 Prefab：

| 用途 | 资源 | GUID / AnimationClip fileID |
| --- | --- | --- |
| 点射 | `RifleGirl/Animations/Aiming/R_Shoot.fbx` | `c64081e16b2eb784285608f8182fac46` / `-319965354747890277` |
| 连射 | `RifleGirl/Animations/Aiming/R_AimIdle_AutoShoot.fbx` | `350d849f350ac944f911df61ffa830c0` / `7853932406585560401` |

以上路径位于 `Assets/CombatGirlsCharacterPack`。连射导入配置保留现有 15–36 帧裁剪、Humanoid、首帧 SwitchSocket 事件和所有既有标识，本次只开启 Loop Time；未导入或运行检查实际循环接缝。

取消动作与重置冷却分开：自然结束、UI/光标/暂停/失焦中断、禁用和恢复都保留 nextShotTime，只有新建角色运行对象时初始化冷却。PlayerRootState.CancelWeaponAction 委托 AttackState 取消逻辑动作与表现，并清除左右键，要求恢复玩法后松开再按；不清移动、观察和交互快照，释放光标仍按原有 CanControl 规则探索移动。Player.Stop 的生命周期停机才同时清全部输入、退出 HSM。动画清理可重复调用，不通过回调递归停止宿主。

Animator 第 1 层必须命名 `UpperBody Shooting`，包含 Empty（默认、无 Motion）、HipFire、ADSFire、HipAutoFire、ADSAutoFire 五状态；HipFire/ADSFire 复用 R_Shoot 并启用 ShotSpeed，HipAutoFire/ADSAutoFire 复用循环 R_AimIdle_AutoShoot，以固定速度 1 播放。该层使用既有上半身 AvatarMask、Override、Write Defaults 关闭，默认权重为 0。PlayerContext 按统一 ShotPoseActive 设置层权重：实际射击动作及淡出收尾期间为 1，其余时间为 0。Exploration 播放 Idle/Run，单纯 Aim 播放 AimLocomotion，二者均无额外武器手 IK 或上半身俯仰叠加；瞄准镜头、移动及朝向仍按原 Aim 模式生效。初始化显式关闭射击姿态，每帧更新和清理路径重新设置权重；Animator 暂时禁用时也清理可访问的旧层权重，恢复后按当前动作重新投影。射击层仍通过 CrossFade 进入/退出 Empty；射击中的 Hip/ADS 互切不关闭层或取消当前动作。Base Layer 的 IK Pass 及 Idle/Run 的 Foot IK 保持关闭，仅射击层保留唯一 IK Pass，上层片段不覆盖腿部。

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

禁用 Player 时由 Player.Stop 取消武器动作、退出活动状态并清理输入快照；PlayerInput 仍启用时继续维护相机和交互输入。重新启用 Player 后，状态机在下一次有效 Update 自动启动并按输入选择状态。PlayerMovement 与 PlayerMotor 已不再是角色移动组件。

本模块供本机玩家使用。本次射击只模拟上半身动画，不包含子弹、射线命中、伤害、音效、粒子、镜头震动或网络；没有弹道，因此未实现精度参数。禁用 Player 会立即取消射击、重置模式并清除输入；重新启用后需重新按键。射击配置和动画投影属于 AttackState，不存在独立射击 MonoBehaviour。

## 验证记录

本次射击职责迁移（2026-10-25）：射击配置从 PlayerShootSimulation 移入 `stateData.attack`，PlayerFireAction 与射击动画处理由 AttackState 持有；PlayerRootState 提供 PrepareFrame/CancelWeaponAction 供 Player 生命周期调用。PrepareFrame 推进动作并解析模式，射击动画播放发生在 HSM Tick 的 AttackState.OnUpdate；PlayerContext 不持有开火对象，但仍通过 SetWeaponMode 更新镜头、俯仰与上半身射击层权重。AimState 与 HipFireState 作为 AttackState 的兄弟叶状态共享动作、冷却和动画相位。旧组件及其 Prefab 组件已移除。使用 Roslyn 静态编译 UI（2 个源文件）、StateMachine（2 个）、Input（3 个）、PlayerMovement（13 个），共 20 个 C# 源文件；4 个程序集 exitCode 均为 0，日志无警告或错误。报告：`.codex-task-snapshot/shoot-simulation/static-check/run-20261025-015259/compile-report.json`。序列化差异检查确认两项动画引用及四个参数精确保留，Prefab 只有两处已有文档字段改动并删除旧组件，其他文档、Main 场景、Controller、第三方脚本不变，且没有缺失的本地 fileID；报告：`.codex-task-snapshot/shooting-in-state-20261025/assets-report.json`。未运行 Unity、导入、构建或直接测试；以下更早验证记录不代表本次迁移结果。

本次上下调角度时左手握持修正仅改动 PlayerAimPose 与本 README。已静态核对 Update 先还原、最终层同帧 IK 标记、LateUpdate 逐骨骼叠加及退出/禁用还原；保留全部序列化字段、默认范围与平滑参数。相对本轮基线，第三方武器脚本、Player Prefab、Controller、Main 场景及点射/连射 FBX meta 的 SHA256 均未变化，射击节拍、状态与动画事件未改。两份改动文件的 git diff --check 通过；全仓检查报告的 Player Prefab 四处尾随空白在本轮基线中已存在，未改动。

使用 Unity 2022.3.62f3 引用元数据与 Roslyn/csc 静态编译 RPG4.UI（2 个源文件）、RPG4.StateMachine（2 个）、RPG4.Input（3 个）、RPG4.PlayerMovement（14 个），共 21 个 C# 源文件；4 项 exitCode 均为 0，日志无警告或错误。本轮报告为 `.codex-task-snapshot/shoot-simulation/static-check/run-20261024-195810/compile-report.json`。未启动 Unity、导入、构建、执行项目逻辑、分析器、生成器或直接测试。修正保证新增俯仰不会改变现有握持相对关系，不代表基础 IK 偏移或实际运行表现已验证；仍需人工确认点射及持续连射上下调角度、退出/纯瞄准恢复，以及暂停、UI 和禁用时无残留。以下均为此前工作的验证历史，不代表本次修正的运行结果。

本次连射动画整合已完成静态阅读、动作/投影分支与引用审阅，以及 Controller、Prefab 和指定 FBX meta 的差异核对；git diff --check 通过，未跟踪的 PlayerFireAction 源文件也无尾随空白。新增同手势第二发起的 IsAutomatic 标记与连射投影，Prefab 仅新增 autoShotClip 引用，原点射和时长配置保持原样；Controller 的 12 个文档无重复 ID 或缺失本地状态引用，射击层五个子状态及 Motion 绑定匹配，两类 auto 状态均速度 1 且不启用 ShotSpeed，默认层权重仍为 0。

独立资源核对还将当前连射 meta 的 loopTime 改回 0 后计算原始字节 SHA256，与本轮开始基线完全一致，证实本轮只开启 Loop Time，用户已有的 15–36 帧裁剪、事件、GUID、internalID 及其他修改完整保留。Main 场景、FBX 二进制与原点射 meta 未改。资源报告为 `.codex-task-snapshot/shoot-simulation/static-check/run-20261024-192556/auto-shoot-assets-report.json`。

使用 Unity 引用元数据与 Roslyn/csc 静态编译 RPG4.UI（2 个源文件）、RPG4.StateMachine（2 个）、RPG4.Input（3 个）、RPG4.PlayerMovement（14 个），共 21 个 C# 源文件，4 项 exitCode 均为 0，日志无警告或错误；本轮报告为同目录 `compile-report.json`。未启动 Unity、导入、构建、执行项目逻辑、分析器、生成器或任何直接测试，未观察新片段实际播放。实际循环接缝与首帧事件、短按完整点射、长按第二发起循环、快速点按不累计连射、跨 Hip/ADS 及过渡中的相位、松键收尾、素材缺失与恢复仍需人工运行确认。以下均为此前工作的验证历史，不代表本次连射整合的验证结果。

此前射击动作 IK 门控修正已静态核对实际单发/淡出条件、纯瞄准与冷却等待的区别、根状态和公开 IK 接口共用条件，以及既有中断清理顺序；git diff --check 通过。与该轮开始基线比较，HasWeaponAction 与 FireAction 的 Tick/Cancel/冷却逻辑保持原样，只新增实际动作只读查询；未更改状态转换、瞄准镜头、移动朝向、射击动画投影、第三方武器脚本、Controller、场景、prefab、meta 或配置字段。

使用 Unity 引用元数据与 Roslyn/csc 静态编译 RPG4.UI（2 个源文件）、RPG4.StateMachine（2 个）、RPG4.Input（3 个）、RPG4.PlayerMovement（14 个），共 21 个 C# 源文件，4 项 exitCode 均为 0，日志无警告或错误。本轮报告为 `.codex-task-snapshot/shoot-simulation/static-check/run-20261024-184845/compile-report.json`。未启动 Unity、导入、构建、执行项目逻辑、分析器、生成器或任何直接测试。纯 Aim 无额外手 IK/俯仰、射击及收尾恢复叠加、仅按住等待冷却和 UI/暂停/失焦等中断的实际表现仍需人工确认。以下均为此前工作的验证历史，其结果不代表本次修正。

此前非攻击 IK 清理已静态核对 Player 模式门控、初始化/中断投影、第三方 IK 与挂点/动画事件职责边界，以及 Controller 的层权重差异；git diff --check 通过。Controller 仅将 UpperBody Shooting 的默认权重由 1 改为 0，保留 Base Layer、IK Pass 和 Foot IK 配置。与该轮开始基线比较，仅改动 Player、PlayerContext、Character_Weapon_Controller、player.controller 和本 README，HSM、开火动作、射击投影、配置字段、场景、prefab 和 meta 保持原样。

使用 Unity 引用元数据直接运行 Roslyn/csc 静态编译 RPG4.UI（2 个源文件）、RPG4.StateMachine（2 个）、RPG4.Input（3 个）、RPG4.PlayerMovement（14 个），共 21 个 C# 源文件，4 项 exitCode 均为 0，日志无警告或错误；报告为 `.codex-task-snapshot/shoot-simulation/static-check/run-20261024-183817/compile-report.json`。修改后的 Character_Weapon_Controller.cs 还分别完成运行时与 UNITY_EDITOR 两个编译分支的单文件静态编译，两项 exitCode 均为 0，日志无警告或错误；报告为同目录 `weapon-ik-compile-report.json`，覆盖的是实际武器脚本的两个分支，并非整个 Assembly-CSharp。

该次未启动 Unity、导入、构建、执行项目逻辑、分析器、生成器或任何直接测试。该版运行时待人工确认的项目为探索只播放基础动画、Aim/HipFire 与腰射收尾可恢复 IK、退出与中断无残留，以及 Animator 禁用和恢复后的层权重；其中纯 Aim 启用 IK 的规则已被本次修正取代。

此前 HSM 层级重构已完成静态阅读、差异审阅、同级转移与初始链核对、序列化字段及新脚本 meta 检查，git diff --check 通过。Aim 的 6 个、HipFire 的 1 个和 Exploration 的 6 个 SerializeField 声明及默认值保持原样；Player、Context、StateData、FireAction、ShootSimulation、Input 和 Main 场景相对该次任务开始基线未改动。新 Attack 脚本的 MonoImporter meta 完整，GUID 在 Assets 的 meta 中唯一。

该次使用 Unity 2022.3.62f3 引用元数据与 Roslyn 静态编译 RPG4.UI（2 个源文件）、RPG4.StateMachine（2 个）、RPG4.Input（3 个）、RPG4.PlayerMovement（14 个），共 21 个 C# 源文件，4 项 exitCode 均为 0，四份编译日志无警告或错误。该次报告相对路径为 `.codex-task-snapshot/shoot-simulation/static-check/run-20261024-183025/compile-report.json`。仅执行静态编译，未启动 Unity、导入、构建、执行项目逻辑、分析器、生成器或直接测试。运行验证由人工确认：探索与持枪组当帧进入正确叶状态、Hip/ADS 互切时的腿部连续性与转向、速度和步频切换，以及 UI/暂停/失焦/禁用清理。

此前状态职责重构已完成代码差异、符号引用、序列化字段与资源边界静态核对，git diff --check 通过。使用 Unity 2022.3.62f3 的引用元数据与 Roslyn 静态编译 RPG4.UI（2 个源文件）、RPG4.StateMachine（2 个）、RPG4.Input（3 个）、RPG4.PlayerMovement（13 个），共 20 个 C# 源文件，4 项 exitCode 均为 0，编译日志无警告或错误。报告相对路径为 `.codex-task-snapshot/shoot-simulation/static-check/run-20261024-162559/compile-report.json`。仅执行静态编译，未启动 Unity、导入、构建、执行项目逻辑、分析器、生成器或直接测试。运行验证由人工确认：短点击与持续节拍、Hip/ADS 切换的腿部连续性与单发进度、末发及淡出期间的腰射运动、Animator 暂时不可用与恢复、UI 在 LateUpdate 打开后的模式和姿态同步，以及中断恢复不能绕过冷却或沿用按住的左右键。

本次腰射/ADS 射击实现仅做静态代码和序列化资源核对，未启动 Unity、导入、构建、执行项目代码或直接测试。人工运行时需确认：短点击完整单发、长按节拍、持续左键切右键不额外开火、松左留右仅瞄准、全松平滑收尾、腰射六方向移动及俯仰握枪、UI/暂停/失焦/光标释放和禁用组件时停止及恢复后重新按键。Empty 无 Motion 的混合效果与最终单次手 IK 仍需在角色动画实际运行中确认。

本次瞄准整合仅做代码与资源静态核对：检查输入/状态/镜头接口、七个动画的 meta GUID 与 AnimationClip fileID、Blend Tree 参数和单位方向、PlayerAimPose 与状态数据引用、Root Motion 和 IK Pass，以及 Main 场景继承的相机绑定。未启动 Unity、导入资源、执行编辑器脚本或运行任何直接测试。运行验证由人工进行，需确认右键进出与占用恢复、瞄准转身和六方向混合、上下看时持枪与左手 IK，以及遮挡时退出瞄准能恢复原探索镜头距离。

本次角色 Prefab 迁移仅做资源与场景序列化引用的静态核对；未运行 Unity 或项目代码。运行验证需人工确认移动和重力、窗口输入屏蔽、相机与交互绑定，以及斜坡、低台阶和接地表现。

此前 Rigidbody 迁移的 prefab YAML 与场景序列化引用曾做静态核对。使用项目现有 Unity 元数据引用和 C# 9 静态编译 RPG4.UI、RPG4.StateMachine、RPG4.Input、RPG4.PlayerMovement、RPG4.Interaction 五个程序集的 18 个源文件，全部通过；遵循现有 asmdef 引用，未运行 Unity、项目代码、分析器或生成器。未导入资源、进入 Play Mode、构建或运行直接测试。该记录属于此前迁移，不代表本次角色 Prefab 迁移的运行或编译结果。
