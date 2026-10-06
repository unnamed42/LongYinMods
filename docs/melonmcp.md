# MelonMCP —— 自建的游戏内 MCP 服务端

> 归属：[`AGENTS.md`](../AGENTS.md) §7 索引。
> **本文件是「MelonMCP 这个项目」的文档**：构建、部署、架构、传输层、内部机制、TODO 与自检。
> 只想**用它探查游戏**的读者看 [`runtime-probing.md`](runtime-probing.md) §7.1（工具清单与使用注意）—— **不必打开本文件**。

---

## 0. 改 MelonMCP 前先看

- **改完必跑** `python3 tools/mcp_selftest.py`（27 项，约 0.3 秒，退出码 0/1/2 = 全过/有失败/连不上）。
  加用例时注意**每个用例要自带前置状态**，否则 `--only` 单跑会假失败。
- **传输层两条红线**（碰了就重现故障）：永远发准确的 `Content-Length`（**绝不 chunked**）、
  永远 `Connection: close`。**不要改回 `System.Net.HttpListener`** —— Proton 下走的是 Wine 的
  http.sys，那是 stub，调到就 abort。
- **`execute_csharp` 的成败信号是编译器的 `ErrorsCount`，不是 `compiled == null`** ——
  只声明类型的提交本来就没有可执行方法，拿它当失败会把 `class Foo {}` 变成报错。
- 部署和其它 mod 一样，**必须冷启动**（`AGENTS.md` §3.2.1）。

---

## 1. 它是什么

- 服务端源码在 `MelonMCP/`（本项目自建，Mono.CSharp REPL 已 ILRepack 内嵌，可执行完整 C# 语句）。
- 传输走协议自带的 **Streamable HTTP**：MCP 客户端只需一个 URL（`http://127.0.0.1:27015/mcp`），**没有 bridge 脚本**。见 §5。

---

## 2. 构建与部署

和别的 mod 一样：`dotnet build MelonMCP/MelonMCP/MelonMCP.csproj -c Debug`，产物拷进 `gamedir/Mods/`。**部署后必须核对两边 md5 一致，并冷启动游戏**（`AGENTS.md` §3.2 / §3.2.1）—— MelonMCP 也是 mod，同样不会热重载。

> ✅ **Debug / Release 都会内嵌 `Mono.CSharp`（2026-10 修正）。**
>
> 这里曾经写着「必须部署 Release，Debug 里没有内嵌 Mono.CSharp，`execute_csharp` 会报
> `The type initializer for 'MelonMCP.Tools.ExecuteCSharpToolDefinition' threw an exception.`」——
> **那个条件已经被移除，本条不再成立。** 现在 `MergeMcsIntoMod` 目标
> **故意不带任何 Configuration 条件**，所以 `dotnet build -c Debug` 产出的 DLL 一样含 REPL。
>
> 原因写在 csproj 的注释里：条件式的 Release-only 合并会让 Debug 构建「**构建成功却产出坏产物**」，
> 而失败点（静态字段初始化器）离真实原因太远。**构建成功却不能用，比构建失败更糟。**
>
> 校验（部署前后都可跑，预期约 **713**）：
>
> ```bash
> monodis --typedef <dll> | grep -c Mono.CSharp
> ```

---

## 3. 自检：改完必跑

**改这段代码后要跑的最小回归集**（2026-10 活进程实测，全部通过）：

| 类别 | 输入 | 期望 |
|---|---|---|
| 表达式 | `1 + 1` | `2` |
| 语句 + 裸尾 | `int a = 1; int b = 2; a + b` | `3` |
| 集合 + 裸尾 | `var l = new List<int>{1,2,3}; l.Count` | `3` |
| LINQ（扩展方法） | `l.Where(x => x > 1).Count()` | `2` |
| 控制流 | `for` / `if` 后接裸尾 | 值 |
| 无值语句 | `int x = 5;` | 「成功但无值」 |
| **返回 `null`** | `string s = null; s` | `null`（**不是**「无值」）|
| void 调用（带 / 不带 `;`）| `Obj.Inc()` | 「成功但无值」，**副作用一次** |
| **副作用只发生一次** | 先 `x = 0;` 再 `x = x + 1;` 再读 `x` | `1`（不是 2）|
| 跨调用变量 | 上次定义的变量这次仍可用 | 值 |
| 声明 + 语句 | `class C {...}` ↵ `C.V()` | 值 |
| `using` + 语句 | `using System.Text;` ↵ 语句 | 值 |
| 交替 4 段 | `class` / 语句 / `class` / 裸尾 | 值 |
| 单独声明 | `class C {...}` | 「成功但无值」**（不是报错）** |
| 编译错误 | `class C {}` ↵ `int x = ;` | `isError=true`、行号是**原始行号** |
| 运行时异常 | `1/0` | `isError=true`、`Execution failed:` |
| 输入不完整 | `int x = ` | `isError=true`、`Incomplete input` |

