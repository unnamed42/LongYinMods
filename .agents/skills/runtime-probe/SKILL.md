---
name: runtime-probe
description: 当要在运行中的龙胤立志传里读状态、改配置、验证补丁是否触发，或排查崩溃/卡死/退出不干净时使用。覆盖 MCP 工具选择、execute_csharp 的能力边界与两个「无声杀进程」路径、生命周期与 minidump 回溯。
whenToUse: 静态分析到极限、需要活进程证据；或游戏崩了/卡死/补丁不生效需要现场取证时。
---

# 运行时探查与崩溃排查

**有活进程就别猜日志。** 本 skill 是使用视角：选哪个通道、每个工具的边界、崩溃了怎么取证。

**完成判据**：结论来自活进程读数或可回溯的 dump，而不是「日志里没看到」。

## 0. 先选通道

| 你的问题 | 去哪 |
|---|---|
| 该用哪个 MCP 工具、它的行为与边界 | §1~§4 |
| 代码写了却不生效 | `harmony-patch` / `native-hook` 的验证小节 + 本文 §5 生命周期 |
| 崩了 / 卡死 / 进程不退出 | §6（minidump → 栈回溯） |
| 要复现只在特定战斗里出现的 bug | `docs/game-internals.md` §5（主动构造战斗） |

## 1. `execute_csharp`：能力边界

引擎是内嵌 Mono.CSharp，**约 C# 7.1**（不是 7.2）。

| 能力 | 结果 |
|---|---|
| 运算符 / 字符串拼接 / `new` / 索引器 / `foreach` / LINQ 方法链 / `string.Join` / `typeof` / 泛型 / 多语句 | ✅ |
| 元组 / `out var` / `is T x` / 数字分隔符 / `private protected` | ✅ |
| 会话状态跨调用保持（变量 / using / 自定义类型） | ✅，`reset=true` 清空 |
| 局部函数 / `in` 参数 / switch 表达式 / using 声明 / LINQ 查询语法 | ❌ |
| `return x` | ❌ 用**裸尾表达式**（`var n = "x"; "hello " + n`） |

**结果分类（必须能分辨）**：

| 结果文本 | `isError` | 含义 | 做什么 |
|---|---|---|---|
| 值 / 文本 | false | 成功有值 | — |
| `Executed successfully; no value returned.` | false | 成功但没值 | 要值就加裸尾表达式 |
| `Compilation failed:` + `error CS....` | **true** | 一行都没执行 | 改代码 |
| `Execution failed: ...` | **true** | 跑起来了，抛异常 | 看栈 |

> ⚠️ `Console.WriteLine` 的输出**从来不会被捕获**（那是编译器的 report printer）。用裸尾表达式。

## 2. ⚠️ 两个「无声杀进程」路径（只能预防）

### ① 栈溢出：REPL 栈只有约 82 KiB

`execute_csharp` 跑在 Unity 主线程上。**重反射会栈溢出** → 命中 guard page →
`EXCEPTION_STACK_OVERFLOW`，CoreCLR 不处理，直接 abort：**无异常、无日志、无 coredump**。

已崩过两次：GC 堆遍历回调里做反射、循环里 `MakeGenericMethod`。

> **纪律：重反射写进 mod 源码（编译后的 C# 有正常栈），不要在 `execute_csharp` 里跑。**
> 必须用运行时泛型时，一次只做一个，确认返回后再做下一个。
> `try/catch` 和 `timeout` 都拦不住。

### ② 猜字段名：缺失字段返回空指针，随后原生路径解引用

```csharp
IL2CPP.il2cpp_field_get_offset(IL2CPP.GetIl2CppField(cls, "listType")); // ✗ 字段不存在 → 进程消失
```

后果：日志只剩一行 `Field listType was not found on class ItemListController`，进程随即消失。

> **判据：读字段前先枚举、再取。** 不要拿猜的名字当探针。
> 枚举方式：读 `output/decomp/full/Il2Cpp/<Type>.cs`，或遍历 `il2cpp_class_get_fields`。

**顺带学会分辨「崩了」还是「正常退出」**：正常退出有退出序列；`grep -c "OnApplicationQuit" <log>` 为 0
且日志停在某条错误行 ⇒ 是崩。

## 3. 常用工具与边界

