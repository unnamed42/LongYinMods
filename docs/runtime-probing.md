# 运行时探查与崩溃排查

> 归属：[`AGENTS.md`](../AGENTS.md) §7 的详细展开。
> 有活进程就别猜日志 —— 本文件讲 MCP 探查与崩溃取证。
> 本文件是**使用**视角（工具清单 + 使用注意）。

---

## 0. 该看哪一节

| 你的问题 | 去哪 |
|---|---|
| 该用哪个 MCP 工具 | §7.1（工具清单 + 每个工具的行为与注意） |
| 代码写了却不生效 | `AGENTS.md` §5 排查顺序 + 本文 §7.3 生命周期（**「补丁挂上」≠「跑过」**） |
| 崩了 / 卡死 / 进程不退出 | §7.4（minidump → 栈回溯） |

---

## 7. 运行时探查

静态分析到极限时，用运行时手段定论。**有活进程就别猜日志。**

### 7.1 Unity MCP（MelonMCP）★ 推荐

**可以对运行中的游戏执行 C# 表达式、直接读写活对象** —— 排查效率远高于读日志。

- **核心工具**：`execute_csharp` / `evaluate_expression` / `find_objects_of_type` / `list_game_objects` / `get_type_info` / `list_types` / `list_assemblies` / `read_logs`。
- **排查补丁用的工具**（2026-10 新增）：`hook_patch_info`（补丁挂载/触发/生效 + patcher 类型 + 入口字节）、`list_patches`（全进程补丁清单，含其他 mod）、`disasm` / `read_mem` / `resolve_jump`（**运行时**字节与跳转解析）、`watch_field` / `unwatch_field`（轮询字段变化）。
- **配置读写工具**：`list_configs` / `get_config` / `set_config` / `reset_config`（读写 MelonLoader 偏好设置，见 §7.1.1）。
- **批量读字段**：`inspect_unity_object`（按类型枚举实例 + 读点号路径字段，见 §7.1.2）。
- **日志分组**：`read_logs` 带 `group_by="prefix"` 时返回**按频率排序的分组计数**，用于判断「哪条日志在刷屏」（见 §7.1.3）。
- **UI 按钮状态**：`dump_menu_state`（枚举按钮 + 三个状态标志，见 §7.1.4）。
- **持久化的知识库**：`get_game_knowledge` / `add_game_knowledge`。
  ⚠️ **它只用于存「游戏本身的知识」**（世界观、设定、数值规则、游戏机制这类**与 mod 开发无关**、
  且**游戏更新也大体不变**的内容）。
  **不要往里写开发侧的东西** —— 工具用法、构建配方、反编译流程、踩坑、地址/字段偏移、
  调用链结论一律**不属于**它：
  - 通用工具/构建/API 知识 → **本文件 `AGENTS.md`**
  - 具体游戏的地址 / 字段偏移 / 调用链 / 实测行为 → **`docs/<project>.md`**

  两个理由：①落盘在 `gamedir/UserData/MelonMCP/game_knowledge.json`，**在游戏目录里、不在仓库里**，
  不随 git 同步，换机 / 校验 / 重装即丢；②别的 agent 读 `AGENTS.md` / `docs/` 时根本看不到它。
  它只保证**跨 MCP session** 可见，**不保证跨仓库/跨机器**。

#### 7.1.0 `execute_csharp` 的能力边界与语言版本

**`execute_csharp` / `evaluate_expression` 的实际能力边界**（引擎为内嵌的 Mono.CSharp）：

- ✅ **支持**：运算符与字符串拼接、`new` 对象构造、索引器、`foreach`、LINQ
  （`Where`/`Select`/`OrderBy`/`ToArray`）、`string.Join`、`typeof`、泛型类型、带局部变量的多语句。
- ✅ **会话状态跨调用保持**（变量 / using / 自定义类型）；`execute_csharp` 传 `reset=true` 清空。
- ❌ **不支持 `return x`** —— 见下方“注意事项”表（用裸尾表达式）。

**语言版本：约 C# 7.1（不是 7.2）—— 已实测，2026-10**

该工具的描述曾写「up to 7.2」，那是从 McsMCP 抄过来的。**那一句不可迁移**：
McsMCP 嵌的是 `lib/net35/mcs.dll`（跑在 net472），我们嵌的是 net6 构建。
（两份的 `LanguageVersion` 枚举完全相同，都是 `ISO_1…V_7, Default, V_7_1, V_7_2, Latest, Experimental`
—— 即**语法上限一样**，差别只在运行时绑定。）

**实际情况是双向都不准**，逐项在本进程里跑过：

| 特性 | 版本 | 结果 |
|---|---|---|
| 元组 / `out var` / `is T x` / 数字分隔符 | 7.0 | ✅ |
| `private protected` | 7.2 | ✅ |
| **`in` 参数** | 7.2 | ❌ 不解析 |
| **局部函数** | 7.0 | ❌ 不解析 |
| **switch 表达式** / using 声明 | 8.0 | ❌ |
| `case int i:` switch 类型模式 | 7.0 | ❌ 内部编译器错误 |
| LINQ 查询语法（`from … select`） | 3.0 | ❌ |