> ⚠️ **前几行（普通语句）是最容易漏测的一类。** 这次改动看起来只针对「多提交」，
> 但它把**所有** snippet 的执行路径都换掉了（旧的「先 Evaluate 再 Run」兜底没了）。
> 只测「定义 class + 调用」会留下普通语句的回归洞；其中
> **副作用只发生一次** 与 **返回 `null` vs 无值** 两条分别是
> 「执行两次」和「哨兵值」的探针，不要省。

**这张表已经脚本化了** —— 不用每次手写：

```bash
python3 tools/mcp_selftest.py                # 全跑（默认 http://127.0.0.1:27015/mcp）
python3 tools/mcp_selftest.py -v             # 附上每条响应的原文
python3 tools/mcp_selftest.py --only 副作用    # 只跑名字含该子串的
```

一次跑完 **27 项（约 0.3 秒）**。退出码 `0` = 全过、`1` = 有失败、`2` = 连不上
（游戏没跑 / 27015 没监听）—— 所以它也能直接当「MCP 还活着吗」的探针。

> ⚠️ 它会 **reset 脚本会话状态**（开头一次、结尾一次），别在会话中途跑。
> 它跑的是普通语句 + 多提交 + 错误分类的全量回归，而不仅是你手头在查的那一条。
> 每个用例都**自带前置状态**（脚本开头先 reset 一次），所以用例顺序无关、
> 也能用 `--only` 单跑一条 —— 加新用例时请保持这一点。


---

## 4. 架构与代码位置

```
游戏进程
├── MelonMCPPlugin.cs         启动/关闭；RegisterTools() 里逐个 _server.RegisterTool(new XxxToolDefinition())
├── Server/MCPHttpServer.cs   HTTP/1.1 分帧 —— 唯一和网络打交道的文件
├── Server/MCPServer.cs       JSON-RPC 分发 + 工具注册表 + 主线程编组
└── Tools/*.cs                一个工具一个类，实现 IToolDefinition
```

**加一个工具**：写 `XxxToolDefinition : ToolDefinitionBase`，在 `MelonMCPPlugin.RegisterTools()` 里注册。
（注册处用 `#if MELONMCP_ENABLE_BROKEN_*` 关掉了几个坏工具 —— **修好后开宏即可，不要重写**。）

⚠️ **`RequiresMainThread` 默认 true**（排到 Unity 主线程执行）。只读原生内存 / 托管反射的工具**必须**显式改成 false —— 主线程忙或被卡死时主线程工具完全不可用，而那恰恰是最需要诊断能力的时候。

---

## 5. 传输层：Streamable HTTP（合并自原 `docs/mcp-http-transport.md`）

MCP 客户端直连游戏进程，**不需要任何 bridge 脚本**：

```yaml
- id: mcp-longyin
  name: '@deepseek-ai/dsh-mcp-client'
  config:
    serverName: longyin
    transport: streamable-http
    url: http://127.0.0.1:27015/mcp
```

以前用的是换行分隔的裸 TCP 监听，但 **MCP 客户端无法直连它**：stdio 绑定要求**客户端去拉起服务端进程**，
而服务端在游戏进程里 —— 所以裸 TCP 永远需要中间放一个翻译进程（即曾经的 `mcp-bridge.*` 脚本）。
换成协议自带的 HTTP 传输后，客户端只需一个 URL。

