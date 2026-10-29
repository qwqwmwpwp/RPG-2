# Player 组件框架与调用时序

基于当前 `Assets/Scripts` 中的实现，只展示主要组件、职责和调用关系，省略状态切换条件、射击参数与骨骼求解细节。使用支持 Mermaid 的 Markdown 预览查看图表。

## 整体框架图

实线表示主要调用或执行关系，虚线表示数据读写、引用或状态同步。框内的普通 C# 对象用于组织逻辑，并不都是挂在 GameObject 上的组件。整体沿用分组流程图格式，按 1:1 比例调整布局。

```mermaid
%%{init: {'flowchart': {'nodeSpacing': 73.23863, 'rankSpacing': 50, 'diagramPadding': 16, 'useMaxWidth': true}}}%%
flowchart LR
    subgraph InputLayer[输入与共享数据]
        Input["PlayerInput<br/>采样输入、维护输入生命周期"]
        Board["PlayerBlackboard<br/>输入、许可、上身需求<br/>移动计划、表现状态"]
        Input -.->|输入处理器写入| Board
    end
    subgraph LogicLayer[角色逻辑]
        Player["Player<br/>初始化、调度双状态机<br/>停止与取消"]
        Intents["PlayerIntentManager<br/>组织输入 / 移动 / 表现意图处理器"]
        Upper["上身状态机<br/>UpperBodyRootState<br/>普通、瞄准、攻击、换弹"]
        Lower["下身状态机<br/>LowerBodyRootState<br/>站立 / 蹲伏、自由移动 / 横移"]
        Player --> Intents
        Player -->|先推进| Upper
        Player -->|解析移动计划后推进| Lower
        Upper -->|记录武器需求与动作| Intents
        Intents -.->|更新许可、计划和表现状态| Board
        Board -.->|读取输入与许可| Upper
        Board -.->|读取移动计划| Lower
    end
    subgraph EquipmentLayer[装备与武器]
        Inventory["BackpackInventory<br/>EquipmentInventory<br/>物品与装备槽"]
        Equipment["PlayerEquipment<br/>同步装备、提供 CurrentWeapon"]
        Weapon["Weapon（普通 C# 对象）<br/>射击、冷却、弹药与换弹"]
        Inventory -.->|装备变更| Equipment
        Equipment -.->|提供当前运行时实例| Weapon
        Upper -->|推进并使用当前武器| Weapon
    end
    subgraph PresentationLayer[视角、动画与物理表现]
        View["PlayerViewController<br/>逻辑观察角与视角节点"]
        Camera["PlayerCameraController<br/>相机距离、肩部偏移、FOV"]
        Cinemachine["Cinemachine<br/>相机跟随与避障"]
        Pose["PlayerAimPose + PlayerWeaponRig<br/>瞄准姿态、武器挂点与辅助手 IK"]
        Animation["PlayerWeaponPresentation + Animator<br/>上身动作投影与角色动画"]
        Physics["Rigidbody + CapsuleCollider<br/>位移、转向与站蹲碰撞体"]
        View -->|处理观察输入| Intents
        Board -.->|观察角| View
        Camera -->|处理缩放与相机目标| Intents
        Board -.->|相机状态| Camera
        Camera --> Cinemachine
        View -.->|跟随目标| Cinemachine
        Player -->|提交姿态| Pose
        Equipment -->|绑定武器模型| Pose
        Board -.->|姿态与握持状态| Pose
        Upper -->|同步上身表现| Animation
        Lower -->|更新下身动画| Animation
        Lower -->|物理步执行| Physics
    end
    Interaction["PlayerInteraction<br/>目标检测、交互与准心反馈"]
    Target["IInteractable<br/>场景交互目标"]
    Board -.->|交互输入与许可| Interaction
    Interaction --> Target
    Interaction -.->|通过 PlayerInput 回写目标| Board
```

核心关系：**上身产生需求 → 意图管理器合并输入与约束 → 下身执行移动计划**。共享黑板负责保存数据，由组件主动读写；`PlayerContext` 汇集黑板、配置和装备引用，`PlayerStateData` 保存参数与组件引用，二者不单独驱动更新。

