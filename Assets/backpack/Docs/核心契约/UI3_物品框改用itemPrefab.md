# UI3 物品框改用 itemPrefab（含 背包物品UI 英文化）

> 状态：**已落盘**——2026-10-06 按本文件完成改动：代码 2 文件（1 次改名）+ prefab 1 个 + 场景 1 个；`dotnet build RPG4.Backpack.UI.csproj` **0 错误 0 警告**。待 Unity 内打开工程复核接线后完成最终验收。
> 落地偏差：文档文件名采用本目录既有编号体系（`UI1_` / `UI2_` → `UI3_`），未使用计划中的 `BackpackItemUI接入itemPrefab.md`；场景与 prefab 的接线由直接改写 YAML 完成（非 Unity 内拖拽），见 3.6。
> 日期：2026-10-06
> 影响面：`Scripts/UI/BackpackGridUI.cs`、`Scripts/UI/背包物品UI.cs`（→ `BackpackItemUI.cs`）、`Prefabs/物品ui.prefab`、`Scenes/New Scene.unity`
> 关联文档：`Docs/工程架构/F1_F2_程序集与目录命名规范.md` §3.2.1、`Docs/核心契约/UI1_面板接口与多窗口支持.md`、`Docs/核心契约/UI2_网格主控内部结构整理.md`（本次改写其 3.5 表第 12 区）
> 性质：**视图层重构 + 资产接线修复**。数据层、输入层、交互层判定逻辑、网络复制均不动。

---

## 一、背景

### 1.1 现状

物品框当前由 `BackpackGridUI` 在运行时用 `new GameObject` 拼装（`#region 私有绘制工具`：`CreateWidget` / `CreateRect` / `CreateText` / `ApplyWidget` / `ApplyWidgetHover` / `SetWidgetAlpha`），而两个早已声明、注释写明用途的字段从未被使用：

| 项 | 位置 | 状态 |
|---|---|---|
| `itemPrefab`（物品UI预制体） | `BackpackGridUI.cs:25` | 声明后全仓 **0 处读写** |
| `itemUIList`（物品UI列表） | `BackpackGridUI.cs:26` | 声明后全仓 **0 处读写** |
| `背包物品UI` 组件 | `Scripts/UI/背包物品UI.cs:9` | 仅被 `BackpackGridUI.cs:26` 的泛型参数引用，无实际调用 |
| `物品ui.prefab`（挂 `背包物品UI`） | `Prefabs/物品ui.prefab:299` | 存在但无人实例化 |

### 1.2 引用面核查结论（改动半径依据）

| 项 | 结论 |
|---|---|
| `背包物品UI` 类型引用 | 全仓仅 `BackpackGridUI.cs:26`（`List<背包物品UI> itemUIList`） |
| 挂 `背包物品UI`（脚本 guid `d1cd5627c5a799a4480c7f3aac5f5b26`）的资产 | ① `Prefabs/物品ui.prefab` 的 MonoBehaviour `8488252464604072532`；② `Scenes/New Scene.unity` 的 MonoBehaviour `216515530`（物体名 `GameObject`，Canvas 直接子级）。**共 2 处** |
| 挂 `BackpackGridUI`（脚本 guid `e51a42c83c8541b459aa33b9a9db4707`）的场景 | 仅 `Scenes/New Scene.unity` 的 MonoBehaviour `737851992`，**无其它场景** |
| `ItemWidget` / `CreateWidget` / `ApplyWidget` / `ApplyWidgetHover` / `SetWidgetAlpha` / `CreateRect` / `CreateText` | 全部为 `BackpackGridUI.cs` 内的 `private`，**无任何跨文件调用者**（仅 UI2 文档描述性提及） |
| 其它「运行时拼装物品框」的遗留实现 | 未发现，`new GameObject` / `AddComponent<Image>` / `AddComponent<TextMeshProUGUI>` 在背包内只集中在 `BackpackGridUI.cs` |
| `BackpackInteractionController` 的依赖面 | 只用公开 API（`SetItemHovered` / `TryGetPlacement` / `TryGetItem` / 携带预览四件套等），不触碰 `ItemWidget`，**本次零改动** |

### 1.3 现存缺陷

