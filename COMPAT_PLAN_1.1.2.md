# RDLE-a11y Mod 新版兼容性分析与兼容计划报告

> 生成日期：2026-08-08
> 分析对象：`RDLevelEditorAccess`（BepInEx mod，v1.9）对新版 Rhythm Doctor（游戏 DLL 更新于 **2026-08-02**，即 1.1.1 之后的版本）的兼容性
> 对比基准：`agents references/Assembly-CSharp/`（新版反编译，380 文件）vs `agents references/Assembly-CSharp_old/`（旧版反编译，360 文件）
> 状态：**待审核**——本报告仅分析与计划，未经批准不执行任何修改

---

## 一、结论摘要

| 项目 | 结论 |
|------|------|
| **启动报错根因** | 游戏 2026-08-02 更新将 **Sprite（精灵）系统整体重构为 Decoration（装饰物）系统**，mod 依赖的 20+ 个旧 API（`Tab.Sprites`、`scnEditor.selectedSprite`、`spritesData`、`eventControls_sprites`、`LevelEvent_MakeSprite`、`SpriteHeader` 等）在新版中**全部被删除/重命名**。针对新 DLL 重新编译会直接失败（实测 3 处 `CS0246` 报错，修复后还将暴露 30+ 处成员引用错误）。若部署旧编译的 DLL，运行时首帧执行访问旧字段的代码即抛 `MissingFieldException` |
| **影响范围** | Sprite 页签功能（虚拟菜单、导航、朗读、插入事件）需整体迁移到 Decoration；另有 6 处次要不兼容（枚举重排、属性继承行为变化、命名空间移动等） |
| **好消息** | **13 个 Harmony Patch 目标方法全部健在且签名不变**（已用反射验证）——启动崩溃并非 Patch 目标缺失导致，而是 mod 自身代码引用旧 API |
| **计划规模** | 预计修改 `EditorAccess.cs` ~25 处、`FileIPC.cs` ~10 处、本地化键 8 处、新增 Decoration 事件类型支持 ~4 类 |

---

## 二、时间线与环境事实

1. **mod 1.9**（2026-06-27）声明兼容游戏 **1.1.1**（2026-06-16 更新）
2. 游戏更新至 **2026-08-02** 版本（`D:\SteamLibrary\steamapps\common\Rhythm Doctor\Rhythm Doctor_Data\Managed\Assembly-CSharp.dll` 最后写入时间 08-02 02:42:44）
3. mod 编译引用 `$(GameDir)` 下的实时 DLL（`RDLevelEditorAccess.csproj`），因此**一旦游戏更新，mod 即失去编译基准**
4. 反编译库差异：新版 1078 文件 / RDLevelEditor 380 文件；旧版 1050 文件 / RDLevelEditor 360 文件。新增 27 文件、移除 8 文件

---

## 三、启动崩溃根因（证据链）

### 3.1 直接证据：编译失败

针对新版 DLL 执行 `dotnet build`：

```
EditorAccess.cs(3535,64): error CS0246: The type or namespace name 'LevelEvent_MakeSprite' could not be found
EditorAccess.cs(3572,45): error CS0246: The type or namespace name 'LevelEvent_MakeSprite' could not be found
EditorAccess.cs(3606,85): error CS0246: The type or namespace name 'LevelEvent_MakeSprite' could not be found
```

**实测只显示 3 个错误的原因**：Roslyn 错误级联抑制——当方法签名中出现无法解析的类型（`List<LevelEvent_MakeSprite>`）时，编译器抑制该文件后续的成员绑定错误。已用最小实验（probe7）证实：同一文件内一旦出现 `List<LevelEvent_MakeSprite>` 签名错误，`Tab.Sprites` 的 CS0117、`scnEditor.spritesData` 的 CS1061 均被抑制。独立环境（probe6）下这些错误全部显现：

```
Probe6.cs(7,38): error CS0117: 'Tab' does not contain a definition for 'Sprites'
Probe6.cs(8,24): error CS1061: 'scnEditor' does not contain a definition for 'spritesData'
```