| 工具 | 用途 / 注意 |
|---|---|
| `execute_csharp` / `evaluate_expression` | 执行 C#，见 §1~§2 |
| `find_objects_of_type` / `list_game_objects` | 枚举实例 |
| `hook_patch_info` / `list_patches` | 补丁挂载/触发/生效、patcher 类型、入口字节；全进程补丁清单 |
| `disasm` / `read_mem` / `resolve_jump` | **运行时**字节与跳转解析 |
| `watch_field` / `unwatch_field` | 轮询字段变化 |
| `list_configs` / `get_config` / `set_config` / `reset_config` | 读写 MelonPreferences，见 §4 |
| `inspect_unity_object` | 按类型 + 点号路径批量读字段，失败隔离 |
| `read_logs` `group_by="prefix"` | 按频率分组计数，回答「哪条日志在刷屏」 |
| `dump_menu_state` | 枚举按钮 + `enabled` / `interactable` / `activeInHierarchy` 三标志 |
| `get_game_knowledge` / `add_game_knowledge` | 只存**游戏本身**的知识，见 `AGENTS.md` §1.2 |

**已禁用的工具（不要再试，会让客户端看到名字却永远失败）**：`list_components` / `inspect_component` /
`toggle_behaviour` / `set_property` / `invoke_method` / `find_game_object` / `inspect_material` / `take_screenshot`。

> 共同死因：**靠反射通用地发现**组件/属性，而 IL2CPP 下代理会塌缩成 `UnityEngine.Component`。
> `inspect_unity_object` / `dump_menu_state` 能工作，正是因为**调用方显式给出 typeName + fields**。
> 🚫 **不要把它们「改进」成自动字段发现器** —— 那正是失败的做法。

**`inspect_unity_object` 的两个 IL2CPP 陷阱**：

1. 必须用泛型 `FindObjectsOfTypeAll<T>()`。非泛型重载**只有 index 0 保留具体类型**，其余退化成 `Object`
   （症状：`name` 能读、`position` 报 `no member` —— 看起来像字段名错，其实是实例问题）。
2. `where` 的布尔比较**不区分大小写**（`True` vs `true`），否则静默选不出实例。

**`read_logs` 的分组键**：忽略时间戳与 level；两个 tag 时取**第二个**；只有一个 tag 时退到消息正文；
序号要归一化（`第 N 次` / `#N` / `count=N`）。

> ⚠️ `read_logs` 读的是 MelonMCP 自己的缓冲，**Msg 通道绑定失败**（本构建下），
> WARNING/ERROR 可靠、普通 Msg 可能缺失 → **「这里没有」≠「从来没打过」**。

## 4. 配置读写

**不要直接编辑 `MelonPreferences.cfg`** —— 游戏运行时会**整体重写**它，手改会被内存里的值静默覆盖。

- `set_config` 要求 `confirm == "<mod>.<key>"`，防误写落盘。
- `BoxedValue` / `SetEntryValue` **只改内存**；落盘要显式 `Save()`。
- `set_config` 返回 `restartRequired: "unknown"` 是**故意的**：值在读取点读 → 立即生效；
  值在 `OnInitializeMelon` 里消费（典型：装原生 hook / Harmony 补丁的开关）→ **已烧进进程，改了要冷启动**。

> 典型例子：`FriendlyNoclip` 的 `wall_native_hooks` 改了不会卸载已装的 detour；`diagnostics` 改了立即生效。

## 5. 生命周期与退出

- `OnApplicationStart()` 在 0.7.3 是 `[Obsolete(error: true)]` → 用 `OnInitializeMelon()`；teardown 用 `OnDeinitializeMelon()`。
- `UnregisterInstance` 顺序：`OnDeinitializeMelon()` → … → `UnpatchSelf()`，**插件不必自己撤 Harmony 补丁**。
- ⚠️ **`OnDeinitializeMelon` 在「热重载」和「真退出」下都会调用，且无法从它自身区分。**
- ⚠️ **回调顺序由 priority 决定，不是订阅先后**：`MelonAssembly` 用默认 priority=0。
  要插到它之前必须给**负数**：

  ```csharp
  MelonEvents.OnApplicationQuit.Subscribe(OnDefiniteQuit, -1);
  MelonEvents.OnApplicationDefiniteQuit.Subscribe(OnDefiniteQuit, -1);
  ```

  > 真实事故：想用 `OnApplicationDefiniteQuit` 置「真退出」标志，默认 priority 排在 MelonAssembly 之后，
  > 标志在 `OnDeinitializeMelon` **之后**才置上 → 保活逻辑从未执行。**完全静默。**
  > 启动时用 `MelonEventBase.GetSubscribers()` 把顺序读回来自检。

