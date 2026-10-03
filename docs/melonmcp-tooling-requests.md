# MelonMCP 工具需求

> 来源：2026-10-05 FriendlyNoclip 调试 session 的实战复盘。
> 用途：交给负责 MelonMCP 的 agent 实现。
> **所有条目都来自「实际卡在哪里」，不是泛泛的功能清单。**

---

## 背景：一次真实调试中，MCP 救了我什么、又缺了什么

FriendlyNoclip 那次排查的目标是「游戏里某个格子字段被谁改掉了」。已有的 `execute_csharp` / `evaluate_expression` 已经**极大**提速（比读日志快得多），但仍有三个环节必须靠「改 mod 代码 → 冷启动 → 部署 → 读日志」的慢循环，每轮成本很高。下面的需求就是为了消掉这些慢循环。

---

## P0 — 最想要的三个

### 1. `hook_patch_info` —— 一次回答「补丁挂上了没有 / 触发了没有 / 生效了没有」

**要解决的问题**：Harmony 报「已挂载」只说明 `Patch()` 没抛异常，**不说明会触发，更不说明生效**。这次因为这个模糊性白耗了整整一轮（把「日志没打印」误读成「补丁没触发」，实际是日志被 `diagnostics` 开关门控了）。

**建议签名**：

```
hook_patch_info(typeName: string, methodName?: string, argCount?: int, owner?: string)
```

**应返回**：

| 字段 | 含义 |
|---|---|
| `target` | 实际绑定的**完整签名** + IL 地址（**必须打印真实签名**，因为同名重载返回哪个不确定） |
| `patcherType` | Harmony 实际选中的 `MethodPatcher` 类型名（如 `Il2CppDetourMethodPatcher`） |
| `patcherIsValid` | 该 patcher 的 `IsValid` 值（`internal` 属性，需反射） |
| `prefixes` / `postfixes` / `transpilers` | 各自的条数 + owner + priority |
| `entryBytes` | **运行时**方法入口的前 N 字节（十六进制） |
| `hitCount` | **每次补丁触发时累加**的计数器 |

**为什么 `hitCount` 是关键**：它把三层一次说清，而且**不受任何日志开关门控**。这次我要自己往 mod 里加 `Interlocked.Increment` 才拿到等价信息。

**注意**：`PatchManager.GetPatchInfo(MethodBase)` 返回的对象里，`prefixes` / `postfixes` / `trailing` 的 `Count` 是**方法**不是属性（在 C# REPL 里要写 `.Count()`）；`Il2CppDetourMethodPatcher` 是 `internal` 类型，`IsValid` 是 `internal` 属性，都要反射。

---

### 2. `watch_field` —— 定位「谁改了这个字段」

**要解决的问题**：查到「某字段的值不对」之后，**找出是哪段代码改的**。这次只能靠静态扫机器码（`mov [reg+0x18], r64`）去找写者，结果**零命中**（真正的写者在原生代码里，模式不同），最后只能靠日志的相对顺序去推断，绕了很大一圈。

**建议两种实现，先做简单可靠的**：

**(a) 轮询快照版（推荐先做）**

```
watch_field(objectRef 或 (typeName + 找对象的方式), fieldName, mode: "poll"|"write", intervalFrames?: int)
```

- 每 N 帧读一次该字段，**变化时**记录：`帧号 / 时间 / 旧值 / 新值`
- 再给一个 `unwatch_field` 停止
- 优点：**零风险**，不会崩游戏，不需要知道字段偏移，不需要内存权限
- 缺点：可能漏掉「同一帧内改了又改回来」

**(b) 硬件写断点版（价值最高，风险也最高）**

- x64 只有 **4 个**硬件断点寄存器（`dr0`-`dr3`），`dr7` 配置长度与类型
- 命中时抓 `rip` + **调用栈**（这才是「谁改的」的最终答案）
- 难点：命中后要恢复现场、要能安全摘除、**绝不能把游戏搞崩**
- 建议在 (a) 验证过用法之后再动

**至少请实现 (a)。** 有它我就能直接看到「字段在哪个时间点、因为什么操作变了」，而不是靠 `Prefix`/`Postfix` 的相对顺序猜。

---

### 3. `disasm` + `read_mem` —— 看**运行时**的真实字节

**要解决的问题**：**磁盘上的字节 ≠ 内存里的字节**。IL2CPP + Il2CppInterop 会把原生方法入口改写成 `ff 25 <disp32>`（`jmp qword ptr [rip+disp32]`），跳转槽落在模块映像**之外**。这次我反复要提醒自己这个差异，而且为了看运行时字节不得不动用 `gdb`（还要先解决沙箱的 PID 命名空间隔离）。

**建议签名**：

