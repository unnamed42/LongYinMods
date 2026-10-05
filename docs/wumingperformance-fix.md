# WuMingPerformance 退出卡死修复（第三方 mod，无源码）

> 归属：[`AGENTS.md`](../AGENTS.md) §8。流程依据 [`mod-recompilation.md`](mod-recompilation.md)。
> 工程：[`WuMingPerformanceFix/`](../WuMingPerformanceFix/)
>
> ⚠️ 本文含**具体游戏行为与实测数据**，游戏更新后可能失效。
> 通用流程（工具 / 命令 / 坑）在 [`mod-recompilation.md`](mod-recompilation.md)，不写在这里。

---

## 1. 症状

游戏**点退出后进程不退出**，窗口消失但 `LongYinLiZhiZhuan.exe` 一直挂着，只能 `kill`。

**二分证据**（用户实测）：启用 `WuMingPerformance` → 卡死；不启用 → 正常退出。

---

## 2. 现场特征（怎么认出是这一类）

| 观测项 | 值 |
|---|---|
| 进程状态 | `S`（sleeping） |
| CPU | **归零**（`utime` 连续采样不增长） |
| 日志 | 停在 `Preferences Saved!`，之后全静默 |
| 线程 | 全部在 `ntsync_schedule` / `__futex_wait` / `poll` 上睡眠，**没有一个在跑** |
| 退出流程 | **所有 mod 都已卸载完毕** |

**判定要点**：这是「**纯等待、无人唤醒**」，不是死循环（死循环会烧 CPU），也不是崩溃。

> ⚠️ 别与另一种形态混淆：本项目还遇到过**运行中**卡死（主线程在 `GameAssembly.dll` 里
> 100% CPU 死循环），那是**另一个**问题，见 §7。

---

## 3. 根因

`WuMingPerformance` 为做异步优化起了一个后台线程（`WuMingPerf-WorldWorker`），它
**attach 进 IL2CPP domain 后从不 detach**：

```csharp
// WuMingPerformance.Patches/BackgroundWorldTasks.cs（修复前）
private static void WorkerLoop()
{
    if (Il2CppThreadScope.TryAttach(out var scope))   // ← il2cpp_thread_attach，owns:true
    {
        _workerAttached = true;
        GC.KeepAlive(scope);                          // ← 只是"保活"
    }
    while (true)                                      // ← 永不退出，无任何 break/信号
    {
        ...
        _wake.WaitOne();                              // ← 永久阻塞，无超时
    }
    // 没有任何 scope.Dispose() → 没有 il2cpp_thread_detach
}
```

**三个缺陷叠加**：

1. 循环 `while (true)`，**无退出路径**
2. `_wake.WaitOne()` **无超时**，`_worker` 从不被 `Join` / 发信号
3. `Il2CppThreadScope` 创建后**从不 `Dispose()`** ⇒ **从不 `il2cpp_thread_detach`**

于是线程以「**非 detach 方式**」终止，而 IL2CPP 运行时的 teardown 无法完成。