### 3.2 新 DLL 反射确认（asmcheck 程序实测）

```
Tab 枚举成员: Song, Rows, Actions, Rooms, Decorations, Windows, None   ← 无 Sprites
scnEditor 字段(sprite/deco): eventControls_decorations, decorationsData, selectedDecoration  ← 无 spritesData/selectedSprite/eventControls_sprites
scnEditor 属性(sprite/deco): selectedDecorationsTabPageIndex, lastUsedDecoration, currentPageDecorationsData, tabSection_decorations
LevelEventType: MakeSprite=MISSING, Sprite=60, ReorderSprite=MISSING, ReorderDecoration=79, GoToLevel=86, TintText=64, SetText=68, SetFont=69, AdvanceTextDecoration=39
LevelEvent_MakeSprite 类型: False；LevelEvent_Sprite 类型: True；LevelEvent_MakeDecorationBase 类型: True
```

### 3.3 运行时崩溃机理（若部署旧编译 DLL）

1. `EditorAccess.Awake()` → `harmony.PatchAll()`：13 个 Patch 目标（`SelectEventControl`、`AddEventControlToSelection`、`get_userIsEditingAnInputField`、`ShowTabSection`、`PreviousButtonClick`、`NextButtonClick`、`Copy`、`Cut`、`Paste(bool)`、`TabSection.ChangePage`、`Timeline.PreviousPage/NextPage`、`RDString.Get`）**经反射验证全部存在、签名不变** → Patch 应用不会抛异常
2. 场景加载 → `StaticOnSceneLoaded` 创建 `AccessLogic`
3. `AccessLogic.Update()` 首帧执行 → 任何访问 `editor.selectedSprite` / `editor.spritesData` / `editor.eventControls_sprites` / `editor.tabSection_sprites` 等**已删除字段**的方法被 JIT 编译 → **`MissingFieldException`**
   - 注意：`Tab.Sprites` 是枚举常量（编译为 int 字面量），运行时**不崩溃**但语义错乱（指向 Decorations 页签）；**字段/属性访问才崩溃**
4. 崩溃位置示例：`EditorAccess.cs:805`（`editor.selectedSprite`）、`:3546`（`editor.spritesData`）、`:3265`（`editor.tabSection_sprites`）等

---

## 四、Sprite → Decoration 重构全景（类型映射）

### 4.1 一对一重命名映射（编译期硬错误）

| 旧 API（mod 现引用） | 新 API | 变化性质 |
|---|---|---|
| `Tab.Sprites`（枚举值 4） | `Tab.Decorations`（枚举值 4） | 枚举成员改名，值不变 |
| `scnEditor.selectedSprite` (string) | `scnEditor.selectedDecoration` (string) | 字段改名 |
| `scnEditor.spritesData` (`List<LevelEvent_MakeSprite>`) | `scnEditor.decorationsData` (`List<LevelEvent_MakeDecorationBase>`) | 字段改名+类型升级 |
| `scnEditor.eventControls_sprites` | `scnEditor.eventControls_decorations` | 字段改名 |
| `scnEditor.tabSection_sprites` | `scnEditor.tabSection_decorations` | 字段改名（类型 `TabSection_Sprites`→`TabSection_Decorations`） |
| `scnEditor.selectedSpritesTabPageIndex` | `scnEditor.selectedDecorationsTabPageIndex` | 属性改名 |
| `scnEditor.currentPageSpritesData` | `scnEditor.currentPageDecorationsData` | 属性改名 |
| `scnEditor.AddNewSprite(LevelEvent_MakeSprite)` | `scnEditor.AddNewDecoration(LevelEvent_MakeDecorationBase)` | 方法改名+参数类型 |
| `LevelEvent_MakeSprite`（具体类） | `LevelEvent_MakeDecorationBase`（抽象基类）+ `LevelEvent_Sprite`（具体子类） | 类层次重构 |
| `LevelEvent_MakeSprite.spriteId` | `LevelEvent_MakeDecorationBase.decorationId` | 属性改名（JSON `"id"`） |
| `SpriteHeader.ShowPanel(string)` | `DecorationHeader.ShowPanel(string)` | 类改名，静态方法签名不变 |
| `SpriteHeader.GetSpriteData*` | `DecorationHeader.GetDecorationData*` | 静态方法改名 |
| `LevelEventType.MakeSprite`（=59） | `LevelEventType.Sprite`（=60） | 枚举改名+值+1 |
| `LevelEventType.ReorderSprite`（=74） | `LevelEventType.ReorderDecoration`（=79） | 枚举改名+值偏移 |
| `LevelEventControl_Sprite` | `LevelEventControl_Decoration` | 控件类改名 |
| `TabSection_Sprites` | `TabSection_Decorations` | 页签类改名 |
| `InspectorPanel_MakeSprite` | `MakeDecorationInspectorPanel` + `InspectorPanel_Sprite` | 面板类改名 |
| `isSpriteTabEvent` | `isDecorationTabEvent` | 属性改名 |
| `LevelEvent_Move.makeSprite` | `LevelEvent_Move.sprite`（仅当目标为 `LevelEvent_Sprite`） | 字段改名+类型收窄 |
| 关卡文件 key `"sprites"` | key `"decorations"` | 序列化 key 变更（`RDLevelData.cs:353`） |
| 运行时 `level.sprites` 字典 | 仍叫 `sprites`（键为 decorationId） | 名字未变，键语义变 |