> ⚠️ **不要再把 `MCPHttpServer` 改回 `System.Net.HttpListener`。**
> 它在这里**根本不能用**，而且**不是我们代码的 bug**，是托管侧修不了的：
>
> **一、`HttpListener` 有两份互不相关的实现。** Windows 上是 `HTTP.sys` 内核驱动的薄封装；
> Linux/macOS 上是 Mono 版监听的托管重写。微软官方文档原话：
> *“its behavior and protocol support vary by platform because each platform uses a different
> underlying implementation.”* —— **两套不同的代码、不同的团队、不同的平台测试。**
>
> **二、Proton 下走的是 `HTTP.sys` 那条。** `RuntimeInformation` 在 Proton 里报 **`Win32NT`**，
> 于是 .NET 选了 HTTP.sys 分支，调进 **Wine 的 http.sys 模拟**。
> 在**本进程内实测**：加载的程序集含 `HttpListenerSession` / `RequestQueueHandle` /
> `ForceCancelRequest`，**不含** `HttpEndPointListener` / `HttpConnection` —— 即确实是 HTTP.sys 变体。
>
> ⚠️ **因此去读托管实现（`source.dot.net` 上的 `HttpConnection.SendError`、
> `_unregisteredConnections` 等）来推理本进程是错的** —— 它们不在这个程序集里。
> 本项目曾因此得出过两个自信但错误的诊断，引以为戒。
>
> **三、Wine 的 http.sys 是带 stub 的部分实现，而 stub 会 abort。** 上游实测：
> 响应长度**事先已知**时走 `HttpSendHttpResponse`（Wine 已实现）；
> 长度**未知**（chunked / 不给 Content-Length）时走 `httpapi.dll.HttpSendResponseEntityBody`，
> **而它在 Wine 里是 stub** —— 调到 stub 会让 Wine 打印
> `unimplemented function ... aborting` 并**干掉该请求**。
>
> **四、症状完全对得上**（换掉前实测）：Python / 手写 Node `http` 都能通（长度已知），
> 而 undici 发出一版请求后收到一个**我们代码从未生成过的 HTML 400**，
> 之后监听器**还在 accept、却永远不再回复**，而游戏本身健康、日志一直在推进。
>
> **结论：改回 `HttpListener` = 重新把 Wine http.sys 接回来。** 当前实现直接走
> `System.Net.Sockets`，彻底绕开 HTTP.sys。

**实现上必须守的两条（漏了就会重现故障）：**

1. **永远发准确的 `Content-Length`**，绝不 chunked —— 这是让它停在 Wine 已实现路径上的唯一办法。
   实现里写死在 `WriteBodyAsync`（包括 202 那种长度为 0 的情况）。
2. **永远 `Connection: close`，且真的关连接。** 否则 Node/undici 会等服务器结束 keep-alive 响应
   直到自己超时，报成 `fetch failed: other side closed` —— 看起来像服务器崩了。
   每个连接只服务**一个**请求，从根上避开 keep-alive / pipelining 状态。

**这是故意的极简 HTTP/1.1，只服务一个 POST endpoint**，不是通用 web 服务器：
不做 chunked 请求体（直接拒绝）、不做 keep-alive 复用、不做 pipelining、不做 TLS。
**它一旦需要这些之外的能力，那是重新考虑传输方案的信号，不是把它养成通用服务器的理由。**

**已验证**（真实 undici SDK，即之前失败的那个客户端）：
`initialize` → 协商出 `2025-03-26`；`tools/list` → 39 个工具、`inputSchema` 齐全；
`tools/call`（含走主线程队列的）✅；**5 个并发客户端 40/40 断言通过**；
分支 `200/202/400/403/404/405` 全部正确；
**空 body POST / 垃圾字节 / 半开连接 / 突然断开之后监听器依旧存活**（这正是以前会弄死它的那类输入）。

---

## 6. `execute_csharp` 的执行模型（`ScriptSession`）

### 6.1 三种结果必须能分辨（曾经的三个静默失败）

分类依据是 `ScriptResult` 带不带 `Exception`（`CompileError` 不带、`RuntimeError` 带），
**两种失败的文案也故意不同** —— 别让它们共用一句。

**❗ 三个曾经的陷阱（已修；旧会话/旧文档里可能还留着错经验）：**

1. **「一整条超长单行表达式静默无结果」→ 拆成两条**
2. **「循环体复杂就静默无结果」→ 用 `reset=true`**
3. **「只看到 `(行,列): error CS....`，但工具报成功」→ 以为代码跑过了**

**前两个是同一个 bug，第三个是另一个**；三者的根因都是
`Mono.CSharp` 的 `Evaluate()` / `Run()` **根本无法表达「编译失败」**：

