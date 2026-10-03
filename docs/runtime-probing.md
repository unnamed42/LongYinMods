# 运行时探查与崩溃排查

> 归属：[`AGENTS.md`](../AGENTS.md) §7 的详细展开。
> 有活进程就别猜日志 —— 本文件讲 MCP 探查与崩溃取证。

---

## 7. 运行时探查

静态分析到极限时，用运行时手段定论。**有活进程就别猜日志。**

### 7.1 Unity MCP（MelonMCP）★ 推荐

**可以对运行中的游戏执行 C# 表达式、直接读写活对象** —— 排查效率远高于读日志。

- 服务端源码在 `MelonMCP/`（本项目自建，Mono.CSharp REPL 已 ILRepack 内嵌，可执行完整 C# 语句）。
- **核心工具**：`execute_csharp` / `evaluate_expression` / `find_objects_of_type` / `list_game_objects` / `get_type_info` / `list_types` / `list_assemblies` / `read_logs`。
- **排查补丁用的工具**（2026-10 新增）：`hook_patch_info`（补丁挂载/触发/生效 + patcher 类型 + 入口字节）、`list_patches`（全进程补丁清单，含其他 mod）、`disasm` / `read_mem` / `resolve_jump`（**运行时**字节与跳转解析）、`watch_field` / `unwatch_field`（轮询字段变化）。
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