| # | 缺陷 | 证据 |
|---|---|---|
| P1 | `itemPrefab` / `itemUIList` 是空挂字段，prefab 资产做出来却没人用 | `New Scene.unity:615-632` 的 `BackpackGridUI` 段里根本没有这两个字段的序列化值 |
| P2 | 尺寸口径两处漂移：`背包物品UI.设置物品图标` 硬编码 `x * 90`；主控用 `cellSize=100` + `slotSpacing=(10,10)`（步距 110） | `背包物品UI.cs:27`、`New Scene.unity:622-623` |
| P3 | 配色硬编码：脚本把根节点 Image 写成 `BackpackFeedbackPalette.ItemFrame`，prefab 自定义的蓝色底（`0.137, 0.286, 0.494`）被覆盖 | `BackpackGridUI.cs:1143`、`物品ui.prefab:273` |
| P4 | 无图标时回落渲染 `item.ItemType` 文本，需要额外 Label 文本节点与断行 / 省略号配置 | `BackpackGridUI.cs:1156-1159, 1178-1179` |
| P5 | **prefab 自身接线不全**：`物品ui.prefab:302` 的 `物品数量` 为 `{fileID: 0}`，一旦调用数量接口即 NRE；场景实例 `New Scene.unity:177` 同样为空 | 同上 |
| P6 | 物品框外观改动必须改代码，美术无法在 prefab 上独立调 | 全文件 `BackpackGridUI.cs:1133-1233` |

---

## 二、变更总览

| 编号 | 变更 | 类型 | 影响文件 |
|---|---|---|---|
| B1 | `背包物品UI` 升级为物品框视图契约，新增 `SetSize` / `SetItem` / `SetHover` / `SetAlpha` 与空引用保护 | 契约 | `背包物品UI.cs` |
| B2 | 类与文件改名 `背包物品UI` → `BackpackItemUI`，字段 / 方法全部英文化 | 命名 | 同上 + `.meta` |
| B3 | 物品框创建由 `new GameObject` 改为 `Instantiate(itemPrefab)` | 结构 | `BackpackGridUI.cs` |
| B4 | `itemUIList` 落地为「活跃物品 UI 登记表」，创建登记 / 回收注销 / 解绑清空 | 结构 | 同上 |
| B5 | 悬停反馈改为「prefab 常态色 ↔ `ItemFrameHovered`」，取消悬停还原 prefab 初始色 | 行为 | 同上 + `BackpackItemUI.cs` |
| B6 | 无图标时隐藏图标并留空，删除类型名回落与 `Content` / `Icon` / `Stack` 拼装 | 行为 | `BackpackGridUI.cs` |
| B7 | 携带预览复用同一 `itemPrefab` 与同一创建路径 | 结构 | 同上 |
| B8 | 删除失去调用者的绘制工具 `CreateRect` / `CreateText`，`#region` / `#endregion` 区域「私有绘制工具」收敛改名为「物品框视图工具」 | 清理 | 同上 |
| B9 | 资产重接线：`物品ui.prefab` 补 `iconImage` / `stackText` / `backgroundImage`；场景实例同步改名 | 资产 | `物品ui.prefab`、`New Scene.unity` |
| B10 | `BackpackInteractionController.cs` 零改动（公开 API 签名与语义不变） | 约束 | — |

---

## 三、逐项方案

### 3.1 视图契约（B1）

`BackpackItemUI` 只做「把数据画到 prefab 上」，不做任何尺寸推导与库存判定：

```
public class BackpackItemUI : MonoBehaviour
{
    [SerializeField] private Image backgroundImage; // 物品框底图（悬停换色；prefab 接线到根节点）。
    [SerializeField] private Image iconImage;       // 物品图标（无图标时隐藏）。
    [SerializeField] private TMP_Text stackText;    // 堆叠数量（大于 1 才显示）。

    public void SetSize(Vector2 pixelSize);             // 主控按 GridMetrics 算好的物品像素尺寸。
    public void SetItem(Sprite icon, int displayStack); // 图标为空则留空；数量 ≤ 1 不显示。
    public void SetHover(bool hovered);                 // 常态色 = Awake 缓存的 prefab 初始色。
    public void SetAlpha(float alpha);                  // 按需挂 CanvasGroup，携带预览用。
}
```

行为细则：