```csharp
// 旧代码（ScriptSession.Run）
value = _evaluator.Evaluate(code);
hasValue = true;          // ← 无条件置 true，哪怕 value 是 null
...
_evaluator.Run(code);     // ← 返回 false 也不看，把诊断当 output 返回
```

- **陷阱 1、2**：`Evaluate()` 对返回 void 的调用**不报错，只返回 `null`**
  （例：`System.Console.WriteLine("x")`）。于是 `hasValue = true` + `value = null`
  → 下面那个 `if (!hasValue)` **不成立** → **整个语句模式被跳过** →
  得到一句与「真的无值」完全相同的 `Execution completed (no result).`，
  **既不报错、也无栈** —— 看起来就像“我查询写错了”。
- **陷阱 3**：`Evaluate()` 与 `Run()` **编译失败时也不抛异常**
  （`Evaluate` 回 `null`、`Run` 回 `true`），而旧代码只 `catch` 异常 →
  编译错误**被当成成功的 output 返回**（`isError` 还是 false），
  调用方根本不会去读它。

**修法**：不再用它们的返回值判断成败，改用**编译器自己的信号** ——
`Evaluator.Compile(text, out CompiledMethod)` 配 report printer 的 `ErrorsCount`。
判据与实现见下一节。


### 6.2 一个 snippet 可以包含多个「提交」

**症状**：在 `execute_csharp` 里先写 `class Foo { ... }` 再跟一句 `Foo.Bar()`，
报 `CS1525: Unexpected symbol 'Foo'`，必须拆成两次调用（`reset` 也没用 ——
它会把刚定义的 class 清掉）。这会让模型反复重试。

**根因（读 Mono.CSharp 源码 + 活进程实测确认）**：交互式编译器
**只用输入的第一个 token 决定解析模式**（`Evaluator.ToplevelOrStatement`）：

| 第一个 token | 解析成 | 接受什么 |
|---|---|---|
| `class` / `struct` / `enum` / `interface` / `namespace` / `using X` | **compilation unit** | **只有声明**，遇到语句就 CS1525 停下 |
| 其它（语句 / 表达式） | **statements** | 只有语句，遇到声明则报错 |

所以「声明 + 语句」**无论谁在前**都会被拒 —— 但**报错位置恰好就是分界点**。
`ScriptSession.RunAsSubmissions` 就用这个位置分段，逐段交给编译器：

```
Compile(整个输入) → 报错 (2,0)
  ├─ 前缀 = 第 1..1 行   → Compile 成功 → 段 1
  └─ 剩余 = 第 2..n 行   → 再试，如此循环
最后一段的值就是整个 snippet 的值
```

**七条实现要点（改这段代码前必读）**：

1. **成败信号是 `ErrorsCount`，不是 `compiled == null`。**
   只声明类型的提交**本来就没有可执行方法**，`Compile` 会返回 `compiled == null`
   而 `ErrorsCount == 0`；拿它当失败会把 `class Foo {}` 变成报错。
2. **只在整段编译失败时才尝试分段**，所以原本能跑的代码语义不变。
3. **前缀必须自己也能编译通过才认**。否则放弃分段并回报编译错误（优先报更具体的
   那条）—— 猜错只能「没帮上忙」，不会悄悄改变语义。
4. **分界点用编译器给的 `Location`（行/列）换算**。列是 1-based，
   **列 0 = 行首**（跨行分界就是这个形状）。`Location` 的列只有 8 bit，
   所以超长单行会回绕 —— 靠第 3 条挡住。
5. **不要真的切掉前缀，而是把它抹成空白**（保留换行）——
   这样后续每段仍带着**原始行号**，报错指向用户真正写的那一行。
6. **一个提交只编译一次、只执行一次**。旧实现是先 `Evaluate` 再 `Run`，
   所以**没有值可返回的语句会被执行两次**（实测 `N++` 一次调用后 `N == 2`）。
   现在直接调用 `Compile` 交回来的 `CompiledMethod` —— 与
   `Evaluator.Evaluate` 内部用的是同一个调用。
7. **只有最后一段的值算整个 snippet 的值**，与「裸尾表达式」一致。

实测（2026-10，活进程，逐步拆解验证）：整段 `err=1, (2,0)`；
偏移算出 50 == `IndexOf("Foo.Bar()")`；前缀 `err=0, compiled=null`（只声明，
`class Foo` 已生效）；剩余段 `err=0, compiled!=null`，调用拿回 7。


