# Player 组件架构与调用时序

更新日期：2026-11-01。以当前工作区实现为准；应用入口与会话切换见[游戏运行流程图](游戏运行流程图.md)，网络部署与库存同步见[网络权威处理与部署方向](网络权威处理与部署方向.md)。

本文描述角色生成后的组件调度。当前客户端从 `ClientBootstrap → MainMenu` 登录独立服务器，再进入 `OnlineGame`；服务器从 `ServerBootstrap` 启动并加载 `OnlineGame`。下文的离线与 Host 分支属于保留的兼容能力，不能据此将旧 `Main` 场景视为当前产品入口。

## 组件总览

`Player` 负责装配与调度，`PlayerContext` 持有每个角色唯一的工作黑板。

```mermaid
flowchart TB
    Player["Player.Awake"] --> Context["PlayerContext<br/>配置与运行时引用"]
    Context --> Board["PlayerBlackboard<br/>工作状态"]
    Player --> Intent["PlayerIntentManager<br/>输入、业务许可、发布"]
    Player --> States["上下身状态机<br/>行为决策"]
    Player --> Presenter["PlayerPresenter<br/>表现桥接"]
    Intent -.->|输入与业务许可| States
    States --> Execute["PlayerWorldAdapter / PlayerEquipment<br/>移动、武器与库存执行"]
    Board -.->|发布快照| Presenter
```

角色依赖刚体、胶囊体、`PlayerInput` 与 `CharacterAttributes`。模型、相机和动画不决定移动、扣弹或伤害是否成立。

## 输入与固定步

下图表示存活角色一次 `SimulateTick` 的主流程。

```mermaid
flowchart TB
    Local["Local：PlayerInput<br/>采样并累计设备输入"] --> Begin
    External["External：SubmitExternalIntent<br/>接收外部输入"] --> Begin
    Begin["BeginFrame / RefreshPermissions<br/>消费输入、刷新业务许可"] --> Prepare["ProcessLook / PrepareFrame<br/>观察角、武器与动作准备"]
    Prepare --> Upper["上身 Tick<br/>Normal / Aim / Attack / Reload"]
    Upper --> Plan["ResolveLocomotion<br/>合并输入与上身约束"]
    Plan --> Lower["下身 Tick / FixedTick<br/>站蹲、Free / Strafe、运动执行"]
    Lower --> Publish["finally：Publish → CompleteFrame<br/>发布结果、清理单次输入"]
```

- 离线由 `Player.FixedUpdate` 驱动；Server / Host 由 `SimulateNetworkTick` 驱动。死亡分支跳过状态机，仍发布状态。
- 窗口与焦点控制本地输入；存活、刚体和武器事实决定业务许可。单次按下和观察增量只消费一次，切换来源会清空旧输入。
- 移动计划与实际姿态分开：站起仍须通过碰撞检查。离线使用动态刚体，网络使用运动学胶囊移动；不使用动画根运动。

## 射击与换弹

```mermaid
flowchart TB
    Action["上身武器行为"] -->|射击| Fire["PlayerEquipment → Weapon.TryFire<br/>校验射速与弹药、扣弹"]
    Fire --> Queue["已接受射击入队<br/>固定方向与参数、应用后坐力"]
    Queue --> Flush["FlushPendingShots<br/>离线立即；网络在全部角色移动后"]
    Flush --> Hit["WeaponShotExecutor<br/>查询命中、提交伤害"]
    Hit --> Feedback["Shot 事件 / ShotResolved<br/>交给本地表现与网络反馈"]
    Action -->|换弹| Reload["ReloadState 发起"]
    Reload --> Commit["Weapon.BeginFrame<br/>按进度交换弹匣、上膛"]
```

射击使用逻辑观察角和身体锚点，不读取模型枪口。已接受的射击不会因同 Tick 随后死亡而撤销；换弹取消不回滚已提交的库存变化。

## 数据与表现

```mermaid
flowchart LR
    Board["PlayerBlackboard"] --> Read["IPlayerReadState<br/>读取实时工作值"]
    Read --> Logic["状态机 / 装备 / 交互"]
    Board -->|Publish| Frame["PlayerPublishedFrame<br/>不可变状态与有序事件"]
    Frame --> Presenter["PlayerPresenter"]
    Presenter --> State["持续状态<br/>动画、姿态、相机、生命与死亡 HUD"]
    Presenter --> Event["一次性事件<br/>枪声、火光、准心与交互反馈"]
```