### 4.2 新增 Decoration 事件体系（mod 尚无支持）

新版装饰物页签新增 8 个事件类型 + 4 个抽象基类：

| 新类型 | 枚举值 | 说明 |
|---|---|---|
| `LevelEvent_Text` | 61 | 文本装饰（歌词）— 新建 |
| `LevelEvent_TintText` | 64 | 文本染色 |
| `LevelEvent_SetText` | 68 | 设置文本内容 |
| `LevelEvent_SetFont` | 69 | 设置字体/加粗/字距/描边 |
| `LevelEvent_AdvanceTextDecoration` | 39 | 推进文本行（新值插入导致后续枚举全部 +1） |
| `LevelEvent_ReorderDecoration` | 79 | 装饰物排序（原 ReorderSprite 升级为双目标） |
| `LevelEvent_GoToLevel` | 86 | 跳转关卡（**普通玩法事件**，非装饰） |
| `LevelEvent_MakeDecorationBase` | — | 装饰容器抽象基类（`decorationId`、`depth`） |
| `LevelEvent_TextAttributesBase` | — | 文本属性基类（text/color/outline/position/size/angle/mode/anchor/narrate） |
| `LevelEvent_SyllableBase` | — | 音节基类（`fadeOutDuration`） |
| `ICompatibleDecorationTypeChecker` | — | 新接口：修饰事件与目标装饰类型是否匹配（不匹配时控件显示深绿色 + InspectorPanel 显示"不兼容"面板） |

### 4.3 行为变化（不报错但语义变）

