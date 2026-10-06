# MelonMCP 的 Streamable HTTP 传输

> 归属：[`AGENTS.md`](../AGENTS.md) §7 索引。
> 场景：**给 MelonMCP 加/改传输层，或 MCP 客户端连不上时**。
> 工具本身的用法与踩坑见 [`runtime-probing.md`](runtime-probing.md) §7.1。

---

## 为什么必须自己写 HTTP —— 不要改回 System.Net.HttpListener

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