**要点**：报告版本号不如报告**具体能否用**。写成「7.1，7.2 只有 `private protected`」，
比「7.2」准确 —— 后者会让人写出局部函数然后撞墙。

> 会话的 `CompilerSettings.Version` 实际是 `Experimental`（枚举最大值），**但它并不代表支持 C# 8**：
> 上面 `switch` 表达式与 using 声明都失败了。**别用这个枚举值推断能力。**
> ✅ **曾经的「静默失败」已修（2026-10）。** 如果你的会话里模型还在说「拆成两句」，
> 那是旧经验。

⚠️ **已禁用的工具（不要再尝试，它们会让 MCP 客户端看到一个名字却永远失败）**：

| 工具 | 禁用原因 |
|---|---|
| `list_components` / `inspect_component` | IL2CPP 下组件列表塌缩为 `UnityEngine.Component` 代理，所有组件都报成 `Component`，`inspect_component` 永远匹配不到类型名 |
| `toggle_behaviour` / `set_property` / `invoke_method` | 同上：靠 `GetComponents` + `GetType().Name` 定位目标组件 |
| `find_game_object` | 受 `instanceId` 分支牵连（`path` 分支本身可用）；改用 `execute_csharp` + `UnityEngine.GameObject.Find` |
| `inspect_material` | 靠 `GetComponent(gameObject, "Renderer")` 拿 renderer，永远报 `No renderer found` |
| `take_screenshot` | 该 IL2CPP/CoreCLR 运行时下 `Texture2D` + `ReadPixels` 路径返回 null |

实现在源码里保留了，注册处用 `#if MELONMCP_ENABLE_BROKEN_INSPECTION_TOOLS` / `#if MELONMCP_ENABLE_BROKEN_SCREENSHOT` 关掉——**修好后开宏即可，不要重写**。

#### ⚠️ 这批工具的共同死因：**靠反射通用地发现**

上面几条失败原因不一样，但**根因是同一个**：它们都在试「自动发现组件 / 属性」，
而这在 IL2CPP 下会**塌缩** —— 所有组件都报成 `UnityEngine.Component`，
反射枚举属性又全装箱成 `Il2CppSystem.Object`，读不出值。

**`inspect_unity_object` / `dump_menu_state` 能工作，正是因为它们反其道而行：**

| 已禁用的做法 | 能工作的做法 |
|---|---|
| 自动发现组件 / 属性 | 调用方**显式给出** `typeName` + `fields` |
| `GetComponents`（塌缩） | `FindObjectsOfTypeAll<T>` / 非泛型重载（已验证可用） |
| 一处失败 → 整个工具失效 | 每字段 / 每实例独立 try/catch |

> 🚫 **不要把这两个工具「改进」成自动字段发现器。**
> 那个约束不是实现细节，**它就是工具能工作的原因**。
> 真要修那批禁用工具，是**独立议题**（得先解决 IL2CPP 代理塌缩），不要和它们混在一起。

仍有部分工具**只有 `instanceId` 分支是死的**（`path` 分支正常）：`instantiate_object` / `set_transform` / `destroy_object`。它们依赖 `UnityHelper.FindObjectByInstanceId`，后者依赖一个在 IL2CPP 下解析为 null 的非泛型 `FindObjectsOfType` 反射查找。**用 `path` 寻址，别传 `instanceId`。**

**注意事项（踩过的坑）**：

| 现象 | 原因 / 对策 |
|---|---|
| **表达式里不能用 `return x`** | 交互式宿主方法返回 `void`，`return x` 报 `error CS0127`。**用裸尾表达式**：`var n = "x"; "hello " + n` |
| ~~一整条超长单行表达式静默无结果~~ | ✅ **已修**（2026-10），见下方「结果分类」 |
| 先定义 `class` 再写语句会报 `CS1525` | ✅ **已支持**（2026-10）—— 一个 snippet 可含多个「提交」，见下方同名小节 |
| `Count` 报错 | 它是**方法**不是属性 → 写 `.Count()` |
| `FindObjectsOfType<T>()` 找不到游戏数据类 | `GridUnitData` / `BattleUnit` 之类**不是 `UnityEngine.Object`**；要经游戏自己的容器取（如 `BattleController.battleMapData.GetGridData(r, c)`） |
| `FindObjectOfType<Il2Cpp.Xxx>()` 报 `Method unstripping failed` | 改用 `FindObjectsOfType<MonoBehaviour>(true)` 按 `GetType().Name` 过滤 |
| 偶发卡顿 / 失败 | 网络问题，**直接重试** |
| 局部变量重名 | `CS0136` —— 会话状态跨调用保持，换个名字 |
| lambda 语句体里带 `new object[]{...}` | 编译失败，拆开写 |

