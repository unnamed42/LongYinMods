# 运行时探查与崩溃排查

> 归属：[`AGENTS.md`](../AGENTS.md) §7 的详细展开。
> 有活进程就别猜日志 —— 本文件讲 MCP 探查与崩溃取证。

---

## 7. 运行时探查

静态分析到极限时，用运行时手段定论。**有活进程就别猜日志。**

### 7.1 Unity MCP（MelonMCP）★ 推荐

**可以对运行中的游戏执行 C# 表达式、直接读写活对象** —— 排查效率远高于读日志。

- 服务端源码在 `MelonMCP/`（本项目自建，Mono.CSharp REPL 已 ILRepack 内嵌，可执行完整 C# 语句）。

> ⚠️ **必须部署 Release 产物，不要部署 Debug。** ILRepack 的 `Condition="'$(Configuration)'=='Release'"`
> 意味着 **Debug 构建里没有内嵌 `Mono.CSharp`**，于是 `execute_csharp` / `evaluate_expression`
> 会报一个与其真实原因毫无关系的错：
>
> ```
> The type initializer for 'MelonMCP.Tools.ExecuteCSharpToolDefinition' threw an exception.
> ```
>
> 这是 `internal static readonly ScriptSession Session = new ScriptSession();` 这个静态字段
> 初始化器在找不存在的 `Mono.CSharp`。**该报错与"代码写错了"无关**，不要去查 REPL 的代码。
>
> 快速判定：`monodis --typedef <dll> | grep -c Mono.CSharp` —— Release 应约 **713**，Debug 是 **0**。
> 其它（不依赖 REPL 的）工具在 Debug 下仍可用，所以这个错很容易被误认为是单点问题。
- **核心工具**：`execute_csharp` / `evaluate_expression` / `find_objects_of_type` / `list_game_objects` / `get_type_info` / `list_types` / `list_assemblies` / `read_logs`。
- **排查补丁用的工具**（2026-10 新增）：`hook_patch_info`（补丁挂载/触发/生效 + patcher 类型 + 入口字节）、`list_patches`（全进程补丁清单，含其他 mod）、`disasm` / `read_mem` / `resolve_jump`（**运行时**字节与跳转解析）、`watch_field` / `unwatch_field`（轮询字段变化）。
- **配置读写工具**：`list_configs` / `get_config` / `set_config` / `reset_config`（读写 MelonLoader 偏好设置，见 §7.1.1）。
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

**`execute_csharp` / `evaluate_expression` 的实际能力边界**（引擎为内嵌的 Mono.CSharp）：

- ✅ **支持**：运算符与字符串拼接、`new` 对象构造、索引器、`foreach`、LINQ
  （`Where`/`Select`/`OrderBy`/`ToArray`）、`string.Join`、`typeof`、泛型类型、带局部变量的多语句。
- ✅ **会话状态跨调用保持**（变量 / using / 自定义类型）；`execute_csharp` 传 `reset=true` 清空。
- ❌ **不支持 `return x`** —— 见下方“注意事项”表（用裸尾表达式）。
- ⚠️ **已知问题**：**一整条超长单行表达式**（如整串 `string.Join(...Where(...Select(...)))`）
  会**静默**返回 `Execution completed (no result)`；拆成两条语句（先赋值再拼接）即正常。

⚠️ **已禁用的工具（不要再尝试，它们会让 MCP 客户端看到一个名字却永远失败）**：

| 工具 | 禁用原因 |
|---|---|
| `list_components` / `inspect_component` | IL2CPP 下组件列表塌缩为 `UnityEngine.Component` 代理，所有组件都报成 `Component`，`inspect_component` 永远匹配不到类型名 |
| `toggle_behaviour` / `set_property` / `invoke_method` | 同上：靠 `GetComponents` + `GetType().Name` 定位目标组件 |
| `find_game_object` | 受 `instanceId` 分支牵连（`path` 分支本身可用）；改用 `execute_csharp` + `UnityEngine.GameObject.Find` |
| `inspect_material` | 靠 `GetComponent(gameObject, "Renderer")` 拿 renderer，永远报 `No renderer found` |
| `take_screenshot` | 该 IL2CPP/CoreCLR 运行时下 `Texture2D` + `ReadPixels` 路径返回 null |