表现关闭后逻辑仍可推进；重新启用时恢复最新状态，不补播历史事件。当前仍在 Unity 主线程运行，发布快照不代表支持并发写入。

库存窗口与武器面板使用独立通道：

| 显示内容 | 数据来源 |
|---|---|
| 背包、物品详情、角色概览 | 库存 / 装备 / 属性视图。Host 私人库存读取权威数据，共享箱子仍须等获权镜像就绪。 |
| 离线 / Host 武器 UI | `PlayerEquipment.WeaponUi` 发布的武器快照。 |
| 纯客户端武器 UI | `BackpackNetworkInventory.WeaponUi` 根据网络镜像发布的武器快照。 |

## 网络角色分工

当前在线入口由服务器在认证及世界就绪校验通过后，按受信任生成配置初始化角色，再发布到连接。客户端还须等待场景、所属角色和 UI 绑定就绪；`ClientSessionController` 进入 `Playing` 后才放行本地游戏输入。场景切换、就绪丢失或断线时会关闭输入。

```mermaid
flowchart TB
    Role{"角色实例"} -->|Server / Host| Authority["SimulateNetworkTick<br/>完整角色业务"]
    Role -->|纯客户端所属玩家| Predict["PredictNetworkMovement<br/>仅预测移动"]
    Predict -->|收到服务器快照| Correct["应用权威状态与运动位置<br/>删除已确认输入"]
    Correct -->|重放剩余移动输入| Predict
    Role -->|纯客户端远端角色| Remote["缓存快照并插值<br/>位置、朝向与观察角"]
    Authority --> Frame["发布角色状态"]
    Predict --> Frame
    Remote --> Frame
    Frame --> View["客户端 Presenter"]
```

- Host 本地输入直接进入服务器缓冲，只执行一次权威业务；独立服务器关闭表现。纯客户端不重放扣弹、伤害或库存事务。
- 网络状态投影使用 `ApplyNetworkPresentation`，射击反馈使用 `ApplyNetworkShot`；两者都不重新执行业务。生命快照可恢复生死与受伤状态，但完整受击方向事件尚未复制。
- 死亡会停止角色行为并更新 HUD，复活会清空旧输入。网络会话中的死亡窗口不提供本地场景重启。

## 源码入口

| 关注点 | 入口 |
|---|---|
| 装配与黑板 | [Player](../Scripts/PlayerMovement/Player.cs)、[PlayerContext](../Scripts/PlayerMovement/PlayerContext.cs)、[PlayerBlackboard](../Scripts/PlayerIntent/PlayerBlackboard.cs) |
| 输入与上下身 | [PlayerIntentManager](../Scripts/PlayerMovement/PlayerIntentManager.cs)、[上身状态](../Scripts/PlayerMovement/States/UpperBodyRootState.cs)、[下身状态](../Scripts/PlayerMovement/States/LowerBodyRootState.cs) |
| 移动与战斗 | [PlayerWorldAdapter](../Scripts/PlayerMovement/PlayerWorldAdapter.cs)、[PlayerEquipment](../Scripts/PlayerMovement/PlayerEquipment.cs)、[Weapon](../backpack/Scripts/Equipment/Weapon.cs) |
| 发布与表现 | [PlayerPublishedFrame](../Scripts/PlayerIntent/PlayerPublishedFrame.cs)、[PlayerPresenter](../Scripts/PlayerMovement/PlayerPresenter.cs) |
| 网络角色 | [NetworkPlayerController](../Scripts/Networking/NetworkPlayerController.cs)、[NetworkPlayerSimulation](../Scripts/Networking/NetworkPlayerSimulation.cs) |
| 在线生成与输入放行 | [AuthoritativeNetworkManager](../Scripts/Networking/AuthoritativeNetworkManager.cs)、[ClientSessionController](../Scripts/Frontend/ClientSessionController.cs) |

本文用于说明当前静态调用关系；多客户端同步与交互效果仍需运行验证。