| 位置 | 旧行为 | 新行为 | mod 影响 |
|---|---|---|---|
| `LevelEventInfo.propertiesInfo` | 仅收集当前类型的 `DeclaredOnly` 属性 | **沿继承链遍历**，含基类属性 | **重大**：mod 依赖"propertiesInfo 已自动排除基础属性"（`EditorAccess.cs:1230` 注释明确说明）——现在 Sprite/Text/SetText 等会多出基类属性，Helper 属性列表与编辑逻辑需适配 |
| `InspectorPanel.Show()` | 直接显示 | 新增 `ICompatibleDecorationTypeChecker` 检查，不兼容时显示专用面板 | mod 朗读"不兼容"面板需支持 |
| `scnEditor.ShowLevelEventSelectorPanel(bool)` | 单参 | `(bool filterMode, EventCategory categoryToShow)` | 签名变化（mod 是否直接调用待查） |
| `LevelEvent_Base.row` | 字段 | `_row` 字段 + `row` 属性 | 读访问兼容 |
| `LevelEvent_Base.usesType` | （bug）`usesBar` | 修正为 `usesType` | 影响事件类型判断逻辑 |
| `RDEditorConstants.CurrentVersion` | 67 | 68 | 版本号 |
| `FloatingTextMode` | `RDLevelEditor` 命名空间 | **全局命名空间** + 新增 `Never=2` 值 | 命名空间移动（若有 `using RDLevelEditor` 仍可解析） |
| 本地化 | `editor.sprites` | 需 `editor.decorations`（bundled 本地化文件**无此键**） | 朗读页签名需回退/新增 |
| `settingsMenu` 字段类型 | `RDPauseMenu` | `PauseMenu` | mod 仅访问 `.gameObject`，兼容 |
| 角色 | 47 个 | 48 个（新增 `Character.Cranky`）+ 新增 `UnsafeEditorCharacters` 数组 | 角色列表朗读需更新 |
| 装饰头添加按钮 | 直接创建精灵 | 打开 `ShowLevelEventSelectorPanel(EventCategory.MakeDecoration)` 让用户选 Sprite/Text | 插入事件虚拟菜单流程变化 |

---

## 五、mod 代码完整修改清单

### 5.1 `RDLevelEditorAccess/EditorAccess.cs`（主战场，~25 处）

**Sprite 页签相关（必改）**：

| 行号 | 现有代码 | 修改为 | 类别 |
|---|---|---|---|
| 98, 1813-1815 | `virtualMenuPurpose == "sprite"` | `"decoration"` | 逻辑 |
| 700 (页签朗读) | `editor.{tab}` 键 | `Tab.Decorations` 需对应 `editor.decorations`（本地化） | 本地化 |
| 705, 805, 936, 1938, 2002, 3394, 3405, 3861, 4204, 4309, 4624 | `Tab.Sprites` | `Tab.Decorations` | 编译错误 |
| 805, 3496, 4233, 4311 | `editor.selectedSprite` | `editor.selectedDecoration` | 编译错误 |
| 3258 | `editor.selectedSpritesTabPageIndex` | `editor.selectedDecorationsTabPageIndex` | 编译错误 |
| 3260 | `new LevelEvent_MakeSprite()` | `new LevelEvent_Sprite()`（或按装饰类型工厂） | 编译错误 |
| 3264 | `editor.AddNewSprite(spriteData)` | `editor.AddNewDecoration(spriteData)` | 编译错误 |
| 3265 | `editor.tabSection_sprites.UpdateUI()` | `editor.tabSection_decorations.UpdateUI()` | 编译错误 |
| 3449, 3498 | `editor.currentPageSpritesData` | `editor.currentPageDecorationsData` | 编译错误 |
| 3501, 3864, 4243, 4317 | `.spriteId` | `.decorationId` | 编译错误 |
| 3542, 3867 | `SpriteHeader.ShowPanel(spriteId)` | `DecorationHeader.ShowPanel(decorationId)` | 编译错误 |
| 3546, 3614, 3862, 4241, 4315 | `editor.spritesData` | `editor.decorationsData` | 编译错误 |
| 3547-3549, 3615-3617, 3952-3954, 4245-4247, 4319-4321, 4472 | `editor.eventControls_sprites` | `editor.eventControls_decorations` | 编译错误 |
| 3535, 3572, 3606 | `List<LevelEvent_MakeSprite>` 参数 | `List<LevelEvent_MakeDecorationBase>` | 编译错误 |
| 4231 (`GetSelectedSpriteList`) | 基于 `selectedSprite`+`spritesData` | 基于 `selectedDecoration`+`decorationsData` | 编译错误 |
| 3267, 3452, 3554, 3622, 808 | `eam.sprite.*` 本地化键 | `eam.decoration.*`（或保留键但改文案） | 本地化 |
| 4901, 4917, 4919, 4930（中）/ 5101, 5117, 5119, 5130（英） | `eam.sprite.*` 键定义 | 改名 `eam.decoration.*` | 本地化 |
| 4473 (`editor.sprites` 键) | 旧键 | `editor.decorations` | 本地化 |

