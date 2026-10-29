# 独立只读复审

审查基线为本任务 `baseline/Assets`，不是 Git HEAD。未修改业务文件，未启动 Unity、运行场景、构建或直接测试。纯 Roslyn 编译由主代理另行执行；本报告不冒用旧编译结果。

## 已核对

- `InputIntentProcessor.Blackboard` 是唯一 `new PlayerBlackboard()`；`Player.Awake` 是唯一 `new PlayerIntentManager(...)`。管理器持有三个处理器，Blackboard/Context 没有自采样、更新函数或 getter 门控副作用。
- 使用结构检索核对所有 Blackboard/blackboard 属性赋值：实际写入集中于 InputIntentProcessor、LocomotionIntentProcessor、PresentationIntentProcessor。公开 getter + internal setter，并以程序集友元开放 Input/Movement；消费者没有直接赋值。
- Input/Interaction/Movement 的 asmdef 实际引用共享 PlayerIntent，没有循环依赖。既有序列化字段保留；没有为了迁移增加场景组件。
- 输入 -200 采样 → 单角色回调刷新许可 → View -100 读取/翻译 Look → Player 150 上身、运动合并、下身及表现；相机的黑板绑定延至首次 Update/Late，避开 Camera.Awake50 早于 Player.Awake150。
- Input.RefreshPermissions 清理失许可快照，ExpireFrameInput 只清当帧脉冲，连续 Move/Crouch/held 保留最近采样。清理后恢复许可不恢复已丢弃的脉冲；光标捕获点击消耗 Fire，Aim/Fire 重按武装逻辑及首帧 Look/Zoom 丢弃保留。
- 预 IK 显式刷新许可，活动武器需求失许可会锁存 WeaponCancellationRequested；PrepareFrame/PlayerLate 消费取消，Root.Cancel 末尾确认。刷新链不 Tick HSM，没有回调递归。Interaction 在检测/调用前后刷新。
- 普通换弹保留输入意图和完成帧 FireRearmFrame；FireAction 与 baseline 完全相同，Cancel 保留 nextShotTime，取消/停机不重建动作对象。
- 下身仍在 Fixed 执行胶囊、平面速度和转向；站起前再次检测，实际蹲伏反馈经 Locomotion processor 修正同一计划并限速。Stop 保留胶囊、清水平速度、恢复临时角速度上限的既有路径保留。
- Player.OnDestroy 解除单角色输入回调；独立禁用通过 Stop/输入 Clear 同步失许可，不新建动作对象。View/AimPose 保留恢复姿态与本帧最终 IK 栅栏。

## 架构边界修正复核结果

主代理首轮指出的 PresentationIntentProcessor 夹带执行器问题已修正，独立复核未发现待修复的阻塞问题：

- `PlayerWeaponPresentation.cs:7` 的 PresentationIntentProcessor 仅做数值翻译、快照写入和取消锁存，不调用 Animator、View 或 AimPose。原 PlayerWeaponPresentation 保留为同文件执行器，由 UpperBodyRootState 持有，执行器不写黑板。
- `Player.cs:55` 的常规流程在上下身更新后先 ApplyIntentPresentation，再 SyncPresentation；`PlayerWeaponPresentation.cs:106` 的 Sync 先执行 LayerWeight，再 Fire/Reload，保留投影顺序。
- `Player.cs:69` 的硬取消先取消上身并同步黑板，紧接着 ApplyIntentPresentation，在检查停机/禁用早退前撤销 View/AimPose 姿态；活动链 0f 同步后再提交表现，不重复准备或消费按键。
- `Player.cs:83` 的 Stop 停止状态机、清理输入后仍调用 ApplyIntentPresentation 和 ApplyLayerWeight；上身取消也在确认取消后按黑板提交层权重。
- View.LateUpdate 和 Camera.Update/LateUpdate 各自明确刷新许可；预 IK 刷新仍只改快照，手部执行器据 WeaponIKEnabled 清零，AimPose.ApplyPose 据黑板复位。Interaction 之后的最终即时骨架撤销由同帧 PlayerLate150 完成。

结论限定为对当前文件的静态审阅；没有把未运行的流程当作实测通过。

最终小修静态确认：`PlayerViewController.cs:42` 在 Awake 计算初始观察角后通过 `IntentManager?.RecordView` 回报，Initialize 中仍保留回报，兼容两种组件初始化先后；`PlayerIntentManager.cs:34` 在读取 owner.isActiveAndEnabled 前使用 Unity `owner != null` 判定，销毁后的刷新将控制许可置为关闭。未扩大业务审查或改动已有 Scene/Animator 空白。

审计脚本的 EditorSymbols 元数据路径已按本机安装修正：实际 UnityEditor 模块位于 `Managed/UnityEngine/UnityEditor*.dll`，没有 `Managed/UnityEditor` 目录。只修正独占脚本的引用枚举，纯 PowerShell 解析检查通过；编译仍由主代理执行。

## 已知运行验证边界

Interaction Late100 与 Brain Late100 同优先级；Camera Late50 可能已完成本帧。保留现有顺序，不以静态检查宣称 UI 晚开时相机会当帧重算。实际运行行为、场景绑定和外部包重新编译均不由本次只读审查证明。