## 主要调用流程图

下图以正常启用且已初始化的角色为例，按 `Update`、`LateUpdate` 和独立的 `FixedUpdate` 组织流程。箭头表示处理先后；各组件的生命周期入口由 Unity 分别调度，具体组件调用写在节点内。

```mermaid
flowchart TB
    subgraph Frame[每帧更新流程]
        direction TB
        U0["Update 开始"]
        U1["PlayerInput<br/>采样输入、写入黑板并刷新许可"]
        U2["PlayerViewController<br/>通过意图管理器更新观察角与视角节点"]
        U3["PlayerCameraController<br/>通过意图管理器处理滚轮缩放"]
        U4["PlayerEquipment<br/>同步当前装备与 Weapon"]
        U5["Player → 上身状态机 / Weapon<br/>刷新许可，推进武器与上身状态"]
        U6["上身状态机 → PlayerIntentManager<br/>写入上身需求、动作与表现状态"]
        U7["Player → PlayerIntentManager<br/>合并输入、上身需求与站蹲约束，生成移动计划"]
        U8["Player → 下身状态机<br/>读取移动计划，切换状态并更新下身动画"]
        U9["Player → PlayerAimPose / PlayerWeaponRig<br/>提交瞄准姿态与武器 Rig"]
        U10["Player → 上身状态机 → PlayerWeaponPresentation<br/>同步上身动画"]
        L0["LateUpdate 开始"]
        L1["PlayerInput / PlayerViewController<br/>刷新许可、修正视角节点"]
        L2["PlayerCameraController<br/>更新相机参数，交由 Cinemachine 跟随"]
        L3["PlayerEquipment / PlayerInteraction<br/>同步装备；检测并执行交互，回报目标与许可"]
        L4{"Player 检查<br/>是否需要取消武器动作？"}
        L5["Player → 上身状态机<br/>取消动作，同步双状态机与姿态表现"]
        Done["本帧流程结束"]
        U0 --> U1 --> U2 --> U3 --> U4 --> U5 --> U6 --> U7 --> U8 --> U9 --> U10
        U10 --> L0 --> L1 --> L2 --> L3 --> L4
        L4 -->|是| L5 --> Done
        L4 -->|否| Done
    end

    subgraph Physics[独立物理更新流程]
        direction TB
        F0["FixedUpdate 开始"]
        F1["Player → PlayerIntentManager<br/>刷新控制许可"]
        F2["Player → 下身状态机<br/>FixedTick 读取已有移动计划"]
        F3["下身状态机 → CapsuleCollider<br/>调整站蹲碰撞体，回写实际姿态"]
        F4["下身状态机 → Rigidbody<br/>执行位移与转向"]
        F5["本次物理步结束"]
        F0 --> F1 --> F2 --> F3 --> F4 --> F5
    end
```

- 核心顺序为：**输入 → 上身需求 → 移动计划 → 下身执行 → 动画与姿态表现**。上身通过 `PlayerContext.Weapon` 访问 `PlayerEquipment.CurrentWeapon`。
- `FixedUpdate` 与渲染帧独立，按 Unity 物理步调度；一次渲染帧可能对应零次或多次物理更新，两个流程之间没有固定的串行箭头。
- `PlayerEquipment` 与 `PlayerInteraction` 的执行顺序相同，图中合并展示，不保证两者先后。图中省略 Cinemachine 内部更新、重复许可检查及装备事件回调。
- 武器许可丢失、暂停或收到取消请求时进入取消分支；`Player` 禁用、失焦或暂停的生命周期清理不在图中展开。

## 对照源码

- [Player、意图管理器与双状态机](../Scripts/PlayerMovement/)
- [输入采样与输入处理器](../Scripts/Input/PlayerInput.cs)
- [共享黑板](../Scripts/PlayerIntent/PlayerBlackboard.cs)
- [交互组件](../Scripts/Interaction/PlayerInteraction.cs)
- [武器运行时逻辑](../backpack/Scripts/Equipment/Weapon.cs)