### 6.3 扩展方法（LINQ）首次调用曾经失效

**症状**：`reset` 后的**第一次** LINQ 扩展方法调用（`x.Count()` / `x.Where(...)`）
返回「成功但无值」，**第二次**才正常。非扩展方法（`x.Length`）不受影响。

**不是** using 缺失：`System.Linq.Enumerable.Count(x)` 静态调用一直正常，
说明命名空间与程序集都在。**是首次编译尚未建立扩展方法查找**。

**修法**：会话初始化时跑一次丢弃的 `(new int[]{1}).Count()` 预热
（`ScriptSession.WarmUpExtensionMethods`）。

**踩坑记录（我改错过两次）**：一度以为要调 `Evaluator.ImportTypes(...)`，
结果 LINQ 全线报 `CS0121: ambiguous`，**且列出的两个签名一模一样**（看着像编译器 bug）。
根因：`ReflectionImporter.ImportAssembly` **内部已经**调
`ImportTypes(..., importExtensionTypes: true)` —— 再调一次就是重复注册。
另：`System.Core` / `netstandard` 都转发 `System.Linq.Enumerable`，
**一起引用会同样重复**；只有定义它的那个程序集能导入。

---

## 7. 工具 TODO 与已否决项

> 原本记在 `docs/melonmcp-tooling-requests.md`。那份清单里的 P0 与踩坑表
> **已全部落地**并分别归入 [`runtime-probing.md`](runtime-probing.md) §7.1 / §7.1.1，该文件已删除；剩下的留在这里。
>
> **状态含义：** ⏸️ = 想做但没做（真 TODO）· ❌ = 已验证不可行，别再试

| 项 | 想解决的问题 | 状态 |
|---|---|---|
| `heap_objects` | 枚举某类型的**活对象** | ⏸️ **TODO**。导出存在且能跑（实测 3169 对象 / 86 ms），但**回调里做类型筛选会爆栈**。要先解决「在原生侧拿到 klass 指针并按类型过滤」，详见下方 |
| `hook_patch_info` 的 `hitCount` | 「补丁挂上了，但到底触发了几次」 | ❌ **已否决** —— 实现必然自指，反而破坏它要回答的问题，详见下方。它同时否决了 `count_calls` |
| `count_calls` | 不写代码就能数某函数被调用了几次 | ❌ 同上，同一个难点 |
| `search_pseudocode` 的**转发桩过滤** | 它会把 IL2CPP 转发桩当结果返回，**噪音很大** | ⏸️ **TODO**。方法体只有 `il2cpp_runtime_invoke` 的桩应被过滤。目前**无过滤**（`GameKnowledgeTools.cs` 里搜不到相关逻辑）。找调用关系用 `tools/find_callers` 更有效 |

### ⏸️ `heap_objects` 的接手须知

**如果要接着做，按这个顺序，不要一步到位：**

1. **先写一个只数不取名的版本**，确认回调能安全跑满全程（本轮已验过：3169 对象 / 86 ms，这步其实已过）。
2. 再解决「怎么在回调里拿到 `klass`」—— 关键是**必须在原生侧比较**：预先取好函数指针，直接比 `klass` 指针，**不要转成字符串**（`klass_name` 返回 `char*`，但 `Marshal.PtrToStringAnsi` 就是分配，就是爆栈）。
3. 最后才是按名字查：可以先把目标类型的 `klass*` 算好，回调里只做一次指针比较。

⚠️ **试错代价是一次游戏崩溃**（本轮实测）。所以每步都要先用小样本验证。

> 如果只是「找出某个类的活对象」，**先考虑更安全的替代路径**：走游戏自己的容器
> （如 `BattleController.battleMapData.GetGridData(r,c)`），或用已验证的
> `watch_field` / `find_objects_of_type`。GC 堆遍历只在
> 「不知道对象在哪、也找不到容器」时才不可替代。
### ⚠️ 为什么 `hitCount` 在 IL2CPP 下做不了（已验证，别再试）

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

### ✅ 替代方案：让 mod 自己维护一个**不受门控**的计数器

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

### ⏸️ P2 一批（未开工，优先级低）