#### 7.1.1 配置读写：`list_configs` / `get_config` / `set_config` / `reset_config`

让 agent **直接改 mod 配置**，不用手改 cfg，也不用开图形界面（agent 开不了 F5 窗口）。

**建在 `MelonPreferences` 上，不依赖 `MelonPreferencesManager`。** 后者是给人用的游戏内 UI；
工具靠它就会在任何别的 MelonLoader 环境失效。`MelonPreferences` 是 MelonLoader 本体的一部分。

⚠️ **不要直接编辑 `MelonPreferences.cfg`。** MelonPreferences 把全部条目留在内存里、
**整体重写**该文件。游戏运行时手改 cfg，会在下一次任何 mod（或用户）调 `Save()` 时
**被内存里的值覆盖 —— 静默丢失**。必须走接口写。

实测确认的语义（MelonLoader 0.7.3）：

| 事实 | 说明 |
|---|---|
| `MelonPreferences.Categories` | 是 `List<MelonPreferences_Category>`，**不是 Dictionary**（用 Dictionary 强转报的是无信息的 NRE） |
| category 的形状 | `Identifier` 是**属性**，`Entries` 是**字段** —— 两者不一致 |
| entry 的值 | 非泛型基类 `MelonPreferences_Entry` 上有 `BoxedValue`，**可读可写**，是唯一能走泛型的入口 |
| 写入生效范围 | `BoxedValue` / `SetEntryValue` **只改内存**，**不落盘** |
| 落盘 | 必须显式 `MelonPreferences.Save()`；它**保留注释、不丢其它 category** |

**`set_config` 要求 `confirm == "<mod>.<key>"`** —— 防止误写落盘（实测能拦住不匹配的 confirm）。

#### ⚠️ 工具不判断「是否需要重启」，这是故意的

值在**读取点**被读（`entry.Value`）→ 立即生效。
值在 **`OnInitializeMelon` 里被消费**（典型：任何**装原生 hook / Harmony 补丁**的开关）
→ **已经烧进进程，改了要冷启动**。

只有各个 mod 自己知道属于哪种，**猜错比不猜更糟**：它正好会复现本项目反复踩的坑
（§3.2.1）—— 读回 `true` 就以为生效了。所以 `set_config` 如实返回
`restartRequired: "unknown - see tool description"`，并解释两种情况的区别。

> 典型例子：`FriendlyNoclip` 的 `wall_native_hooks` / `wall_pass_hook` 控制的是
> `OnInitializeMelon` 里一次性安装的原生 detour —— **改了值不会卸载已装的 hook**。
> 而 `diagnostics` 在每次用的时候读 `.Value`，**改了立即生效**。

#### 7.1.2 `inspect_unity_object` —— 按类型批量读字段

一条调用替代「手写 `foreach` + 空值保护 + 拼字符串」。用法：

```
inspect_unity_object {
  typeName: "AreaBuildingIconController"     // 必填，短名即可（走 TypeResolver）
  fields:   ["buildingData.buildingID", "buildingData.lv"]   // 点号路径，逐段解析
  count:    20        // 默认 20，上限 200
  where:    "buildingData.buildingID=-1"     // 按字段值过滤
}
```

**失败隔离是它的核心价值**：某字段/某实例读失败 → 该格显示 `<err:...>`，
**其余列、其余实例照常返回**。一个坏对象不会让整批失败。

##### ⚠️ 为什么它不会重蹈被禁用工具的覆辙

`list_components` / `inspect_component` / `toggle_behaviour` 那批被禁用的工具，
共同死因是**靠反射通用地发现**组件与属性 —— IL2CPP 下代理类型塌缩成
`UnityEngine.Component`，反射枚举属性全装箱成 `Il2CppSystem.Object`，读不出值。

本工具**反过来**：`typeName` 与 `fields` 都由调用方**显式给出**，不做任何自动发现。

> 🚫 **不要把它「改进」成自动字段发现器** —— 那就退回到已被证明失败的做法了。

##### ⚠️ 两个 IL2CPP 陷阱（都实测踩过）

**1. 必须用泛型重载 `FindObjectsOfTypeAll<T>()`，不能用非泛型的。**

非泛型重载声明的返回类型是 `Il2CppReferenceArray<Object>`，于是
**只有 index 0 携带具体代理类型，从 index 1 起全部退化成 `UnityEngine.Object`**：

```
非泛型重载取回的 Transform 实例：
  [0] managed=Transform   actual=RectTransform
  [1] managed=Object      actual=RectTransform   ← 塌缩
  [2] managed=Object      actual=RectTransform
```

症状很误导：`name` 能读（`Object` 上也有），`position` 读不到 →
报 `no member 'position' on Object`，**看起来像字段名写错，实际是实例问题**。

