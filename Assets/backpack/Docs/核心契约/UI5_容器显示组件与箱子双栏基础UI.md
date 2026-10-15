# UI5 · 容器显示组件与箱子双栏基础 UI

基础显示落盘日期：2026-10-11；整件空位转移接入日期：2026-10-12。复用已有物品预制体与本地网格排布。

## 1. 组件职责

| 组件 | 职责 |
| --- | --- |
| `BackpackContainerUI` | 显式绑定一个 `IBackpackView`，生成对应格子，显示图标、数量与多格占位；监听该来源的 `Inv.Changed` 并在帧末刷新 |
| `BackpackGridUI` | 原玩家背包窗口兼容壳，保留原 Inspector 配置、来源解析、输入开关及交互控制器接缝；显示能力委托给 `BackpackContainerUI` |
| `ChestBackpackUI` | 组合左右显示组件，管理来源、布局、输入焦点、玩家输入与光标快照，转发点击给一份交互会话 |
| `BackpackInteractionController` | 保留原单栏行为；双栏模式固定携带来源、动态命中目标栏，处理整件空位转移与取消 |
| `BackpackItemUI` | 沿用已有物品框预制体，负责尺寸、图标、数量和外观 |

双栏第一阶段支持左键拿起整件／整堆、点击另一栏空位放下。接收位置被任何物品占用或任一容器未就绪时禁止转移；同类堆叠合并、拆分、不同物品互换不在双栏模式中执行。原单栏窗口保留已有交互行为。

## 2. 容器显示组件契约

```csharp
view.BindInventory(backpackInventory);
view.Redraw();
view.UnbindInventory();
```

- `BindInventory(IBackpackView source)` 支持玩家、箱子及已有网络库存视图。来源变化时清理旧物品、本地位置、显示覆盖与事件订阅；同源且事件键和尺寸没有变化时不重复重绘。
- `Container` 返回来源对应的实际 `InventoryContainer`；纯网络客户端没有权威容器，返回 null，但仍通过 `IBackpackView.CopyItems` 显示数据。
- 网格尺寸优先使用实际容器的 `Columns` / `Rows`；纯客户端使用来源配置尺寸。未绑定时先按 `initialColumns` / `initialRows` 生成占位格。
- `Inventory`、`Columns` / `Rows`、`GridColumns` / `GridRows`、`ItemViews`、`IsReady` 提供只读查询；`EnsureInitialized()` 可用于宿主在绑定前确保占位网格存在。
- 禁用显示组件时释放事件订阅但保留来源；再次启用重新订阅并读取。来源晚初始化、事件键变化或来源销毁时会更新绑定。容器变化只置脏并在帧末重绘，单独一栏变化不会重新绑定另一栏。

显示配置沿用 `slotPrefab`、`itemPrefab`、`cellSize`、`slotSpacing`、`slotLayer`、`itemLayer`、`uiFont`，可在 Inspector 中独立配置。原几何换算、本地排布、悬停和携带预览接口保留，供原玩家交互控制器继续使用。

## 3. 箱子窗口契约

```csharp
// 玩家来源自动解析：本地网络玩家优先，单机默认玩家兜底。
chestWindow.Open(chest);

// 显式指定玩家背包，适合单机角色或指定角色。
chestWindow.Open(playerBackpack, chest);

chestWindow.Close();
```

`playerView` 与 `chestView` 必须是两个不同的显示组件，放在窗口根的直接子节点。箱子来源只由 `defaultChest` 或 `Open` 参数提供，不会被自动换回玩家背包。左右栏允许不同尺寸，窗口按两栏实际像素大小重新排布。

`openOnStart` 用于基础展示：开启后 Start 显示窗口，左栏等待玩家就绪，右栏读取 Inspector 配置的 `defaultChest`。未指定箱子时右栏显示占位格。`Close()` 使用 CanvasGroup 隐藏窗口并解绑两栏；宿主仍可接受后续 `Open` 调用。

## 4. 预制体使用

预制体：`Assets/backpack/Prefabs/UI_ChestBackpack.prefab`。已经配置窗口背景、两个容器显示组件及已有格子 / 物品模板；两栏初始为 8 列 × 6 行，单格 64 像素、间距 8 像素。