**新增支持（非编译错误但功能缺失）**：
- `HandleAltEnter()` 快捷操作 switch：新增 `LevelEvent_Text`、`LevelEvent_SetText`、`LevelEvent_TintText`、`LevelEvent_SetFont`、`LevelEvent_AdvanceTextDecoration`、`LevelEvent_ReorderDecoration` 的 case（参考现有 `SetRowXs` 模式）
- 事件类型插入菜单（`EventTypeSelect` 虚拟菜单）：需包含新的装饰事件类型
- `propertiesInfo` 基类属性适配（见 5.3）

### 5.2 `RDLevelEditorAccess/IPC/FileIPC.cs`（~10 处）

| 位置 | 现状 | 修改 |
|---|---|---|
| FIPC:789-791, 1016, 1443 | 依赖 `propertiesInfo` 只含 DeclaredOnly 属性 | **适配继承链属性**：Helper 需正确处理基类属性（去重、只序列化叶子类 JSON 属性） |
| FIPC:825-829, 1174-1175, 1593-1604 | `GameSoundType` 引用 | 检查枚举值是否变化（`PulseSoundHoldP2` 等）——需验证 |
| FIPC:1646 | `LevelEventType.ChangePlayersRows` | 枚举值偏移（39→40），引用名不变则安全 |
| FIPC:1785 | `LevelEventType.AddOneshotBeat` | 枚举值偏移，引用名不变则安全 |
| FIPC:3386-3389 | `typeof(scnEditor).GetProperty("game")` 反射 | 验证 `game` 属性/字段在新版存在 |
| FIPC:3617-3657 | `RDSongOffsets`/`SongOffset` 反射 | 验证仍存在 |
| FIPC:1861-1881 | `ListedMethodAttribute` | 验证仍存在 |
| — | Sprite 事件相关 helper 逻辑 | 若引用 `LevelEvent_MakeSprite` 需改 `LevelEvent_Sprite`/`MakeDecorationBase` |

### 5.3 属性系统适配（关键行为变化）

`EditorAccess.cs:1230` 注释："evt.info.propertiesInfo 已经自动排除了基础属性（使用 BindingFlags.DeclaredOnly）"。新版 `LevelEventInfo.cs:59-69` 改为**遍历继承链收集 `[JsonProperty]` 属性**。影响：
- `LevelEvent_Sprite` 会多出基类 `LevelEvent_MakeDecorationBase` 的 `decorationId`、`depth` 属性
- `LevelEvent_SetText` 会多出 `LevelEvent_TextAttributesBase` 的 10+ 属性
- Helper 属性面板（`EditorForm.cs`）与 mod 的 `EditEvent` 属性过滤逻辑需：**去重（相同 JSON 属性名取子类）、保留基类可编辑属性**

### 5.4 Helper 端（`RDEventEditorHelper/EditorForm.cs`）

- 新增事件类型的属性面板（Text/SetText/SetFont/TintText/AdvanceTextDecoration）自动生成逻辑依赖 `propertiesInfo`——适配 5.3 后应自动工作，但需人工验证
- `SoundData` sentinel 逻辑（`__track_default__`/`__manual__`）不受影响

---

## 六、兼容计划（分阶段执行，待审核）

### 阶段 0：基线冻结（30 分钟）
- [ ] 备份当前 `EditorAccess.cs`、`FileIPC.cs`、`EditorForm.cs`
- [ ] 提交一个"重构前基线" commit（仅当用户要求）
- [ ] 记录游戏版本号确认（用反射脚本确认 GameDir DLL 的 Tab 枚举成员）

### 阶段 1：编译通过（核心目标，4-6 小时）
按"编译错误从少到多"的顺序修复，每修完一轮跑一次 build：

1. **类型重命名批改**（一次性机械替换，~20 处）：
   - `Tab.Sprites` → `Tab.Decorations`
   - `selectedSprite` → `selectedDecoration`
   - `spritesData` → `decorationsData`
   - `eventControls_sprites` → `eventControls_decorations`
   - `tabSection_sprites` → `tabSection_decorations`
   - `selectedSpritesTabPageIndex` → `selectedDecorationsTabPageIndex`
   - `currentPageSpritesData` → `currentPageDecorationsData`
   - `SpriteHeader` → `DecorationHeader`
   - `spriteId` → `decorationId`