1. `Awake` 缓存 `rectTransform`、`backgroundImage.color`（记作 `baseColor`）与 `iconSize` 的最近值；`backgroundImage` 缺失时 `SetHover` 只跳过换色、不报错。
2. `SetSize` 写 `rectTransform.sizeDelta = pixelSize` 并重算图标内嵌；`SetItem` 写图标与数量后同样触发一次内嵌重算（两者都可能先到，故内嵌逻辑独立成私有方法 `ApplyIconLayout`）。
3. 图标等比内嵌：以**传入的像素尺寸**为可用区域（`size = pixelSize + iconPadding`，内边距由序列化字段给定，默认 0，与 prefab 现状「图标 100×100 填满 100×100 根节点」一致）；按 `sprite.rect` 宽高比取短边贴齐、长边留空。
4. `icon == null` → `iconImage.gameObject.SetActive(false)`，留空即返回；不再有任何类型名文本（对应 Q3 决策）。
5. 数量文本：`displayStack > 1` 显示 `"x" + displayStack`，否则清空为 `string.Empty`（格式**只在此处定义**，主控不再拼文本）；`stackText == null` 时跳过。
6. `SetAlpha`：惰性 `AddComponent<CanvasGroup>()`，固定 `interactable = false` / `blocksRaycasts = false`，只写 `alpha`。
7. 所有可空引用一律跳过 + 每实例只告警一次（`BackpackLog.Warn(LogModule.UI, …)`），不逐帧刷屏。

### 3.2 改名与英文化（B2）

| 原 | 新 | 说明 |
|---|---|---|
| 类 `背包物品UI` / 文件 `背包物品UI.cs` | `BackpackItemUI` / `BackpackItemUI.cs` | MonoBehaviour 必须与文件名同名 |
| `.meta` | `背包物品UI.cs.meta` → `BackpackItemUI.cs.meta` | **guid `d1cd5627c5a799a4480c7f3aac5f5b26` 保持不变**，故 prefab / 场景的 `m_Script` 引用不丢 |
| 字段 `物品数量` (TMP_Text) | `stackText` | 序列化键改名 → YAML 同步改（见 3.6） |
| 字段 `物品图标` (Image) | `iconImage` | 同上 |
| —（新增） | `backgroundImage` (Image) | 悬停底图，指向根节点 Image |
| —（新增） | `iconPadding` (float) | 图标可用区域的内缩量，默认 0 |
| 方法 `设置物品数量(int)` | 并入 `SetItem(Sprite, int)` | 删除独立入口，格式与图标一起写入 |
| 方法 `设置物品图标(int x, int y, Sprite)` | 拆分 `SetSize(Vector2)` + `SetItem(Sprite, int)` | 尺寸来源改为主控 |

`BackpackGridUI` 侧同步：`List<背包物品UI> itemUIList` → `List<BackpackItemUI> itemUIList`。

命名口径回归项目通用偏好（英文标识符 + 中文注释），中文标识符仅保留在资产名（`物品ui.prefab`）与其 `m_Name` 上。

### 3.3 尺寸口径（B3 前置）

尺寸公式**只保留在 `BackpackGridUI.GridMetrics` 一处**：

| 场景 | 公式（`GridMetrics`） | 1 格（100/10） | 2×1 格 |
|---|---|---|---|
| 单格边长 | `cellSize` | 100 | — |
| 物品块尺寸 | `ItemPixelSize(w, h) = w*cellSize + spacing.x*(w-1)`（y 同理） | `(100,100)` | `(210,100)` |
| 物品落点 | `CellToPixel(cell) = (x*(cellSize+spacing.x), -y*(cellSize+spacing.y))` | 步距 110 | 步距 110 |

主控在放置循环里把 `ItemPixelSize` 的结果通过 `SetSize` 传给 `BackpackItemUI`，后者不再有任何「格边长」概念；`背包物品UI.cs:27` 的 `x * 90` 随之删除。

### 3.4 创建、回收与活跃登记表（B3–B4）

`ItemWidget` 收敛为两项：

| 字段 | 类型 | 说明 |
|---|---|---|
| `root` | `GameObject` | prefab 实例根节点 |
| `view` | `BackpackItemUI` | 根节点上的视图组件 |

`CanvasGroup` 不再由主控持有（`SetAlpha` 由视图自理），`frame` / `icon` / `label` / `stack` 四个子节点引用全部消失。

`CreateWidget(RectTransform parent)`：
1. `itemPrefab == null` → 记一条警告（状态翻转时一次）并返回 `null`；
2. `Instantiate(itemPrefab, parent)`，取 `BackpackItemUI`；
3. 强制 `anchorMin = anchorMax = pivot = (0, 1)`（prefab 现状是 `(0.5, 0.5)`，不覆盖会半格偏移）、`anchoredPosition = zero`、`localScale = one`、`layer` 沿用 prefab 的 5；
4. 组件缺失 → 销毁实例、记警告、返回 `null`。