```
disasm(address: long, count?: int)      → 指令列表（地址 / 字节 / 助记符 / 操作数）
read_mem(address: long, length: int)    → 十六进制 + 可打印字符
resolve_jump(address: long)             → 若该处是 ff 25 / e9，解出真实目标地址
```

**额外价值**：`resolve_jump` 能直接回答「这个 `ff 25` 最终跳去哪」——这次我是靠手工读 `[rip+disp32]` 才算出来的。

**注意**：`GameAssembly.dll` **只映射头部一页**，真正的代码在匿名 `r-xp` 区（约 24MB）。所以要按 `r-xp` + 大小筛选，不能只找模块名。

---

## P1 — 次想要（这次也用到，但绕了路）

### 4. `list_patches` —— 全局补丁清单

一次列出进程内**所有** Harmony 补丁（**含其他 mod 的**），每条带：目标方法完整签名、owner、priority、prefix/postfix 类型。

这次我想知道「某方法上到底挂了几个 patch」时，只能一个一个反射查。多个 mod 之间互相干扰时（比如两个 mod 都 patch 了同一方法），这个工具是刚需。

### 5. `count_calls` —— 不写代码就能数调用次数

`count_calls(typeName, methodName, argCount?)` → 开启后返回该方法的调用次数；配合 `stop_count_calls` 停止。

这次我是往 mod 里加 `Interlocked.Increment` 计数器来实现的：**改代码 + 冷启动 + 部署**，一轮成本很高。

### 6. `heap_objects` —— 枚举某类型的活对象

`heap_objects(typeName, limit?)` → 该类型的所有（或前 N 个）活实例指针 + 基本信息。

**为什么需要**：`FindObjectsOfType<T>()` 只对 `UnityEngine.Object` 子类有效。这次想找 `GridUnitData` / `BattleUnit`（**不是** `UnityEngine.Object`）时完全用不了，只能顺着 `BattleController.battleMapData.normalGrids` 摸进去 —— 但如果不知道入口在哪，就无从下手。

**注意**：Il2CppInterop 的 `Il2CppObjectPool` 只是**缓存**不是全量遍历，不能替代这个功能。

---

## P2 — 顺手做也不错

| 工具 | 用途 |
|---|---|
| `snapshot` / `diff` | 给一批对象/字段拍快照，之后 diff，自动化「某个操作前后变了什么」 |
| `log_mark(label)` | 往 MelonLoader 日志里插一个显眼的锚点，方便把日志时间轴和 MCP 操作对齐 |
| `break_on(address)` / `run_until(address)` | 在 REPL 里做轻量断点，不用切 gdb |
| `stack_trace_native()` | 拿原生栈帧。MelonLoader 0.7 有 `NativeStackWalk`，但它是 Windows-only 且首次要下 PDB；Proton 下行为待验证 |
| `register_dump()` | 一次性 dump 通用寄存器 + `rip` 附近反汇编，等价于「gdb 的 `info registers` + `x/4i $pc`」 |

---

## 现有工具的踩坑（建议在文档/错误信息里提示）

这些是我这次真实踩到的，**实现新工具时也可能重复**：

| 现象 | 原因 / 对策 |
|---|---|
| `return x` 报 `error CS0127: 'InteractiveHost': A return keyword must not be followed by any expression when method returns void` | 交互式宿主方法返回 `void`。**用裸尾表达式**：`var n = "x"; "hello " + n` |
| 一整条超长单行表达式（如整串 `string.Join(...Where(...Select(...)))`）**静默返回 "(no result)"** | 拆成两条语句 |
| `Count` 报 `Cannot convert method group 'Count' to non-delegate type 'int'` | 是**方法**不是属性 → 写 `.Count()` |
| `FindObjectOfType<Il2Cpp.Xxx>()` 报 `Method unstripping failed` | 改用 `FindObjectsOfType<MonoBehaviour>(true)` 按 `GetType().Name` 过滤 |
| 局部变量重名报 `CS0136` | **会话状态跨调用保持**，换个名字 |
| lambda 语句体里带 `new object[]{...}` 编译失败 | 拆开写 |
| 偶发卡顿 / 无响应 | 网络问题，**直接重试**即可 |

---

## 优先级建议

**只做三个 → 1 + 2(a) + 3。**

| 本次调试的痛点 | 对应工具 |
|---|---|
| 补丁挂载 / 触发 / 生效分不清，且日志受开关门控 | `hook_patch_info`（带 `hitCount`） |
| 知道字段值错了，但不知道谁改的 | `watch_field`（轮询版） |
| 磁盘字节 ≠ 运行时字节，`ff 25` 指向哪不明 | `disasm` + `read_mem` + `resolve_jump` |

**2 是价值最高但最难做的**。硬件断点要处理「只有 4 个槽」「命中后要恢复」「绝不能崩游戏」，而**轮询快照版简单可靠得多，建议先做它**。多数「谁改的」问题，看到「哪个时间点、因为什么操作变的」就已经够了。