2. **类型系统升级**：
   - `LevelEvent_MakeSprite` → 上下文判断用 `LevelEvent_Sprite`（新建）或 `LevelEvent_MakeDecorationBase`（容器/列表）
   - `AddNewSprite` → `AddNewDecoration`
3. **本地化键迁移**：`eam.sprite.*` → `eam.decoration.*`，`editor.sprites` → `editor.decorations`（并检查游戏本地化是否已含 `editor.decorations`，若无则在 RDStringPatch 中兜底）
4. **验证**：`dotnet build` 零错误；`lsp_diagnostics` 干净

### 阶段 2：功能适配（6-8 小时）

5. **Sprite 页签导航/朗读逻辑**：`HandleSpriteNavigation`（约 3447-3630 行区域）改为 Decorations 语义——朗读 `eam.decoration.info`、处理 `LevelEvent_Text`（无角色、显示 decoName）与 `LevelEvent_Sprite`（有角色）的分支
6. **插入事件虚拟菜单**：`EventTypeSelect` 菜单加入新装饰事件类型；`CreateEventAndEdit` 的类型→类名反射映射（`"LevelEvent_" + type`）自动适配新类名，但需加入 `LevelEvent_Text` 等的 `eam.eventtype.*` 本地化键
7. **`GetSelectedDecorationList`**：重写现有 `GetSelectedSpriteList`
8. **事件查找/过滤（Ctrl+F/F3）**：检查 `_filterRule` 中事件类型枚举是否需扩展
9. **快捷操作（Ctrl+Shift+Enter）**：为 Text 系事件增加 case（参考 SetRowXs 模式）

### 阶段 3：属性系统与 Helper 适配（4-6 小时）

10. **propertiesInfo 继承链适配**：新增去重逻辑（JSON 属性名在继承链中只保留子类定义）；验证 Helper 对新事件类型的属性面板
11. **Helper 反射安全**：所有 `Type.GetType("RDLevelEditor.LevelEvent_*")` 类名映射核对（`LevelEvent_MakeSprite` 不再存在）
12. **验证**：启动 Helper 打开 Text 事件、SetText 事件，确认属性齐全、可编辑、可保存回写

### 阶段 4：新增功能补全（4-8 小时，可选但推荐）

13. **新事件类型朗读**：`LevelEvent_Text`（文本内容朗读）、`SetText`/`SetFont`/`TintText`（属性朗读）、`AdvanceTextDecoration`（音节推进朗读）——沿用现有事件朗读模式
14. **`ICompatibleDecorationTypeChecker` 适配**：朗读"不兼容装饰"提示
15. **GoToLevel 事件**：纳入事件浏览/查找（普通玩法事件，非装饰页签）
16. **新角色 Cranky**：角色选择虚拟菜单 + 朗读

### 阶段 5：回归验证（2-4 小时）

17. **编译**：`dotnet build` 双项目零错误；Release 构建成功
18. **游戏内实测**（需 GameDir）：
    - 启动无报错（`LogOutput.log` 无异常）
    - 六个页签切换朗读正常（Song/Rows/Actions/Rooms/Decorations/Windows）
    - 装饰页签：方向键导航、插入 Sprite/Text 装饰、朗读事件数
    - 事件插入菜单含全部新类型
    - Helper 打开装饰相关事件属性正常
    - 旧存档（含 `"sprites"` key）可正常打开（游戏侧有版本迁移：`settings.version < 68` 时 `"Sprites"`→`"Decorations"`）
19. **回归清单**：对照用户手册 1-9 章功能逐项过

### 阶段 6：交付（1 小时）

20. `release.sh` 打包；更新 `changelog-cn.txt`/`changelog-en.txt`（新版本号、兼容游戏版本号）；更新 AGENTS.md（Sprouts→Decoration 架构变更）