泛型重载的元素类型是 `Il2CppReferenceArray<T>`，**每个元素都保留具体类型**。
运行时类型已知时用 `MakeGenericMethod` 调用即可（**一次调用**的反射开销可忽略）。

**2. `where` 的布尔比较要不区分大小写。**

布尔渲染成 `true`/`false`（C# 惯例），而调用方/JSON 参数自然写成 `True`。
严格比较会**静默选不出任何实例** —— 与「确实没有该值的实例」无法区分。

##### 与 `GridUnitData` 这类非 Unity 类型的关系

`GameObject` / `Transform` / `Component` 这类 `UnityEngine.Object` 子类可以枚举；
`GridUnitData` / `BattleData` 之类**不是** Unity 对象，`FindObjectsOfTypeAll` 不接受，
工具会给出明确拒绝而不是空结果。要取那些得走游戏自己的容器
（如 `BattleController.battleMapData.GetGridData(r, c)`）。

#### 7.1.3 `read_logs` 的分组统计 —— 回答「哪条日志在刷屏」

`read_logs` 的 `filter` 是「**筛选后逐条返回**」，回答不了「**各类分别多少条**」。
要判断该关掉哪个诊断项，需要的是**计数**：

```
read_logs { group_by: "prefix", count: 1000, limit: 20 }
```

```
Log groups by prefix  (8 group(s) over 173 line(s))

51  探针·图标点击
34  探针·Shift 点击
30  探针·锤子切换
28  Shift+单击升级
27  探针·地块点击
```

这一步以前只能**绕过 MCP 去 shell 里 `grep | sort | uniq -c`**。

##### ⚠️ 分组键怎么取（两个坑都能让结果变成废数据）

日志行有两种形态，取错那一个**不会报错，只会给出无用的结果**：

```
[07:50:17] [MelonMCP] Registered tool: read_logs          ← 一个 tag（logger 名）
[07:50:25] [WARNING] [MelonMCP] [探针·图标点击] 第 1 次    ← 两个 tag
```

1. **必须忽略时间戳与 level**，否则每行都是独立一组。
2. **两个 tag 时要取第二个**。第一个 `[MelonMCP]` 是这个 mod 打的**所有**日志共用的
   logger 名 —— 拿它当键会把所有消息合并成一组，输出「1 组 N 行」，
   **看起来像正常结果，实际啥也没回答**。
3. **只有一个 tag 时不能直接用那个 tag**，要退到**消息正文**。否则同上的塌缩：
   `[MelonMCP] Could not subscribe...` 与 `[MelonMCP] no tag here 1` 会被并成一组。
4. **序号必须归一化**。`[探针] 第 32 次` / `第 33 次` 不抹掉数字就变成 32 个组、每组 1 条 ——
   这是本工具「看起来在工作但没在工作」的第二种形态。已处理：
   `第 N 次`、`#N`、`count=N`、`N times`、行尾 `(N)`。

> ⚠️ 归一化**故意不粗暴替换所有数字** —— `item 32` / `item 45` 可能是有意义的不同消息。
> 只处理**计数器形态**的出现。

##### ⚠️ 日志缓冲的已知局限

`read_logs` 读的是 MelonMCP 自己的缓冲，而它**只装得下订阅成功的事件**。
本 MelonLoader 构建下 Msg 通道**绑定失败**（启动日志里有
`Could not subscribe to Msg logs via reflection`），所以 **WARNING / ERROR 可靠，
普通 Msg 可能缺失**。

→ **「这里没有」不等于「从来没打过」**。排查时优先用 `Warning`/`Error` 打日志，
或直接读磁盘上的 `MelonLoader/Latest.log`。

#### 7.1.4 `dump_menu_state` —— 枚举 UI 按钮与状态

一条调用替代「手写 `FindObjectsOfType<Button>` + `GetComponentsInChildren<Text>` 循环」，
输出每个按钮的三个状态标志：

```
4 buttons:
  [拆除]     enabled=false interactable=true  activeInHierarchy=true  sibling=0
  [取消拆除] enabled=true  interactable=true  activeInHierarchy=true  sibling=3
```

##### ⭐ 为什么要读**三个**标志

**残留按钮的判据是 `enabled=false`，而 `interactable` 与 `activeInHierarchy` 仍是 `true`。**

只看 `interactable` 或只看 `activeInHierarchy` 都会把残留按钮判成「可用」——
这正是本项目那次排查的核心发现，也是这个工具存在的理由。
`usableCount` 字段按「三个都为 true」统计，避免调用方各读一个。

##### ⚠️ 实例来源差 **100 倍**，必须显式选

| API | 实测数量 | 含义 |
|---|---|---|
| `Object.FindObjectsOfType<Button>()` | **9** | 只在**活动场景**里的 |
| `Resources.FindObjectsOfTypeAll<Button>()` | **951** | 另外包括**非活动对象**与**其它已加载场景** |