`Refresh()` 循环：
- 取不到 widget（`null`）→ `continue`，**不登记**、不写 `render.widgets`（下次重绘重试）；
- 新建成功 → `itemUIList.Add(view)`；
- 回收（`obsolete`）→ `itemUIList.Remove(view)` 后 `Destroy(root)`；
- `SetActive(false)` 隐藏携带中物品的逻辑保留（仍占格，位置记录不变）。

`Unbind()`：清空 `render.widgets` 前先把 `itemUIList` 清空（含 `carriedPreview`）。`OnDestroy()` 同样清空。

活跃登记表对外只读：

```
public IReadOnlyList<BackpackItemUI> ItemViews => itemUIList; // 运行时活跃物品 UI（代码独占维护）。
```

序列化字段注释写明：**代码独占维护，勿在场景中预拖**（用户已确认不做对象池，实例一律按需创建销毁）。

### 3.5 悬停与透明度（B5）

| 项 | 现状 | 变更后 |
|---|---|---|
| 悬停变 | `ApplyWidgetHover` 写 `frame.color = ItemFrameHovered / ItemFrame` | 转调 `view.SetHover(bool)`：悬停写 `BackpackFeedbackPalette.ItemFrameHovered`，取消悬停写 `Awake` 缓存的 `baseColor`（= prefab 的蓝底） |
| 携带预览半透明 | `SetWidgetAlpha` 在根节点惰性挂 `CanvasGroup` 写 0.78 | 转调 `view.SetAlpha(0.78f)`，常量 `CarriedAlpha` 保留在 `BackpackGridUI` |

`SetItemHovered` / `Refresh` 里的悬停补色路径不变，只换实现。

### 3.6 资产重接线（B9）

**`Prefabs/物品ui.prefab`**（MonoBehaviour `8488252464604072532`，第 299–303 行）：

| 行 | 现状 | 改为 |
|---|---|---|
| 300 | `m_Script: … guid: d1cd5627c5a799a4480c7f3aac5f5b26` | 不变 |
| 302 | `"\u7269\u54C1\u6570\u91CF": {fileID: 0}` | `stackText: {fileID: 8259297500555138283}`（子节点 `Text (TMP)`） |
| 303 | `"\u7269\u54C1\u56FE\u6807": {fileID: 4560813680279190635}` | `iconImage: {fileID: 4560813680279190635}`（子节点 `Image`） |
| —（新增） | — | `backgroundImage: {fileID: 8928634845197331061}`（根节点自身 Image） |
| —（新增） | — | `iconPadding: 0` |

顺带：`Text (TMP)` 的样例文本 `m_text: x10000`（第 68 行）置空，避免未赋值时显示假数据。根节点 Image 的蓝色与 Sliced 设置保留（悬停常态色取自它）。

**`Scenes/New Scene.unity`**（MonoBehaviour `216515530`，第 165–178 行）：同一套改名与补接线，目标为 `stackText: {fileID: 680956776}`、`iconImage: {fileID: 1048886716}`、`backgroundImage: {fileID: 216515531}`。

**`Scenes/New Scene.unity` 的 `BackpackGridUI`**（MonoBehaviour `737851992`，第 620–624 行）：已在 `slotPrefab` 之后插入 `itemPrefab: {fileID: 8925857034192164597, guid: af57d5486ee16654fab52b69b1e1cd8d, type: 3}`（= `物品ui.prefab` 根节点）与 `itemUIList: []`。该组件是场景原生对象而非 prefab 实例，无需 `m_Modifications`，因此直接改写 YAML 即可；Unity 打开工程后应复核 Inspector 上两个字段已填。

`物品ui.prefab.meta` 的 guid 为 `af57d5486ee16654fab52b69b1e1cd8d`，`BackpackItemUI.cs.meta` 改名后 guid 仍为 `d1cd5627c5a799a4480c7f3aac5f5b26`（`物品ui.prefab` 与 `New Scene.unity` 的 `m_Script` 引用因此未断）。

`RPG4.Backpack.UI.csproj` 是 Unity 自动生成的工程文件，其中一行 `<Compile Include="…\背包物品UI.cs" />` 已同步为 `BackpackItemUI.cs`（下次 Unity 刷新工程时会被重建，无需手工维护）。

