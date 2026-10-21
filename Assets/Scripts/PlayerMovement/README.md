# 基础第三人称移动与相机

打开 `Assets/Scenes/PlayerMovement.unity`。场景已配置现有 Player 预制体、InputManager、Cinemachine 相机预制体、地面、遮挡墙、碰撞方块和低台阶。

现有工作场景 `Assets/backpack/Scenes/New Scene.unity` 也已接入同一套移动与相机：复用原有 Player 和 InputManager，替换固定相机实例，显式绑定 CameraTarget、移动参考相机和交互相机，并把原有地面扩大到约 30×30 米。原有 UI、物品与背包组件配置保持不变。

- **WASD**：相对实际相机的水平朝向移动，人物平滑转向移动方向；松开后保留朝向，斜向移动不会加速。
- **鼠标移动**：自由环绕观察，原地转镜头不会改变人物朝向。
- **滚轮**：调整轨道尺寸，中层轨道半径默认 4 米，范围 2～6 米。
- **Esc**：释放鼠标；左键单击重新捕获。切出应用时释放鼠标，禁用相机控制组件时恢复此前光标状态。

## 已配置的资源

`Assets/player/player.prefab` 使用 CharacterController，胶囊高 2 米、半径 0.3 米，台阶高度 0.3 米，坡度上限 45°。PlayerMovement 默认速度 4 米/秒、转向平滑时间 0.12 秒，负责重力与移动，并以实际水平位移更新 Animator 的 `speed` 参数。Animator 已关闭 Root Motion，更新模式为 Normal。

`Assets/player/PlayerCamera.prefab` 包含 Main Camera、Cinemachine Brain、FreeLook 三层轨道、Cinemachine Collider 和 PlayerCameraController。FreeLook 使用 World Space，关闭自动回正；轨道位置阻尼为 0.2。鼠标轴采用 Input Value Gain，灵敏度在 FreeLook 的 X/Y Axis Max Speed 中调整。上下观察范围由三层轨道配置决定。

Collider 检测 Default 层，忽略 Player 标签，使用 Pull Camera Forward。相机碰撞半径 0.2 米，遮挡时立即拉近，恢复阻尼为 0.35。需要避障的物体必须配置碰撞体，且位于检测层中。

InputManager 独占输入资产实例，同时管理 player 映射的启用与释放。移动和相机读取同一实例；look 绑定鼠标 delta，zoom 绑定滚轮 Y，并按 Windows 鼠标常见的每刻度 120 单位换算。项目自带的输入资产和生成类均已同步更新。

## 在其他场景使用

放入 Player、PlayerCamera 和 InputManager；将 FreeLook 的 Follow、Look At 都设为 Player 子节点 CameraTarget。PlayerMovement 的 Camera Transform 引用实际 Main Camera，留空时会查找带 MainCamera 标签的相机。相机预制体作为独立根节点放置。

本模块供本机玩家使用。本次没有添加联机移动、跳跃、冲刺或背包窗口处理。

## 验证记录

已完成独立 C# 静态编译，以及输入资产、生成类、GUID、场景与预制体引用核对。未启动 Unity、进入 Play Mode 或运行直接测试。

人工运行场景后重点确认：相机旋转后的 WASD 方向与转身、斜向速度和碰墙停止、地面与低台阶移动、idle/run 切换、遮挡墙前相机拉近和恢复、滚轮缩放及光标释放/重新捕获。