两者都对，只是回答不同问题。工具用 `includeInactive`（**默认 false**）暴露这个选择，
而不是偷偷替你选一个。

实测 `includeInactive=true` 时，前 500 个里 **usable=0** —— 那 942 个是池化 / 预制体 /
其它场景的对象，**不是当前菜单**。当菜单 dump 用只会淹没结果。

##### ⚠️ 必须**一次性**枚举 + 读取

部分菜单的内容在游戏自己的 `Update` 里重建。分两次 MCP 调用读「按钮集合」和「各自状态」，
实际比较的是**两个不同的集合** —— 本项目实测出现过「上次 4 个按钮、下次 1 个」的
自相矛盾读数，**看起来像数据问题，其实是时序问题**。

工具内部先把集合与状态一起取完再返回，从根上消除这一类误判。

##### 用法要点

- `rootPath`：限定到某个面板的后代（如 `Canvas/MainMenu`）。**`GameObject.Find` 只匹配活动对象**，
  所以非活动面板无法这样寻址 —— 那种情况直接省略 `rootPath` 并配 `includeInactive`。
- `labelOnly`：只看有文字标签的。默认 false —— 无标签的按钮**仍是按钮**，静默丢掉会掩盖状态。
- 每个按钮独立 try/catch：池化对象里有已销毁的，一坏一格不影响整批
  （实测扫全部 951 个，`readErrors` 为 0）。

#### `execute_csharp` 的结果分类（编译失败 / 运行时异常 / 无值）

**编译失败 / 运行时异常 / 无值，这三者必须能分辨** —— 混在一起会让人在**正确的代码**
里找不存在的 bug（`AGENTS.md` 纪律 1）。现在它们确实是分开的：

| 结果文本 | MCP `isError` | 含义 | 你该做什么 |
|---|---|---|---|
| 值 / 文本 | false | 成功，有返回值 | — |
| `Executed successfully; no value returned. ...` | false | 成功，但**没值** | 正常。要值就加**裸尾表达式** |
| `Compilation failed:` + `(行,列): error CS....` | **true** | **编译失败，一行都没执行** | 改代码 |
| `Execution failed: ...` | **true** | 跑起来了，抛异常 | 看栈 |

> ⚠️ **`Console.WriteLine` 的输出从来不会被捕获** —— `_diagnostics` 是
> **编译器**的 report printer（错误/警告），不是运行时的 stdout。
> 用 `Console.WriteLine` 调试本就不行，得用**裸尾表达式**返回值。
> （这条容易被误为是同一个 bug，但两者无关。）

#### 声明与语句可以写在同一次调用里

`class Foo { ... }` 后面直接跟 `Foo.Bar()` **同一次调用就行**，不必拆两次 ——
早先这样会报 `CS1525`，现已支持（一个 snippet 可以含多个「提交」）。


#### ⚠️ `execute_csharp` 会**把游戏搞崩**：栈只有约 82 KiB

**这条比缺工具重要** —— 它能让整个游戏进程消失，且**无法捕获**。

实测：REPL 跑在 Unity 主线程上，栈约 **82 KiB**。在 `execute_csharp` 里做
**重反射**会**栈溢出** —— 命中 guard page，原生代码抛 `EXCEPTION_STACK_OVERFLOW`，
CoreCLR 不处理，**直接 abort**：无异常、无日志、无 coredump（除非开了 minidump）。

**本项目已因此崩过两次**，两次都是同一个模式：

| 场景 | 结果 |
|---|---|
| GC 堆遍历回调里调 `il2cpp_object_get_class` + 反射取类名 | 崩 |
| 循环里 `MakeGenericMethod` / 反射遍历类型 | 崩 |

两次都靠 `tools/il2cpp_unwind.py` 从容确认：**主线程栈用量 = 总量**。

**纪律：重反射写进 mod 源码（编译后的 C# 有正常栈），不要在 `execute_csharp` 里跑。**
必须用运行时泛型时，**一次只做一个**，确认返回后再做下一个。

> ⚠️ **catch 不住。** 这是原生栈溢出，不是托管异常 —— `try/catch` 无效。
> `execute_csharp` 的 `timeout` 参数**也拦不住**（它目前未被使用，且执行在主线程上，
> 无法安全中断）。只能靠预防。

### 7.2 日志与文件路径

- **日志**：`gamedir/MelonLoader/Latest.log`
- **配置落盘**：`gamedir/UserData/MelonPreferences.cfg` —— ⚠️ 若源码**没有**调 `SetFile`，所有 `[Category]` 都写在**这一个文件**里，不要去找 `UserData/<ModName>.cfg`。MelonPreferences **不会**自动删除已废弃的键（僵尸键无害，但别被骗）。
- **写文件必须用** `MelonLoader.Utils.MelonEnvironment.GameRootDirectory`。**不要**用 `AppContext.BaseDirectory + "..\\.."` —— 在 Proton 下相对回退会失败（本项目踩过）。