实现在源码里保留了，注册处用 `#if MELONMCP_ENABLE_BROKEN_INSPECTION_TOOLS` / `#if MELONMCP_ENABLE_BROKEN_SCREENSHOT` 关掉——**修好后开宏即可，不要重写**。

仍有部分工具**只有 `instanceId` 分支是死的**（`path` 分支正常）：`instantiate_object` / `set_transform` / `destroy_object`。它们依赖 `UnityHelper.FindObjectByInstanceId`，后者依赖一个在 IL2CPP 下解析为 null 的非泛型 `FindObjectsOfType` 反射查找。**用 `path` 寻址，别传 `instanceId`。**

**注意事项（踩过的坑）**：

| 现象 | 原因 / 对策 |
|---|---|
| **表达式里不能用 `return x`** | 交互式宿主方法返回 `void`，`return x` 报 `error CS0127`。**用裸尾表达式**：`var n = "x"; "hello " + n` |
| 一整条超长单行表达式（如整串 `string.Join(...Where(...Select(...)))`）静默无结果 | 拆成两条语句 |
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

### 7.2 日志与文件路径

- **日志**：`gamedir/MelonLoader/Latest.log`
- **配置落盘**：`gamedir/UserData/MelonPreferences.cfg` —— ⚠️ 若源码**没有**调 `SetFile`，所有 `[Category]` 都写在**这一个文件**里，不要去找 `UserData/<ModName>.cfg`。MelonPreferences **不会**自动删除已废弃的键（僵尸键无害，但别被骗）。
- **写文件必须用** `MelonLoader.Utils.MelonEnvironment.GameRootDirectory`。**不要**用 `AppContext.BaseDirectory + "..\\.."` —— 在 Proton 下相对回退会失败（本项目踩过）。

### 7.3 MelonLoader 0.7.3 生命周期

- `OnApplicationStart()` 在该版本是 `[Obsolete(error: true)]` → 用 **`OnInitializeMelon()`**；teardown 用 `OnDeinitializeMelon()`。
- `UnregisterInstance` 顺序：`OnDeinitializeMelon()` → `UnregisterInternal()` → … → **`HarmonyInstance.UnpatchSelf()`**。
  → **插件不需要自己撤销 Harmony 补丁**，MelonLoader 会做。teardown 只需释放非 Harmony 资源。
- `MelonLogger` 的静态事件（`MsgCallbackHandler` / `WarningCallbackHandler` / `ErrorCallbackHandler`）若不退订会**钉住程序集、阻止 ALC 回收**。

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

---

### 7.5 已知的工具缺口（想做但还没做）

> 原本记在 `docs/melonmcp-tooling-requests.md`。那份清单里的 P0 与踩坑表
> **已全部落地**并分别归入 §7.1 / §7.1.1，该文件已删除；以下几个**仍未实现**，
> 留在这里以免丢失（需求来源是当时一次真实的排查）。

| 工具 | 想解决的问题 | 难点 / 备注 |
|---|---|---|
| `hook_patch_info` 的 `hitCount` | 「补丁挂上了，但到底触发了几次」 | ❌ **在 IL2CPP 下做不了，已否决** —— 理由见下方。它同时否决了 `count_calls` |
| `count_calls` | 不写代码就能数某函数被调用了几次 | ❌ 同上，同一个难点 |
| `heap_objects` | 枚举某类型的**活对象** | `Il2CppObjectPool` **只是缓存，不是堆**；真正的路径是 `il2cpp_gc_heap_foreach`，需要额外导出 |

#### ⚠️ 为什么 `hitCount` 在 IL2CPP 下做不了（已验证，别再试）

这个需求看起来合理，但**它的实现必然自指**，而自指正好破坏它要回答的问题。