---

## 七、验证方案（每阶段验收标准）

| 阶段 | 验收命令/动作 | 通过标准 |
|---|---|---|
| 1 | `dotnet build RDLE-a11y.sln` | 退出码 0，无 error |
| 1 | `lsp_diagnostics`（改动的 .cs） | 无 error |
| 2 | 游戏内装饰页签导航 | 无异常，朗读正确 |
| 3 | 手动 Helper 测试（构造 `temp/source.json`） | 属性面板完整、保存回写正确 |
| 4 | 游戏内插入/编辑各新事件类型 | 无异常，朗读合理 |
| 5 | `dotnet build -c Release` + `release.sh` | 打包成功，产物完整 |
| 5 | 游戏内全功能回归 | 对照手册逐项通过 |

---

## 八、风险与注意事项

1. **`LevelEventType` 枚举值偏移**：`AdvanceTextDecoration=39` 插入导致其后的所有枚举值 +1。mod 若存在硬编码 int 值（非枚举名引用）会静默错乱——需全局排查 `(int)` 转换
2. **`propertiesInfo` 行为变化是隐性炸弹**：编译不报错但运行行为改变（Helper 属性列表变多），是最容易漏掉的适配点
3. **本地化键缺失**：bundled 本地化文件仍只有 `editor.sprites`，无 `editor.decorations`。若游戏实际本地化已更新（bundled 是旧快照）则无忧；否则需 RDStringPatch 兜底
4. **不保证向后兼容**：计划按"完美兼容最新版"执行——旧版游戏的兼容性不在本次范围内（用户明确要求）
5. **`LevelEvent_Text` 装饰的朗读**：文本装饰无角色概念，`GetSpriteDisplayName` 需分叉（Sprite→角色名，Text→decoName）
6. **装饰添加流程变化**：`DecorationHeader.AddButtonClick` 打开事件选择面板而非直接建精灵——mod 的"插入装饰"虚拟菜单交互需重新设计以匹配
7. **存档兼容**：游戏侧已有 `version < 68` 的 `"Sprites"`→`"Decorations"` 迁移逻辑，旧存档可开；但**新版存档含新事件类型**，mod 浏览旧代码路径时需容错

---

## 九、附：关键文件定位（供实施时查阅）

- 新版装饰系统：`agents references/Assembly-CSharp/RDLevelEditor/` 下 `TabSection_Decorations.cs`、`DecorationHeader.cs`、`LevelEvent_MakeDecorationBase.cs`、`LevelEvent_Sprite.cs`、`LevelEvent_Text.cs`、`LevelEvent_TextAttributesBase.cs`、`LevelEvent_SyllableBase.cs`、`LevelEvent_SetText.cs`、`LevelEvent_SetFont.cs`、`LevelEvent_TintText.cs`、`LevelEvent_AdvanceTextDecoration.cs`、`LevelEvent_ReorderDecoration.cs`、`LevelEvent_GoToLevel.cs`、`LevelEventControl_Decoration.cs`、`ICompatibleDecorationTypeChecker.cs`、`MakeDecorationInspectorPanel.cs`、`GoToLevelAction.cs`、`OGGConverter.cs`
- 新版 scnEditor：`scnEditor.cs`（`decorationsData` :352、`selectedDecoration` :364、`currentPageDecorationsData` :723、`AddNewDecoration` :3348、`CreateEventControl` 工厂 :2134、`AddNewEventControl` 装饰分支 :2167-2192）
- 枚举：`Tab.cs`（`Decorations=4` :10）、`LevelEventType.cs`（Sprite=60、Text=61、ReorderDecoration=79、GoToLevel=86 等）
- 序列化：`RDLevelData.cs`（`decorations` :25、key :353）、`RDEditorConstants.cs`（`DecorationsKey` :49、事件类别 :1152-1184）
- 兼容接口：`LevelEvent_Base.cs`（`isDecorationTabEvent` :181、`ownerDecoration` :193-207、旧档迁移 :402-411）