### 7.3 MelonLoader 0.7.3 生命周期

- `OnApplicationStart()` 在该版本是 `[Obsolete(error: true)]` → 用 **`OnInitializeMelon()`**；teardown 用 `OnDeinitializeMelon()`。
- `UnregisterInstance` 顺序：`OnDeinitializeMelon()` → `UnregisterInternal()` → … → **`HarmonyInstance.UnpatchSelf()`**。
  → **插件不需要自己撤销 Harmony 补丁**，MelonLoader 会做。teardown 只需释放非 Harmony 资源。
- `MelonLogger` 的静态事件（`MsgCallbackHandler` / `WarningCallbackHandler` / `ErrorCallbackHandler`）若不退订会**钉住程序集、阻止 ALC 回收**。
- ✓ **`OnDeinitializeMelon` 在「热重载」和「真退出」两种情况下都会被调用**，且**无法从它自身区分**。
  这曾导致 MelonMCP 在退出时**过早关掉监听端口** —— 后果见下。

#### 「真退出」与「热重载」的区分（反编译 MelonLoader 0.7.3 核实）

调用链（反编译核实）：

```
SupportModule_From.DefiniteQuit()          // 引擎确定要退出了，不可取消
  → MelonEvents.OnApplicationDefiniteQuit.Invoke()
  → 按 priority 顺序依次回调所有订阅者：
      ① MelonAssembly.OnApplicationQuit()   ← 卸载：UnregisterMelons → OnDeinitializeMelon
      ② （其它 mod 的回调）
  → 回到 DefiniteQuit：Core.Quit()          // ← 卸载之后才真正退
```

⚠️ **注意两个不同的事件**（名字像，语义不同）：

| 事件 | 语义 | 可否取消 |
|---|---|---|
| `MelonEvents.OnApplicationQuit` | **请求**退出 | ✅ 可取消 |
| `MelonEvents.OnApplicationDefiniteQuit` | **确定**退出 | ❌ 不可取消 |

##### ⚠️ 真正的坑：订阅顺序由 **priority 决定**，不是「谁先订阅谁先跑」

`MelonEventBase<T>.Subscribe(action, priority=0, ...)` 的插入规则（反编译原样）：

```csharp
for (int num = 0; num < actions.Count; num++)
    if (a.priority < melonAction.priority)   // ← 严格小于才插到前面
    { actions.Insert(num, a); return; }
actions.Add(a);                              // ← 同 priority：追加到末尾 → 后执行
```

`Invoke()` 按这个数组顺序依次调用。而 **`MelonAssembly` 订阅时用的是默认 priority = 0**。

> ❌ **本项目真实事故**：MelonMCP 想用 `OnApplicationDefiniteQuit` 置一个「真退出」标志，
> 以在 teardown 里决定「保活 MCP」还是「释放端口」。代码写的是
> `Subscribe(OnDefiniteQuit)`（默认 priority 0），而它**在 `OnInitializeMelon` 里才订阅**，
> 晚于 MelonAssembly ⇒ **排在 MelonAssembly 之后** ⇒ 标志位在
> `OnDeinitializeMelon`（即服务器已被关掉）**之后**才置上 ⇒ **保活逻辑从未执行**。
>
> 危害在于**完全静默**：mod 正常启动、正常记日志、正常退出，只是那条保活分支永远走不到。
> 日志里能看到的唯一线索是：期望的两条日志**一条都没有**，而 `Server stopped` 出现了。

✅ **正确做法：给一个负 priority，插到 MelonAssembly 之前**

```csharp
MelonEvents.OnApplicationQuit.Subscribe(OnDefiniteQuit, -1);          // 请求退出（最早）
MelonEvents.OnApplicationDefiniteQuit.Subscribe(OnDefiniteQuit, -1);  // 确定退出
```

`-1 < 0` ⇒ 走 `Insert(0, ...)` ⇒ 先于 MelonAssembly 执行。

**为什么同时订阅两个事件**：`OnApplicationQuit`（请求，可取消）最早触发，
`OnApplicationDefiniteQuit`（确定）随后。**任一个到来都足以断定进程要走了** ——
即使请求退出后来被取消，代价也只是端口多监听一会儿（进程很快就没了），
而**漏判的代价是丢光诊断能力**。这个不对称性决定了应该偏保守。

> 💡 **教训（写这类钩子时的通用纪律）**：只要回调顺序会影响正确性，
> 就**不能依赖订阅先后**，必须显式指定 priority；并且**启动时把顺序读回来自检**
> （`MelonEventBase.GetSubscribers()` 返回的就是真实调用序）。
> **静默失效比报错危险得多** —— 它会让你在正确的代码里找不存在的 bug。
> MelonMCP 的 `VerifyQuitSubscriptionOrder()` 即此自检：
> 启动时确认自己的回调确实排在 MelonAssembly 之前，否则**大声报错**。