**事实一：Harmony 自己不记录任何调用次数。**
`HarmonyLib.Patch` 的全部字段就这些：`priority / index / owner / before / after / debug /
debugEmitPath / wrapTryCatch`；`PatchManager` 只暴露 `GetMethodPatcher` / `GetPatchInfo` /
`GetPatchedMethods` 等。**没有计数器可读**，只能自己造。

**事实二：能插计数器的位置都是 `internal`。**
反编译 `Il2CppInterop.HarmonySupport.Il2CppDetourMethodPatcher` 后，原生调用链是：

```
原函数入口 ff 25 → nativeDetour → GenerateNativeToManagedTrampoline 生成的 delegate
                                → DetourTo（CopyOriginal + HarmonyManipulator.Manipulate）
                                → 托管方法（prefix/postfix 在这里跑）
```

计数只能加在 `DetourTo` 或那个 trampoline delegate 上 —— 而该类是 **`internal`**，
`DetourTo` 是 `override`。要替换它就得**接管 Harmony 的整个 IL2CPP 补丁机制**。

**致命问题：「再加一个计数 prefix」会污染它要观测的东西。**
在 `DetourTo` 里 `HarmonyManipulator.Manipulate(... GetPatchInfo())` 说明：给同一方法再加
一个 prefix，它确实会被执行。但那样测到的是**「Harmony 托管包装被进了几次」**，
而不是「游戏调了这个函数几次」。于是发生自指：

| 情况 | 计数器显示 | 能否与其它情况区分 |
|---|---|---|
| `IsValid=true`，原生调用进得来 | N 次 | ✅ |
| `IsValid=false`，原生绕过补丁 | 0 次 | — |
| **计数器自己所在的路径没跑到** | **0 次** | ❌ **与上一行无法区分** |

**用一个可能失效的机制去检测失效。** 这正是 §5.2 那条纪律（「日志没打印 ≠ 代码没执行，
先查该日志是否被门控」）的同一个坑，只是换了个形式。

另外 `hook_patch_info` 目前是**只读查询**；为了计数而改成「顺手改一下目标的补丁链」，
会把一个安全的诊断工具变成有副作用的工具 —— 得不偿失。

#### ✅ 替代方案：让 mod 自己维护一个**不受门控**的计数器

不需要任何新工具。本项目已经在用这个模式：`FriendlyNoclip` 里的
`_onLeaveHits` / `_enterGridHits` —— **计数永远做（无开销、无副作用），日志受门控**。

它没有自指问题，因为计数器由 mod 自己的代码直接递增，与 Harmony 是否安装成功无关 ——
**「进了几次」和「补丁是否生效」是两个独立的事实，交叉对照才能得出结论**。

而且它经 MCP 直接可读（实测）：

```
_onLeaveHits = 343
_enterGridHits = 386
```

→ 新写补丁时**顺手加一个私有 static 计数字段**，比等一个通用工具划算得多。
详见 §5.2「取证仪表要不受门控」。

**P2 那一批当时都未开工**，记录下来供参考：`snapshot` / `diff`（拍快照后 diff，自动化
「某操作前后变了什么」）、`log_mark`（往日志插时间轴锚点）、`break_on` / `run_until`
（REPL 里的轻量断点）、`stack_trace_native`（MelonLoader 0.7 有 `NativeStackWalk`，
但 Windows-only 且首次要下 PDB，Proton 下行为待验证）、`register_dump`
（等价于 gdb 的 `info registers` + `x/4i $pc`）。
> 💡 这三个缺口里，**`heap_objects` 是唯一值得考虑的**：它有明确的技术路径
> （数出 `il2cpp_gc_heap_foreach` 的导出）且不自指。另外两个已否决。
>
> 至于 `hitCount`：它当初被标为「最值得补」，是因为当时只看了「它对应哪个痛点」
> 而没验证「它在 IL2CPP 下能否成立」。**这个判断是错的**，已在上文纠正。

---