**依据**（Unity 工程师对同类现象的解释，[来源](https://discussions.unity.com/t/background-threads-cause-app-to-hang-on-ios-with-il2cpp/890948)）：

> At shutdown, the IL2CPP runtime will attempt to cause all threads (background or not) to exit.
> Threads that are executing managed code should exit properly. **But if a given thread is executing
> native code, that might be a problem**, as the native code could be involved in some kind of a
> blocking call into the OS, and the IL2CPP runtime won't be able to stop that thread.

同症状的官方 issue：[Player build freezes after calling Application.Quit() when the scripting
backend is set to IL2CPP](https://issuetracker.unity.com/issues/8178/player-build-freezes-after-calling-applicationquit-when-the-scripting-backend-is-set-to-il2cpp)
（特征：**Mono 后端正常、IL2CPP 卡死**，2019→2022 多版本复现）。

### 3.1 关键实测证据

修复前，退出瞬间由 mod 自己打印的状态：

```
[ShutdownTag] OnApplicationQuit ENTER | worker=alive managedThreadId=3
              isBackground=True state=Background, WaitSleepJoin
              attachedToIl2Cpp=True queued=0 running=0 | IsSaving=False
[ShutdownTag] waited for HasRunning to clear | elapsedMs=1 HasRunning=False
[ShutdownTag] entering WaitForPendingSave | elapsedMs=1
[ShutdownTag] WaitForPendingSave returned | elapsedMs=1
[ShutdownTag] base.OnApplicationQuit returned | elapsedMs=2
```

**`attachedToIl2Cpp=True`** —— 线程在退出时确实还 attach 着。

> 💡 **反例警告**：这些日志**不能**用来判断「mod 的退出逻辑有没有跑」。
> 修复前那段代码在 `HasRunning == false` 时**本就不打任何日志**，
> 所以「没有日志」≠「没有执行」。本项目曾据此误判过一次（见 §6）。

### 3.2 因果位置的更正

最初的推断是「**WuMing 自己在等那个 worker**」—— **这是错的**。
实测（§3.1）显示它的退出逻辑 **2ms 就跑完了**，卡死发生在其后 400+ms、以及整个
MelonLoader teardown 之后。

正确的位置是：**IL2CPP 运行时在等一个永不归队的线程**，而不是 mod 在等它。

---

## 4. 修复

四处改动，全部在 [`WuMingPerformanceFix/`](../WuMingPerformanceFix/)：

### ① 给循环一个出口（核心）

```csharp
private static volatile bool _shutdown;

try
{
    while (!_shutdown)                    // 原来是 while (true)
    {
        ...
        _wake.WaitOne(200);               // 原来是无超时 WaitOne()
    }
}
finally
{
    scope.Dispose();                      // 补上漏掉的 il2cpp_thread_detach
}
```

`WaitOne(200)` 的超时是关键：它让 `_shutdown` 每 200ms 必定被重新检查，
**不存在 `Set()` 与循环自身出队竞争而丢失信号**的窗口。

### ② 退出时主动停线程并等待

```csharp
internal static void ShutdownWorker(int timeoutMs = 3000)
{
    _shutdown = true;
    _wake.Set();
    if (!worker.Join(timeoutMs))
        MelonLoader.MelonLogger.Error($"工作线程在 {timeoutMs}ms 内未退出...");
}
```

**超时是有意的**：若线程正卡在任务里（`Execute` 等日信号最长 15s），`Join` 会放弃
而**不会反过来把退出本身挂住**；且失败**打错误日志**，不静默。

调用点：`WaitForPendingSave()` **之后**、`base.OnApplicationQuit()` **之前** ——
既让飞行中的任务有机会跑完，又让 detach 尽早发生。

### ③ `Enqueue` 的边界情况

`Enqueue` 是**惰性建线程**的。退出后若还有任务提交，会重新起一个 `_shutdown` 已为
`true` 的线程 → **立即退出、任务被静默丢弃**。已加守卫，改为**内联执行**：

```csharp
if (_shutdown) { Execute(item); return; }
```

### ④ 诊断日志（`[ShutdownTag]` 前缀）

**不受 `DiagnosticsEnabled` 门控**，因为该开关只覆盖「任务在跑时」的信息，
而卡死现场「什么任务都没在跑」——**结构上就测不到**。

---

## 5. 验证

### 5.1 修复生效的判据（日志）

```
[ShutdownTag] stopping worker | ... attachedToIl2Cpp=True ...
[ShutdownTag] worker detached from il2cpp and exiting      ← ★ 关键：修复前从来没有这一行
[ShutdownTag] worker stopped | ... worker=dead ...
[ShutdownTag] base.OnApplicationQuit returned | ...
```

**修复前后唯一的功能性差异就是那一行 `worker detached from il2cpp`**，之后整个
teardown 顺利完成、进程消失。

### 5.2 反向实验（更强的证据，尚未做）

若要**双向**证实因果，可做：**去掉 `scope.Dispose()`（保留退出信号）→ 预期重新卡死**。
这是最干净的受控对比，且**零 API 风险**。

### 5.3 ⚠️ 证据强度说明

- ✅ **已证实**：显式 detach 后，同样操作流程不再卡死（唯一变量是那一行 detach）
- ⚠️ **未直接观测**：IL2CPP 内部究竟在等什么

所以严格表述是「**detach 消除了卡死**」，而非「**已证明 IL2CPP teardown 在等该线程**」。

**为什么不去直接观测**：`il2cpp_thread_get_all_attached_threads` **是官方 API**
（见 libil2cpp 的 `il2cpp-api-functions.h`，本游戏 `GameAssembly.dll` 也确实导出，
序号 220），但社区实测它**常不可用或返回空**
（[frida-il2cpp-bridge#618](https://github.com/vfsfitvnm/frida-il2cpp-bridge/issues/618)、
[#658](https://github.com/vfsfitvnm/frida-il2cpp-bridge/issues/658)，替代方案会导致
access violation）。而我们要在**正在 teardown 的运行时**上调它，
**可能把卡死变成崩溃**，从而失去还能用的 MCP。**收益不抵风险，故放弃。**

---

## 6. 本项目在此次修复中犯过的错（值得记住）

| 错误 | 教训 |
|---|---|
| 据「退出时没有日志」推断「退出逻辑没执行」 | **那段代码在 `HasRunning==false` 时本就不打日志**。「没有日志」≠「没有执行」 |
| 以为卡死在 mod 的等待里 | 实测它的退出逻辑 **2ms 就跑完**了。要**先加日志确认边界**，再谈推断 |
| 修复后仍报 `ThreadStateException` | 见下 |

**`ThreadStateException` —— 一个通用的 C# 坑，与反编译无关**：

```csharp
// 抛异常（线程已死）
worker.IsBackground
worker.ThreadState

// 安全
worker.IsAlive            // 返回 false
```

我们的诊断代码只给 `IsAlive` 加了保护，却在 `ShutdownWorker()` **之后**读
`IsBackground` / `ThreadState`（此时线程已死）→ `ThreadStateException`：

```
System.Threading.ThreadStateException: Thread is dead; state cannot be accessed.
   at System.Threading.Thread.IsBackgroundNative()
   at System.Threading.Thread.get_IsBackground()
   at ...BackgroundWorldTasks.DescribeWorker()
```

**规则：`Thread` 的 `ThreadState` / `IsBackground` 在终止后不可读；任何日志代码都不许抛。**
详见 [`AGENTS.md`](../AGENTS.md) §3.4。

---

## 7. 与另两种卡死的区分

本项目遇到过**三种**卡死。前两种在 mod 层面，可以处理；第三种**不在我们的层面**：

| | 本文（退出卡死） | 运行中卡死 | wineserver 阻塞 |
|---|---|---|---|
| 时机 | 点退出后 | 时间流逝中 | **Alt-Tab 切窗口后** |
| 进程状态 | `S` | `R`（running） | `S` |
| CPU | **0%** | **~100%（单核烧满）** | **0%** |
| 主线程位置 | Wine `ioctl` | **`GameAssembly.dll` 内死循环** | **读 wineserver 的 pipe** |
| 与 WuMing | **有关** | **无关** | **无关** |
| 能否修 | ✅ 已修（本文） | 未定位 | ❌ **不在我们的层面** |
第二种（运行中卡死）是游戏原生代码里的死循环（现场：两个线程用 24 字节步长扫同一张表），
**至今未定位**。

第三种（wineserver 阻塞）：Alt-Tab 后主线程卡在 `read()` 一个 **wineserver 持有的 pipe** 上，
且**所有托管线程**（含 `Job.Worker`、`.NET Finalizer`、MCP 的线程池线程）都阻塞在同一类
`read()` 上 —— 即**整个托管层在等 wineserver 回应**，而 wineserver 自身睡在 `ep_poll`。

> **为何不再往下查**：这属于 **Wine/Proton 层的同步问题，不是 mod 或 .NET 的问题**。
> 而且它直接推翻了「给 MCP 换专用线程就能保活」的想法 —— 在 wineserver 级阻塞下
> **进程内任何手段都救不了**（线程全冻）。想看栈只能从**进程外**（宿主侧 `gdb`/`eu-stack`），
> 那正是本项目已有排查手段。
>
> **绕过思路**（未验证）：Alt-Tab 前先进菜单/暂停；或试 gamescope / `PROTON_USE_WINED3D=1`。

均见 [`runtime-probing.md`](runtime-probing.md) 的退出/卡死排查章节。

---

## 8. 复现与部署

```bash
# 重新构建（工程已在仓库内，无需重新反编译）
dotnet build WuMingPerformanceFix/WuMingPerformance.csproj -c Release

# 部署（需提权），并核对 md5 + 体积
cp WuMingPerformanceFix/bin/Release/net6.0/WuMingPerformance.dll gamedir/Mods/
md5sum WuMingPerformanceFix/bin/Release/net6.0/WuMingPerformance.dll gamedir/Mods/WuMingPerformance.dll
```

> ⚠️ **构建不可字节复现**：两次构建的 PE `TimeDateStamp` 与模块 MVID 不同
> （实测 209 字节差异，仅此二者）。**体积与类型数应一致**（46,592 B / 55 类型）；
> 因此校验用「**体积 + 类型数 + 关键字符串**」，不能只看 md5。
>
> ⚠️ **改完必须冷启动**（[`AGENTS.md`](../AGENTS.md) §3.2.1）。