> 💡 **为什么值得在意**：退出路径本身可能是坏的。若 exit hang 的原因在 `Core.Quit()` 之后
> （IL2CPP teardown），那么在 `OnDeinitializeMelon` 里就把调试工具全关掉，等于
> **在事故现场卸掉监控**。MelonMCP 现在遇到 definite quit 会**保持端口监听**，
> 让 `read_logs` / `main_thread_status` / `disasm` 在退出期仍可用。

> ⚠️ **`OnUpdate` 里的 `_deinitialized` 早退会连看门狗一起停掉**。心跳必须放在早退之前，
> 否则 `main_thread_status` 在退出期会报一个**永远不动的计数器** —— 把「主线程卡死」和
> 「心跳被自己关了」搞成同一个读数。

#### IL2CPP 退出卡死：官方定性

这是 **Unity 已知问题**，非本项目特有：[Player build freezes after calling Application.Quit() when the scripting backend is set to IL2CPP](https://issuetracker.unity.com/issues/8178/player-build-freezes-after-calling-applicationquit-when-the-scripting-backend-is-set-to-il2cpp)
（特征：**Mono 后端正常、IL2CPP 卡死**，2019→2022 多版本复现）。

机制（Unity 工程师 [JoshPeterson 的解释](https://discussions.unity.com/t/background-threads-cause-app-to-hang-on-ios-with-il2cpp/890948)）：

> At shutdown, the IL2CPP runtime will attempt to cause all threads (background or not) to exit.
> Threads that are executing managed code should exit properly. **But if a given thread is executing
> native code, that might be a problem**, as the native code could be involved in some kind of a
> blocking call into the OS, and the IL2CPP runtime won't be able to stop that thread.

**判定特征**：进程 `S` 态、**CPU 归零**、日志已走到全部 mod 卸载完，但进程不退。
→ 用 `eu-stack -p <pid>` / `gdb -p <pid>` 看谁还 attach 着。
**本项目实例**：`WuMingPerformance` 的 `WuMingPerf-WorldWorker` 线程
`il2cpp_thread_attach` 过、永久阻塞在 `WaitOne()`、**从不 `Dispose`（即从不 detach）**。

### 7.4 崩溃排查

- **游戏崩溃时 MelonLoader 来不及写日志** → 用 coredump 或**二分隔离**。
- **二分隔离比读代码快得多**：把可疑功能各配一个开关，逐项关掉观察是否还崩。
- **附加 gdb**：沙箱默认是 `bwrap --ro-bind / / --dev /dev --unshare-pid`，**PID 命名空间隔离**（只能看到几个 PID）—— 这是**命名空间问题不是权限问题，`sudo` 无用**。需要 `danger-full-access` 级别的提权才能看到宿主机进程。
  > ⚠️ 注意：**读** gamedir 下的日志/dump **不需要**提权（见 [AGENTS.md §3.2](../AGENTS.md)）；
  > 只有 gdb 这类需要跨 PID 命名空间的操作才要。
- gdb 会在 .NET 常规信号（SIGUSR1/SIGUSR2）上误停 → 必须加 `handle SIGUSR1 nostop noprint pass`（及 SIGUSR2/SIG32/SIG33/SIGPIPE）。
- ⚠️ **`GameAssembly.dll` 只映射头部一页，真正的代码在匿名 `r-xp` 区**（约 24MB）→ 按 `r-xp` + 大小筛选候选区。
- **VA→文件偏移映射**：`file_off = VA - 0x180000000 - 0xc00`（PE 头 + 节对齐差）。直接用 `VA - 0x180000000` 会读错字节。

#### 首选：让 .NET 自己产出 minidump（已验证可用）

实测有效，**不用装任何东西、不用写代码**。在 Steam 启动选项里设：

```
DOTNET_DbgEnableMiniDump=1 DOTNET_DbgMiniDumpType=1 DOTNET_DbgMiniDumpName="S:\coredump\crash_%t.dmp" DOTNET_EnableCrashReport=1 %command%
```

- 环境变量要写在 `%command%` **前面**；Proton 直接继承父进程 env（`proton` 里 `self.env = dict(os.environ)`）
- **`S:` 是游戏目录**（Proton 把游戏盘映射为 `S:`）→ `S:\coredump\` 对应 `.../SteamLibrary/coredump/`，比 `C:` 好找得多
- **不指定 `DOTNET_DbgMiniDumpName` 时默认落在 `%TEMP%`**（`createdump` 内部调 `GetTempPath2A`），**不是** CWD
- 占位符：`%p`=pid、**`%t`=Unix 时间戳**（推荐，比 pid 好排序）、`%e`=exe 名
- `createdump.exe` 在 Wine 下**确实可用**（实测产出合法 `MDMP`，8 个流，85 线程 + 301 模块）

#### 离线回溯：`tools/il2cpp_unwind.py`

```bash
tools/il2cpp_unwind.py /run/media/.../coredump/crash_<ts>.dmp [--all-threads]
```

**为什么 gdb 不行**：IL2CPP 编译的 x64 代码**不用 frame pointer**，`rbp` 常是堆指针，gdb 的 `bt` 只能打印 `#1 0x2 in ?? ()`。
**正确做法**：x64 Windows 用 **`.pdata` 表驱动回溯**（`RUNTIME_FUNCTION` → `UNWIND_INFO`）。本二进制有 **104663** 条；脚本已实现解析 + 回溯 + 符号化。

**写这类工具的三个坑**（都真踩了）：

1. ⚠️ **`os.path.basename` 在 Linux 上不切 Windows 反斜杠路径**。minidump 里模块名是 `S:\...\GameAssembly.dll`，要手写 `name.replace('\\','/').rsplit('/',1)[-1]`。
2. ⚠️ **「在模块内」的边界必须是模块真实大小**，不能用很大的常数（如 `0x100000000`），否则越界地址被当成模块内、产生假帧。大小从 `ModuleList` 取。
3. ⚠️ **二分查找要防空集回绕**：`bisect_right` 对**小于**最小元素的值返回 `i=0`，其前驱是最后一个元素 → 看似命中实则荒谬。先判 `rva < sorted[0]`。

**符号化只能命中 `il2cpp` 节**：`script.json` 地址范围 `0x20F000..0x18A3E10`，起点恰是 `il2cpp` 节开头。**`.text`（`0x1000..0x20DED0`）是 IL2CPP 运行时本体，本来就没托管符号**，显示成 `<GameAssembly+0x...>` 是**正确行为而非 bug**。

**ASLR 不是问题**：运行时基址（`0x6FFF...`）与静态 imagebase（`0x180000000`）差一个固定偏移，`base + RVA` 换算后 `.pdata` 能正确命中。怀疑偏移时，**拿一个已知 rip 反查 `.pdata`** —— 命中即说明换算正确。

#### ⚠️ 先看 dump 里到底有什么，再解释它

**「符号化不出来」有两种完全不同的原因，修法相反**：①帧落在无符号的 `.text`（正常）；②**代码页根本没被 dump 进去**（采集范围问题，再修解析器也没用）。

实测的 `DOTNET_DbgMiniDumpType=1`（MiniDumpNormal）**只抓线程栈 + 已映射页的一小部分**，**不含任何模块映像**：

| 项 | 实测值 |
|---|---|
| 内存区域 | 88 个，共 **164.7 KiB** |
| 线程栈 | **81/81 全部抓到** |
| `GameAssembly.dll` 代码页 | ❌ **完全没有** |

所以脚本会先打一行 `note: GameAssembly code pages are NOT in this dump`。
**栈本身是够的** —— 回溯靠的是栈上的返回地址，不是代码页 —— 但**指令字节、`__state`、局部变量都不在里面**，别指望从 dump 里读代码。

#### ⚠️ `rip` 在 `ntdll.dll` 里 ≠ 崩在 ntdll

两个 dump（`FailFast` 触发）的主线程 `rip` 都是 `ntdll.dll+0xEA94`，反汇编是：

```
c3                    ret
eb 01                 jmp +1
c3                    ret
ff 14 25 0010fe7f     call qword ptr [0x7ffe1000]    <- Wine 绝对间接调用
c3                    ret
```

这是 **Wine 的 syscall / 异常派发跳板**，不是游戏代码、也不是托管代码。`FailFast` 会走到这里，于是 **81 个线程里有 70 个的 `rip` 都是同一个值**。

→ **判断「崩在哪」必须看回溯出的 `#1` 起的帧**，`#0` 只是跳板。工具现在会把模块名打出来（`<ntdll.dll+0xEA94>`、`<coreclr.dll+0x211ABA>`），**"outside GameAssembly" 这种说法太粗** —— 在 `ntdll` 和在 `coreclr` 是两种完全不同的诊断。
#### 为什么外部手段都抓不到

真实案例：`Marshal.Copy` 打在未映射地址上，**无日志、无 coredump（`coredumpctl` 计数不变）、journalctl 无 wine segv**。原因：

- CoreCLR 只处理**发生在托管代码或它自己原生运行时里**的硬件异常，其余直接 `PROCAbort`
- Wine 的 `segv_handler` 把异常转成 Windows 异常，不走 Linux 信号给 systemd-coredump
- 结果**两边都以为对方会处理**

→ 所以**必须显式开 `DOTNET_DbgEnableMiniDump`**。

> ⚠️ **`FailFast` 产出的 dump 里没有 `Exception` 流**（不产生硬件异常），看不到故障地址。
> 真实野指针崩溃会有 `EXCEPTION_ACCESS_VIOLATION` + 故障地址，**信息量大得多**。
> 另：`FailFast` 跑在触发它的线程上（如 MCP 的 socket 线程），**栈上没有游戏代码** —— 要验证符号化得让崩溃发生在游戏逻辑里。