### 3.7 清理与文档回填（B8）

- 删除 `CreateRect` / `CreateText`（B7 之后唯一调用者消失）；
- 删除 `ItemWidget.label`（类型名回落）与 `Content` / `Icon` / `Stack` 相关拼装；
- `#region` / `#endregion` 区域「私有绘制工具」改为「物品框视图工具」，成员收缩为 `CreateWidget` / `ApplyWidget` / `ApplyWidgetHover` / `SetWidgetAlpha`；字段、属性与方法仍按 UI2 3.5 的职责区归类，区域必须成对且不跨类型边界；
- `uiFont` 字段的作用范围收窄为「脚本新建的格子 / HUD 文本」，注释同步；物品框文本字体改由 prefab 自带 TMP 资产决定；
- 回填 `UI2_网格主控内部结构整理.md` 3.5 表第 12 区（原文列出的 `CreateRect` / `CreateText` 已不存在）。

---

## 四、明确不做

| 项 | 理由 |
|---|---|
| 对象池 / 复用物品框实例 | 用户已确认 `itemUIList` 为活跃登记表；物品框只在库存增删时创建销毁，非逐帧 |
| 无图标回落显示物品类型名 | 用户决策为「图标置空」；回落需给 prefab 加名称文本节点 |
| 物品框内联名称 / 描述 / 品质角标等新字段 | 超出本次范围 |
| 改 `BackpackInteractionController.cs` | 公开 API 不变，控制器零改动 |
| 改数据层 / 输入层 / 网络复制 | 本任务纯视图层 |
| 用 `GridLayoutGroup` 排布物品框 | 物品框是多格自由定位（`CellToPixel`），布局组件不适用 |
| 调整 `cellSize` / `slotSpacing` 数值去迁就 prefab 的 90 | 尺寸口径统一到 `GridMetrics`，不反向改场景参数 |

---

## 五、验收清单

1. 编译：`dotnet build` 目标程序集 0 错误（`RPG4.Backpack.UI`）；无「未使用字段」类编译警告新增。
2. 引用面：`背包物品UI` 中文标识符在 `.cs` 中零残留；`CreateRect` / `CreateText` 已删除且无调用者残留。
3. 改名安全：`BackpackItemUI.cs.meta` 的 guid 仍为 `d1cd5627c5a799a4480c7f3aac5f5b26`；Unity 打开工程后 `物品ui.prefab` 与 `New Scene.unity` 上该组件**不显示 Missing Script**。
4. 接线：`物品ui.prefab` 与场景实例的 Inspector 上三个引用（背景 / 图标 / 数量）均非 None。
5. 尺寸：背包打开后，1 格物品框实测 100×100、2×1 格物品框 210×100，落点与格子对齐（步距 110），无半格偏移。
6. 悬停：悬停物品框变为高亮色，移开恢复 prefab 原始蓝底（不是 `ItemFrame` 常量色）。
7. 无图标：物品无 `ImageFragment.Icon` 时该格只剩底图，无任何文字残留。
8. 数量：堆叠 > 1 显示 `x<N>`，= 1 无文本，且不抛 NRE（P5 已修）。
9. 携带预览：外观与网格内一致、半透明 0.78、不接收指针事件；隐藏原格与复位行为不变。
10. 多窗口 / 重绘：`itemUIList` 在每次重绘后与场上实例数一致（无残留、无重复）；解绑与销毁后归零。
11. 交互层：`BackpackInteractionController.cs` diff 为空。

---

## 六、待决策项

1. **场景里的 `GameObject`（`New Scene.unity:216515528`，Canvas 直接子级、挂 `背包物品UI`）如何处置**：它是 `物品ui.prefab` 的手工原型实例，当前不受任何脚本引用。可选：① 删除（推荐，避免同款脚本两处维护）；② 保留作视觉对照；③ 就地改造成 prefab 变体后删除原物。
2. **数量文本前缀**：保留 prefab 现状的小写 `"x"`（本次默认），还是改成 `"×"`。
3. **`iconPadding` 是否需要**：默认 0（图标可用区域 = 物品块整块）。若美术希望图标四周留缝，改 prefab 上图标节点尺寸或调此字段。
4. **`ItemViews` 只读视图是否保留**：当前仅调试用，若确认无用可只留序列化列表、不开放属性。
5. `UI2` 文档是否需要整体重发（本次只回填第 12 区一行）。