预制体沿用项目现有的 LiberationSans SDF 字体，其主资产及回退字体不包含中文标题字形，因此默认标题为 `Player Backpack` / `Chest Backpack`。标题与等待提示可在 `ChestBackpackUI` Inspector 中修改；需要中文时同时配置包含对应中文字形的 `Ui Font`。

1. 把 `UI_ChestBackpack.prefab` 拖到场景 Canvas 下。
2. 在一个场景物体上添加 `ChestInventory`，Unity 会自动补同物体的 `BackpackInventory`；把箱子拖到窗口组件的 `Default Chest`。
3. 进入 Play。默认左侧解析本地玩家，右侧显示指定箱子已有物品；箱子尚无物品时显示空格。

箱子测试物品生成（2026-10-12）：给箱子同物体的 `BackpackInventory` 配置 `Catalog`，勾选 `ChestInventory` 的 `Fill Catalog On Start`，进入 Play 后会按角色背包相同规则填入目录模板：不可堆叠物品 1 件，可堆叠物品最多 5 件且不超过上限。该开关默认关闭；运行中也可通过箱子组件右键菜单“测试/从目录填入物品”手动生成。填充会追加或合并物品，容量不足的模板按现有添加方法拒绝并输出原因，完成后输出成功数量。

原场景上的 `BackpackGridUI` 继续使用原序列化字段，Awake 自动获取或添加显示组件并传递配置，无需重新连接格子与物品模板。双栏预制体已配置一份 `BackpackInteractionController`；旧版本窗口缺少该组件时由宿主自动补齐，无需给左右栏分别添加控制器。

## 5. 第一阶段交互契约

1. 左键点击物品只记录来源、实例、整堆数量和抓取格偏移，并隐藏源框；真实归属、占格和数量保持原样。
2. 指针命中左右栏时按该栏本地排布计算落点；携带预览共用窗口最上层，允许左右不同尺寸和格子大小。目标覆盖完整矩形为空，且目标真容器也有完整空矩形时显示可放置反馈；任何占用、越界或容量不足都显示阻塞并保持携带。
3. 跨容器放下调用 `TryTransferItemToEmptySpace`，保留实例、ID、数量，不自动合并任何已有堆叠。成功后才写入目标的本地显示位置并刷新双方。显示位置与库存自动寻位坐标相互独立，不把 UI 下标当作库存下标；同栏空位放下仅调整本地显示位置。
4. Esc 或右键取消当前拿起；背包开关键关闭窗口，`Close`、禁用、销毁也先取消并清空待处理点击再解绑。来源切换、来源物品被移除、携带数量改变、任一栏失效会取消未提交的拿起。取消优先于同帧左键提交。
5. 打开箱子先关闭原单栏，再保存并暂停当前玩家 `PlayerInput`、显示与解锁光标；重复 `Open` 不覆盖原快照，晚就绪的玩家也会补暂停，关闭按原状态恢复。箱子打开与关闭当帧拦截旧单栏快捷键、鼠标转发与控制器轮询，防止一份输入被两个窗口处理。输入动作映射仍由 `InputManager` 独占启停。

两侧必须能取得实际 `InventoryContainer` 才启用本阶段交互。单机与有权威容器的主机可使用；纯网络客户端保留只读显示，跨容器服务端命令尚未接入。

## 6. 验证边界

最终 `Inventory`、`Runtime`、`UI` 的实际源码引用真实 Unity DLL，按 C# 9.0 编译通过，0 警告、0 错误；原 `BackpackInteractionController` 一并编译。真实 `InventoryGridLayout` 的 4 个场景、48 次断言通过，覆盖两份排布独立、尺寸与溢出、增删重排和清空旧来源。

第一阶段验证（2026-10-12）：空位转移数据接口使用真实库存源码与少量 Unity 桩，22 个场景、792 次断言通过；真实库存与排布联合验证使用实际 Unity Core DLL，7 个场景、134 次断言通过，覆盖双向转移、同类堆叠不合并、显示落点与后台自动位置分离、多格抓取偏移、目标占用及容量不足零变更。第一阶段 `Inventory`、`Runtime`、`UI` 源码已重新引用真实 Unity DLL 编译，0 警告、0 错误。

基础显示的预制体引用已核对；第一阶段新增交互组件后共有 13 个本地对象，左右为不同显示组件。每栏 568 × 424 像素，窗口 1232 × 512 像素与边距、栏间距、标题高度一致。真实 GameObject 渲染和编辑器场景点击仍需在 Unity 中确认。