| 项 | 用途 |
|---|---|
| `snapshot` / `diff` | 给一批对象/字段拍快照，之后 diff，自动化「某操作前后变了什么」 |
| `log_mark` | 往日志插一个显眼锚点，方便把日志时间轴与 MCP 操作对齐 |
| `break_on` / `run_until` | REPL 里的轻量断点，不用切 gdb |
| `stack_trace_native` | 拿原生栈帧。MelonLoader 0.7 有 `NativeStackWalk`，但 **Windows-only 且首次要下 PDB**，Proton 下行为待验证 |
| `register_dump` | 一次性 dump 通用寄存器 + `rip` 附近反汇编，等价于 gdb 的 `info registers` + `x/4i $pc` |

> 💡 其中 `register_dump` 与本项目的崩溃排查最契合（§7.4），可优先考虑。
### ⚠️ `heap_objects` 实测记录：导出能用，但回调会爆栈

本轮在活进程里真试了。结论分两半，**两半都有用**。

**先说好消息（你的疑问的答案）：C++ `new` 的东西能不能搜到？**

→ **IL2CPP 下没有「C++ new 出来的托管对象」这回事。** C# 的 `class` 编译后就是一个
`Il2CppObject`，由 IL2CPP 的 GC 分配 —— **「C++ new」与「托管堆」是同一条分配路径**。
所以 GC 堆遍历当然能扫到它们。真正搜不到的是**纯原生内存**（IL2CPP 运行时的 malloc/STL、
Unity 引擎侧的原生对象），那些不是 `Il2CppObject`，永远不会出现在遍历里。

**导出确实存在且能跑**（实测）：

```
objdump -p GameAssembly.dll | grep il2cpp_gc_foreach_heap
  [ 110] +base[ 111]  006e il2cpp_gc_foreach_heap      # 注意名字顺序
```

> ⚠️ 我之前写的 `il2cpp_gc_heap_foreach` **是错的**，正确的是 **`il2cpp_gc_foreach_heap`**
> （foreach 在前）。按错误名字找会得 0 命中，然后误以为「IL2CPP 没导出」。
> 另：`Il2CppInterop.Runtime` **没有**绑定它（`strings` 搜不到），必须自己 P/Invoke / GetExport。

实测结果（`MiniDumpNormal` 之外，直接调）：

| 项 | 值 |
|---|---|
| 遍历到的对象数 | **3169 个** |
| `il2cpp_gc_get_used_size()` | **343109632**（343 MB） |
| 单次遍历耗时 | **86 ms** |

回调可用 `Marshal.GetFunctionPointerForDelegate` 传。

**再说坏消息：按类型名筛选会爆栈。**

我想在回调里用 `IL2CPP.il2cpp_object_get_class` + `il2cpp_class_get_name_` 拿类型名，
通过反射调用（因为 `Il2CppInterop` 的那两个方法在热循环里）。
结果：**主线程栈溢出，游戏直接死**。

事后用刚建好的 minidump 工具确认（`crash_1791052641.dmp`）：

```
主线程栈: 82.4 KiB used / 82.4 KiB total    ← 一点不剩，rsp 已在栈底
```

根因：**这个回调跑在 GC 的栈上，不是普通托管线程的栈**。在回调里做任何重活
（反射、字符串分配、甚至只是调几个托管方法）都会把那个栈压穿。

**所以 `heap_objects` 的正确形状是：**

- ✅ 回调用**纯原生代码**（预先取好函数指针，不要反射、不分配）
- ✅ **过滤条件也必须是原生侧比较**（比如直接比较 `klass` 指针，而不是比字符串）
- ⚠️ 遍历本身很快（86 ms / 3169 对象），但**不要在回调里做任何非平凡的事**
- ❌ **不要在回调里调用托管方法** —— 包括看起来无害的 `IPAddress` / `string`

> 💡 这次踩坑本身有价值：它同时证明了 §7.4 的 minidump 链路
> （采集 → 。pdata 回溯 → 栈用量分析）**在真实崩溃上是好用的** —— 上一次
> 只能拿到 `FailFast` 的假栈，这次是真正由自己的代码造成的崩溃，而且
> **从栈用量一眼就能读出根因**。

> 💡 也再次验证了一条老纪律：**先探测能力边界，再写工具**。
> 本轮真正的产出不是 `heap_objects`，而是「IL2CPP 下 C++ new 与托管堆是同一回事」
> 这个认知，以及一个可复用的爆栈教训。

---
