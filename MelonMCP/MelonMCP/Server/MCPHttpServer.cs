using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace MelonMCP.Server
{
    /// <summary>
    /// MCP "Streamable HTTP" transport, served straight out of the game process.
    ///
    /// WHY THIS EXISTS: an MCP client launches a stdio server as a subprocess - that process
    /// lifecycle is part of the stdio binding and cannot be negotiated away, so a plain TCP listener
    /// can never be connected to directly. It always needs a translation process in between, which
    /// is the sole reason the mcp-bridge.py/js/ps1 scripts exist. Speaking the protocol's HTTP
    /// transport removes that process: the client is configured with a URL and nothing else.
    ///
    /// WHAT IT REUSES: everything except framing. JSON-RPC dispatch, the tool registry and main-thread
    /// marshalling all stay in MCPServer.HandleRequest, so both this and any future transport serve
    /// the same tools with the same behaviour.
    ///
    /// ─── Spec conformance ───────────────────────────────────────────────────────────────────────
    /// Implements the Streamable HTTP transport at revision 2025-03-26 and earlier:
    ///
    ///   • ONE endpoint path, POST only, one JSON-RPC message per POST.
    ///   • A request gets `Content-Type: application/json` with a single JSON object. The SSE
    ///     alternative is optional - the client must support both, the server picks one - and JSON is
    ///     right here because no tool emits progress notifications or server-initiated requests.
    ///   • A notification (no id) gets `202 Accepted` with NO body.
    ///   • `MCP-Protocol-Version` is validated; an unsupported value is rejected with 400 and an
    ///     `UnsupportedProtocolVersion` error advertising what is supported.
    ///   • `Origin`, when PRESENT, must be loopback; otherwise 403.
    ///   • Loopback-only bind.
    ///
    /// NOT implemented, on purpose:
    ///   • SSE response streams and the standalone GET stream. Both optional, and this server pushes
    ///     nothing. GET is answered 405, which is what the spec prescribes for a server offering no
    ///     GET stream.
    ///   • Protocol-level sessions (`Mcp-Session-Id`). Optional since 2025-06-18, removed in
    ///     2026-07-28. This server is stateless and returns no session id, which the spec allows.
    ///   • `x-mcp-header` tool-parameter mirroring (a server MAY).
    ///
    /// ═══ WHY THIS IS A HAND-WRITTEN HTTP/1.1 SERVER AND NOT System.Net.HttpListener ═══════════════
    ///
    /// The obvious implementation - and the one this file started as - is HttpListener. It does not
    /// work here, and the reason is not a bug in our code. It is unfixable from managed code:
    ///
    ///   • System.Net.HttpListener has TWO unrelated implementations. On Windows it is a thin
    ///     wrapper over the kernel HTTP.sys driver; on Linux/macOS it is a managed rewrite of Mono's
    ///     listener. Microsoft documents this ("its behavior and protocol support vary by platform
    ///     because each platform uses a different underlying implementation").
    ///
    ///   • Under Proton/Wine, RuntimeInformation reports Win32NT, so .NET selects the **HTTP.sys**
    ///     branch and calls into Wine's http.sys emulation. Verified in this very process:
    ///     the loaded assembly contains HttpListenerSession / RequestQueueHandle /
    ///     ForceCancelRequest, and contains NO HttpEndPointListener / HttpConnection - i.e. it is
    ///     the HTTP.sys variant, not the managed one. Reading the managed implementation's source to
    ///     reason about this process is therefore meaningless; they are different programs.
    ///
    ///   • Wine's HTTP.sys is a partial implementation with stubs. MEASURED upstream: a response
    ///     whose length is not known up front is sent through `httpapi.dll.HttpSendResponseEntityBody`
    ///     - which Wine stubs, and a call to a stubbed function makes Wine print
    ///     "unimplemented function ... aborting" and kill the request. When the length IS known it
    ///     goes through `HttpSendHttpResponse`, which Wine implements properly.
    ///
    ///   • The observed failure matches exactly: plain Python/Node http clients worked (known
    ///     Content-Length), while undici's request produced an HTML 400 that our code never
    ///     generated, and thereafter the listener accepted connections but never answered again -
    ///     while the game itself stayed perfectly healthy and the log kept advancing.
    ///
    /// So this server talks `System.Net.Sockets` directly, bypassing HTTP.sys entirely. That is also
    /// why it emits an EXACT Content-Length and closes the connection every time: those are the two
    /// properties that keep the exchange inside the part of the stack that is known to work.
    ///
    /// ⚠️ SCOPE - this is deliberately a minimal HTTP/1.1 server for ONE POST endpoint:
    ///   • No chunked transfer encoding, no keep-alive reuse, no pipelining, no TLS.
    ///   • Chunked request bodies are REJECTED rather than parsed (see ReadBodyAsync).
    ///   • Anything outside the supported subset gets an explicit error response, never a silent
    ///     mis-parse.
    /// It is not a general-purpose web server and must not be grown into one; the moment it needs
    /// features beyond this list, that is the signal to revisit the transport decision instead.
    /// </summary>
    internal sealed class MCPHttpServer
    {
        private readonly MCPServer _server;
        private readonly int _port;
        private readonly string _path;

        /// <summary>
        /// Origins accepted on incoming requests.
        ///
        /// MEASURED, and the reason this is not "reject everything": an MCP client that is a native
        /// process (the DSH harness, Claude Code, an SDK program) sends NO Origin header at all,
        /// while a browser-based client always does. Rejecting absent Origins would block the normal
        /// case; accepting arbitrary ones would leave the DNS-rebinding hole the spec warns about.
        /// So: absent is allowed, present must be loopback.
        /// </summary>
        private static readonly string[] AllowedOriginHosts = { "localhost", "127.0.0.1", "[::1]", "::1" };

        /// <summary>Cap on a single request, to bound what an accidental huge POST can allocate.</summary>
        private const int MaxRequestBodyBytes = 32 * 1024 * 1024;

        /// <summary>
        /// Cap on the request line + headers block.
        ///
        /// A request whose headers never terminate would otherwise be read forever. This is a real
        /// failure mode rather than a theoretical one: an HTTP client that half-opens a connection
        /// and stalls would tie up a worker until the read timed out.
        /// </summary>
        private const int MaxHeaderBytes = 64 * 1024;

        private TcpListener _listener;
        private CancellationTokenSource _cancellation;
        private Task _acceptLoop;

        public bool IsRunning { get; private set; }

        /// <summary>The URL an MCP client should be configured with.</summary>
        public string EndpointUrl => $"http://127.0.0.1:{_port}{_path}";

        public MCPHttpServer(MCPServer server, int port, string path)
        {
            _server = server;
            _port = port;

            // Normalize to one leading slash and no trailing slash so "/mcp", "mcp" and "/mcp/" all
            // configure the same endpoint. Requests are matched against this exact string, so an
            // un-normalized value silently stops matching rather than erroring.
            var p = string.IsNullOrWhiteSpace(path) ? "/mcp" : path.Trim();
            if (!p.StartsWith("/", StringComparison.Ordinal)) p = "/" + p;
            _path = p.TrimEnd('/');
            if (_path.Length == 0) _path = "/mcp";
        }

        public void Start()
        {
            if (IsRunning) return;

            // ★ 127.0.0.1, never IPAddress.Any: the spec says bind loopback for a local server, and
            // this endpoint can rewrite any mod's config and execute arbitrary C#. Binding 0.0.0.0
            // would expose that to the whole network.
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Start();

            _cancellation = new CancellationTokenSource();
            IsRunning = true;

            // ★ Task.Run, NOT a direct call. Accepting blocks, and blocking the Unity main thread
            // freezes the game - measured earlier in this project: 0 frames for 80+ seconds,
            // recoverable only by restarting the process.
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_listener, _cancellation.Token));
        }

        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;

            _cancellation?.Cancel();

            // Capture then null the field BEFORE stopping: the accept loop re-reads _listener, and
            // nulling it while the loop is parked in AcceptTcpClientAsync turns the expected
            // ObjectDisposedException into a NullReferenceException that skips the clean exit.
            var listener = _listener;
            _listener = null;

            try { listener?.Stop(); } catch { }

            var loop = _acceptLoop;
            _acceptLoop = null;
            if (loop != null)
            {
                try
                {
                    // Bounded: a hung accept loop must not block teardown. The socket is released by
                    // Stop() above regardless, which is what a hot reload actually needs.
                    if (!loop.Wait(TimeSpan.FromSeconds(5)))
                        MelonMCPPlugin.Logger?.Warning("HTTP accept loop did not stop within 5s.");
                }
                catch (Exception ex)
                {
                    MelonMCPPlugin.Logger?.Warning($"HTTP accept loop did not stop cleanly: {ex.Message}");
                }
            }

            _cancellation?.Dispose();
            _cancellation = null;
        }

        private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
        {
            while (!token.IsCancellationRequested && listener != null)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync();
                }
                catch (ObjectDisposedException) { break; }   // listener stopped
                catch (SocketException) { break; }
                catch (InvalidOperationException) { break; }
                catch (Exception ex)
                {
                    if (!IsRunning || token.IsCancellationRequested) break;

                    // A single failed accept must not kill the loop. Anything that reaches here is
                    // unexpected, so it is logged rather than swallowed - silence would look from
                    // the outside exactly like the listener having died.
                    MelonMCPPlugin.Logger?.Warning($"HTTP accept error: {ex.Message}");
                    continue;
                }

                // Handle off the accept loop so one slow tool call - a main-thread tool waiting on a
                // wedged game, say - cannot block every other client. Requests are independent
                // because the server is stateless.
                _ = Task.Run(() => HandleClientAsync(client));
            }
        }

        /// <summary>
        /// Serves exactly one request on <paramref name="client"/> and closes the connection.
        ///
        /// One request per connection is a deliberate simplification, not an oversight: it removes
        /// keep-alive state, request pipelining and connection reuse from the problem entirely, and
        /// it is what makes the response shape predictable. MCP clients open a connection per POST.
        /// </summary>
        private async Task HandleClientAsync(TcpClient client)
        {
            try
            {
                client.NoDelay = true;

                using (client)
                using (var stream = client.GetStream())
                {
                    // Bounded read timeout: a client that connects and then says nothing must not
                    // hold a worker forever.
                    stream.ReadTimeout = 30000;
                    stream.WriteTimeout = 30000;

                    await ProcessAsync(stream);
                }
            }
            catch (Exception ex)
            {
                MelonMCPPlugin.Logger?.Warning($"HTTP request failed: {ex.Message}");
                try { await TryFailAsync(client, 500, JsonRpcError.InternalError(ex.Message)); } catch { }
            }
        }

        private async Task ProcessAsync(NetworkStream stream)
        {
            // --- Read the head ---------------------------------------------------------------
            var head = await ReadHeadAsync(stream);
            if (head == null)
            {
                await WriteBodyAsync(stream, 400, "text/plain", "Malformed HTTP request.\n");
                return;
            }

            var requestLine = head.RequestLine;
            var parts = requestLine.Split(' ');
            if (parts.Length != 3)
            {
                await WriteBodyAsync(stream, 400, "text/plain", "Malformed HTTP request line.\n");
                return;
            }

            var method = parts[0];
            var rawTarget = parts[1];

            // --- Origin validation (spec: MUST; DNS-rebinding defence) -----------------------
            var origin = head.Headers.TryGetValue("Origin", out var o) ? o : null;
            if (!string.IsNullOrEmpty(origin) && !IsAllowedOrigin(origin))
            {
                MelonMCPPlugin.Logger?.Warning($"Rejected request with disallowed Origin '{origin}'.");
                await WriteJsonAsync(stream, 403, new JsonRpcResponse
                {
                    Id = null,
                    Error = JsonRpcError.InvalidRequest(
                        $"Origin '{origin}' is not allowed. This server accepts loopback origins only.")
                });
                return;
            }

            // --- Path routing ----------------------------------------------------------------
            var path = rawTarget;

            // Strip a query string; the endpoint has no query parameters and a client is entitled
            // to append one. An absolute-form target ("http://host/mcp") is legal in HTTP/1.1 as
            // well, so reduce it to its path rather than failing to match.
            var q = path.IndexOf('?');
            if (q >= 0) path = path.Substring(0, q);
            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                if (Uri.TryCreate(path, UriKind.Absolute, out var abs)) path = abs.AbsolutePath;
            }

            if (!string.Equals(path.TrimEnd('/'), _path, StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(stream, 404, new JsonRpcResponse
                {
                    Id = null,
                    Error = JsonRpcError.MethodNotFound(
                        $"Unknown path '{path}'. The MCP endpoint is '{_path}'.")
                });
                return;
            }

            // --- Method routing --------------------------------------------------------------
            // The GET stream is optional and unimplemented; 405 is the spec's prescribed answer and
            // is deliberately distinguishable from 404 ("wrong URL").
            if (!string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                await WriteBodyAsync(stream, 405, "text/plain",
                    "Only POST is supported on the MCP endpoint. This server offers no SSE stream.\n",
                    extraHeaders: new[] { "Allow: POST" });
                return;
            }

            // --- Protocol version validation -------------------------------------------------
            var protocolError = ValidateProtocolVersion(head, out var negotiatedVersion);
            if (protocolError != null)
            {
                await WriteJsonAsync(stream, 400, protocolError);
                return;
            }

            // --- Body ------------------------------------------------------------------------
            string body;
            try
            {
                body = await ReadBodyAsync(stream, head);
            }
            catch (Exception ex)
            {
                await WriteJsonAsync(stream, 400, new JsonRpcResponse
                {
                    Id = null,
                    Error = JsonRpcError.ParseError(ex.Message)
                });
                return;
            }

            if (string.IsNullOrWhiteSpace(body))
            {
                await WriteJsonAsync(stream, 400, new JsonRpcResponse
                {
                    Id = null,
                    Error = JsonRpcError.ParseError("Empty request body.")
                });
                return;
            }

            // One message per POST (spec). Reject a batch explicitly rather than silently handling
            // only the first element, which would look like data loss to the caller.
            if (body.TrimStart().StartsWith("[", StringComparison.Ordinal))
            {
                await WriteJsonAsync(stream, 400, new JsonRpcResponse
                {
                    Id = null,
                    Error = JsonRpcError.InvalidRequest(
                        "JSON-RPC batching is not supported on this endpoint; send one message per POST.")
                });
                return;
            }

            JsonRpcRequest rpcRequest;
            try
            {
                rpcRequest = JsonConvert.DeserializeObject<JsonRpcRequest>(body, MCPProtocol.JsonSettings);
            }
            catch (JsonException ex)
            {
                await WriteJsonAsync(stream, 400, new JsonRpcResponse
                {
                    Id = null,
                    Error = JsonRpcError.ParseError($"Invalid JSON: {ex.Message}")
                });
                return;
            }

            if (rpcRequest == null || string.IsNullOrEmpty(rpcRequest.Method))
            {
                await WriteJsonAsync(stream, 400, new JsonRpcResponse
                {
                    Id = null,
                    Error = JsonRpcError.InvalidRequest("Body is not a JSON-RPC request with a 'method'.")
                });
                return;
            }

            // --- Notification vs request -----------------------------------------------------
            // A JSON-RPC notification has no id. The spec requires 202 with no body for these.
            if (rpcRequest.Id == null)
            {
                try { _server.HandleRequest(rpcRequest, negotiatedVersion); }
                catch (Exception ex) { MelonMCPPlugin.Logger?.Warning($"Notification '{rpcRequest.Method}' failed: {ex.Message}"); }

                await WriteBodyAsync(stream, 202, null, string.Empty);
                return;
            }

            // --- Dispatch --------------------------------------------------------------------
            // The negotiated revision is passed IN rather than stored on the server: requests are
            // handled concurrently, so a field would let two overlapping initialize calls read each
            // other's value and answer with a version the client never proposed.
            var rpcResponse = _server.HandleRequest(rpcRequest, negotiatedVersion);

            // A request that produced no response would hang the client. Synthesize an empty result
            // rather than returning 204, which the client would read as a transport error.
            if (rpcResponse == null)
            {
                rpcResponse = new JsonRpcResponse { Id = rpcRequest.Id, Result = new { } };
            }

            await WriteJsonAsync(stream, 200, rpcResponse);
        }

        /// <summary>A parsed HTTP request head: the request line plus headers, in original order.</summary>
        private sealed class HttpHead
        {
            public string RequestLine;
            public readonly Dictionary<string, string> Headers =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            /// <summary>The raw Content-Length value, still unparsed so a bad one can be rejected.</summary>
            public string ContentLengthRaw;
        }

        /// <summary>
        /// Reads the request line and headers, stopping at the blank line.
        ///
        /// Reads byte-by-byte rather than line-by-line on a StreamReader: a StreamReader buffers,
        /// which would consume bytes belonging to the body and desynchronise the connection - the
        /// classic "headers parsed fine, body is truncated" failure.
        /// </summary>
        private static async Task<HttpHead> ReadHeadAsync(NetworkStream stream)
        {
            var head = new HttpHead();
            var line = new StringBuilder();
            var total = 0;
            var first = true;
            var done = false;

            var buffer = new byte[1];

            while (!done)
            {
                line.Clear();

                // --- one line, terminated by CRLF (bare LF tolerated) ---
                while (true)
                {
                    int n;
                    try { n = await stream.ReadAsync(buffer, 0, 1); }
                    catch (IOException) { return null; }
                    catch (ObjectDisposedException) { return null; }

                    if (n <= 0)
                    {
                        // The peer closed or reset. Nothing to answer on a socket that is already
                        // gone, so report the head as unavailable and let the caller close.
                        return null;
                    }

                    total++;
                    if (total > MaxHeaderBytes) return null;

                    if (buffer[0] == (byte)'\n') break;
                    if (buffer[0] == (byte)'\r') continue;
                    line.Append((char)buffer[0]);
                }

                var text = line.ToString();

                if (first)
                {
                    head.RequestLine = text;
                    first = false;
                    continue;
                }

                if (text.Length == 0) { done = true; break; }   // blank line ends the head

                var colon = text.IndexOf(':');
                if (colon <= 0) return null;

                var name = text.Substring(0, colon).Trim();
                var value = text.Substring(colon + 1).Trim();

                if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    head.ContentLengthRaw = value;
                }
                else if (string.Equals(name, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                {
                    // We deliberately do not decode chunked bodies. Rejecting is the honest answer:
                    // silently treating a chunked body as if it were the raw payload would hand the
                    // JSON parser a stream it cannot read and report a confusing parse error.
                    if (value.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
                        throw new NotSupportedException(
                            "Chunked request bodies are not supported; send an explicit Content-Length.");
                }

                // Duplicate headers: keep the first, per HTTP's general rule for Content-Length and
                // the practical behaviour of every client we accept.
                if (!head.Headers.ContainsKey(name)) head.Headers[name] = value;
            }

            return head;
        }

        /// <summary>
        /// Reads exactly Content-Length bytes of body.
        ///
        /// No Content-Length means no body, which is legitimate and common - but for POST it is far
        /// more often a client error, so the caller turns an empty body into a clear 400 rather than
        /// guessing.
        /// </summary>
        private static async Task<string> ReadBodyAsync(NetworkStream stream, HttpHead head)
        {
            if (head.ContentLengthRaw == null) return string.Empty;

            if (!long.TryParse(head.ContentLengthRaw, System.Globalization.NumberStyles.None,
                              System.Globalization.CultureInfo.InvariantCulture, out var length))
            {
                throw new FormatException($"Invalid Content-Length '{head.ContentLengthRaw}'.");
            }

            if (length < 0) throw new FormatException("Negative Content-Length.");
            if (length > MaxRequestBodyBytes)
            {
                throw new FormatException(
                    $"Request body of {length} bytes exceeds the {MaxRequestBodyBytes}-byte limit.");
            }

            if (length == 0) return string.Empty;

            var buf = new byte[length];
            var read = 0;
            while (read < length)
            {
                var n = await stream.ReadAsync(buf, read, (int)(length - read));
                if (n <= 0) throw new IOException("Connection closed while reading the request body.");
                read += n;
            }

            return Encoding.UTF8.GetString(buf);
        }

        /// <summary>
        /// Validates the `MCP-Protocol-Version` header against the revisions this server speaks.
        ///
        /// Returns null when the request may proceed, and sets <paramref name="negotiatedVersion"/>
        /// to the revision to answer under.
        /// </summary>
        private static JsonRpcResponse ValidateProtocolVersion(HttpHead head, out string negotiatedVersion)
        {
            var header = head.Headers.TryGetValue("MCP-Protocol-Version", out var h) ? h : null;

            if (string.IsNullOrWhiteSpace(header))
            {
                // Spec: a server MAY treat a missing header as the pre-2025-06-18 behaviour, for
                // clients written before the header existed. Strictly more compatible, and it cannot
                // affect a client that does send it.
                negotiatedVersion = MCPProtocol.LegacyVersionWithoutHeader;
                return null;
            }

            header = header.Trim();

            if (MCPProtocol.SupportedProtocolVersions.Contains(header, StringComparer.Ordinal))
            {
                negotiatedVersion = header;
                return null;
            }

            negotiatedVersion = null;

            // Spec: 400 plus an UnsupportedProtocolVersion error listing what IS supported, so the
            // client can retry with one of them. The code is in JSON-RPC's implementation-defined
            // range (-32000..-32099); what a client actually reads is `data.supported`.
            return new JsonRpcResponse
            {
                Id = null,
                Error = new JsonRpcError
                {
                    Code = -32000,
                    Message = $"Unsupported MCP protocol version '{header}'.",
                    Data = new
                    {
                        type = "UnsupportedProtocolVersion",
                        requested = header,
                        supported = MCPProtocol.SupportedProtocolVersions
                    }
                }
            };
        }

        /// <summary>
        /// True when the Origin is loopback.
        ///
        /// Only the HOST is checked, not port or scheme: a local client's Origin has no routing
        /// meaning, and the threat being defended against is a REMOTE page (http://evil.example)
        /// whose name resolves to 127.0.0.1 - which this rejects.
        /// </summary>
        private static bool IsAllowedOrigin(string origin)
        {
            try
            {
                if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
                return AllowedOriginHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static Task WriteJsonAsync(NetworkStream stream, int status, JsonRpcResponse payload)
        {
            var json = JsonConvert.SerializeObject(payload, MCPProtocol.JsonSettings);
            return WriteBodyAsync(stream, status, "application/json", json);
        }

        /// <summary>
        /// Writes a complete, self-delimiting HTTP/1.1 response.
        ///
        /// ★★ THE TWO PROPERTIES THAT MATTER, AND WHY:
        ///
        /// 1. ALWAYS an exact Content-Length, never chunked. Under Proton this is not a preference
        ///    but a correctness requirement - Wine routes a known-length response through
        ///    HttpSendHttpResponse (implemented) and an unknown-length one through
        ///    HttpSendResponseEntityBody (a stub that aborts the request). See the class comment.
        ///
        /// 2. ALWAYS `Connection: close`, and the socket really is closed by the caller. A client
        ///    (Node/undici in particular) otherwise waits for the server to end a kept-alive
        ///    response until its own timeout, surfacing as "fetch failed: other side closed", which
        ///    reads like a server crash. Declaring close up front, and honouring it, avoids the
        ///    entire class of confusion.
        /// </summary>
        private static async Task WriteBodyAsync(
            NetworkStream stream, int status, string contentType, string body,
            string[] extraHeaders = null)
        {
            var payload = body == null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(body);

            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(ReasonPhrase(status)).Append("\r\n");

            if (contentType != null)
                sb.Append("Content-Type: ").Append(contentType).Append("\r\n");

            // Exact length, always - including the 202 case, where the length is 0.
            sb.Append("Content-Length: ").Append(payload.Length).Append("\r\n");
            sb.Append("Connection: close\r\n");

            if (extraHeaders != null)
                foreach (var h in extraHeaders)
                    sb.Append(h).Append("\r\n");

            sb.Append("\r\n");

            var headBytes = Encoding.ASCII.GetBytes(sb.ToString());
            await stream.WriteAsync(headBytes, 0, headBytes.Length);

            if (payload.Length > 0)
                await stream.WriteAsync(payload, 0, payload.Length);

            await stream.FlushAsync();
        }

        private static string ReasonPhrase(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 202: return "Accepted";
                case 400: return "Bad Request";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 500: return "Internal Server Error";
                case 501: return "Not Implemented";
                default: return "Status";
            }
        }

        /// <summary>Best-effort error response; never throws, since it runs inside a catch block.</summary>
        private static async Task TryFailAsync(TcpClient client, int status, JsonRpcError error)
        {
            try
            {
                if (client == null || !client.Connected) return;

                var json = JsonConvert.SerializeObject(new JsonRpcResponse { Id = null, Error = error },
                                                       MCPProtocol.JsonSettings);

                using (var stream = client.GetStream())
                {
                    await WriteBodyAsync(stream, status, "application/json", json);
                }
            }
            catch
            {
                // The connection is already gone; nothing useful to do.
            }
        }
    }
}