- **IL2CPP 退出卡死**是 Unity 已知问题（Mono 正常、IL2CPP 卡）。判定特征：进程 `S` 态、CPU 归零、
  日志已走到全部 mod 卸载完，但进程不退 → `eu-stack -p <pid>` / `gdb -p <pid>` 看谁还 attach 着。
  实例见 `docs/wumingperformance-fix.md`。

## 6. 崩溃排查

- 游戏崩溃时 MelonLoader **来不及写日志** → 用 minidump 或**二分隔离**（把可疑功能各配一个开关）。
- 附加 gdb：沙箱默认 PID 命名空间隔离（只看到几个 PID），**这是命名空间问题不是权限问题，`sudo` 无用**
  → 需要 `danger-full-access`。gdb 要在 .NET 信号上 `handle SIGUSR1 nostop noprint pass`（及 SIGUSR2/SIG32/SIG33/SIGPIPE）。

### 6.1 首选：让 .NET 自己产 minidump

Steam 启动选项（环境变量写在 `%command%` **前面**）：

```
DOTNET_DbgEnableMiniDump=1 DOTNET_DbgMiniDumpType=1 DOTNET_DbgMiniDumpName="S:\coredump\crash_%t.dmp" DOTNET_EnableCrashReport=1 %command%
```

- `S:` 是游戏目录（Proton 映射）；不指定名字时默认落 `%TEMP%`，不是 CWD。
- 占位符：`%p`=pid、`%t`=Unix 时间戳（推荐）、`%e`=exe 名。

### 6.2 离线回溯：`tools/il2cpp_unwind.py`

```bash
tools/il2cpp_unwind.py <coredump>/crash_<ts>.dmp [--all-threads]
```

**gdb 为什么不行**：IL2CPP 的 x64 代码不用 frame pointer，`rbp` 常是堆指针，`bt` 只能打 `??`。
正确做法是 **`.pdata` 表驱动回溯**（`RUNTIME_FUNCTION` → `UNWIND_INFO`，本二进制 104663 条）。

写这类工具的三个坑：

1. `os.path.basename` 不切 Windows 反斜杠（minidump 里是 `S:\...`）→ 手写 `replace('\\','/').rsplit('/',1)[-1]`。
2. 「在模块内」的边界必须是**模块真实大小**（从 `ModuleList` 取），大常数会产生假帧。
3. 二分查找防**空集回绕**：先判 `rva < sorted[0]`。

### 6.3 先看 dump 里到底有什么，再解释它

`MiniDumpNormal` **只抓线程栈 + 一小部分页，不含任何模块映像**：

| 项 | 实测 |
|---|---|
| 内存区域 | 88 个，共 164.7 KiB |
| 线程栈 | 81/81 全抓 |
| `GameAssembly.dll` 代码页 | ❌ 完全没有 |

所以栈够回溯（靠返回地址），但**指令字节、`__state`、局部变量都不在**。

> ⚠️ **`rip` 在 `ntdll.dll` 里 ≠ 崩在 ntdll**：`FailFast` 会走到 Wine 的 syscall/异常派发跳板，
> 81 个线程里 70 个 `rip` 是同一个值。**判断「崩在哪」必须看 `#1` 起的帧。**
> ⚠️ `FailFast` 的 dump 里没有 `Exception` 流，也没有游戏代码栈 —— 真实野指针崩溃
> （`EXCEPTION_ACCESS_VIOLATION` + 故障地址）信息量大得多。

## 收尾

- 细节与实测见 [`docs/runtime-probing.md`](../../../docs/runtime-probing.md)。
- 日志路径 `gamedir/MelonLoader/Latest.log`；配置 `gamedir/UserData/MelonPreferences.cfg`。
- 写文件用 `MelonEnvironment.GameRootDirectory`，**不要** `AppContext.BaseDirectory + "..\\.."`（Proton 下失败）。
