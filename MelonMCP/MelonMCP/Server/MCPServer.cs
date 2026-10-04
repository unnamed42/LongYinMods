using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MelonMCP.Tools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MelonMCP.Server
{
    /// <summary>
    /// MCP Server implementation using TCP with JSON-RPC 2.0
    /// Supports multiple concurrent clients
    /// </summary>
    public class MCPServer
    {
        /// <summary>
        /// How long a main-thread tool may wait for the Unity main thread before giving up.
        ///
        /// Short on purpose. When the main thread is healthy, tools run in milliseconds, so this is
        /// never reached in normal use. When it is wedged, waiting longer buys nothing and costs the
        /// caller the ability to react - a 400ms liveness probe plus a clear verdict is worth far more
        /// than 30 seconds of silence followed by a misleading 'the game may be paused'.
        /// </summary>
        private const int MainThreadToolTimeoutSeconds = 3;

        private readonly int _port;
        private TcpListener _listener;
        private CancellationTokenSource _cancellation;
        private readonly ConcurrentDictionary<Guid, MCPClientHandler> _clients = new ConcurrentDictionary<Guid, MCPClientHandler>();
        private readonly Dictionary<string, IToolDefinition> _tools = new Dictionary<string, IToolDefinition>(StringComparer.OrdinalIgnoreCase);
        /// <summary>The background accept loop, tracked so Stop() can wait for it to unwind.</summary>
        private Task _acceptLoop;

        private bool _isRunning;

        public int ToolCount => _tools.Count;
        public bool IsRunning => _isRunning;

        public MCPServer(int port)
        {
            _port = port;
        }

        public void RegisterTool(IToolDefinition tool)
        {
            _tools[tool.Name] = tool;
            MelonMCPPlugin.Logger?.Msg($"Registered tool: {tool.Name}");
        }

        public void Start()
        {
            if (_isRunning) return;

            _cancellation = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Start();
            _isRunning = true;

            // Start accepting clients in background
            _acceptLoop = Task.Run(AcceptClientsAsync);
        }

        public void Stop()
        {
            if (!_isRunning) return;

            _isRunning = false;
            _cancellation?.Cancel();

            // Disconnect all clients
            foreach (var client in _clients.Values)
            {
                client.Disconnect();
            }
            _clients.Clear();

            // Capture the listener in a local and give the accept loop its own reference to it.
            // AcceptClientsAsync reads _listener on every iteration, so nulling the field while the
            // loop is still parked in AcceptTcpClientAsync turns the expected ObjectDisposedException
            // into a NullReferenceException and skips the clean break.
            var listener = _listener;
            _listener = null;

            try
            {
                listener?.Stop();
            }
            catch { }

            // Wait for the accept loop to observe the shutdown. This is what actually makes the port
            // reusable: on a hot reload, MelonLoader loads the new assembly as soon as
            // OnDeinitializeMelon returns, and if the old listener socket is still held the new
            // Start() throws at TcpListener.Start() with 'address already in use'.
            var loop = _acceptLoop;
            _acceptLoop = null;
            if (loop != null)
            {
                try
                {
                    loop.Wait(TimeSpan.FromSeconds(5));
                }
                catch (Exception ex)
                {
                    MelonMCPPlugin.Logger?.Warning($"Accept loop did not shut down cleanly: {ex.Message}");
                }
            }

            _cancellation?.Dispose();
            _cancellation = null;
        }

        private async Task AcceptClientsAsync()
        {
            // Take a local reference to the listener: Stop() nulls the field before the loop is
            // guaranteed to have observed the cancellation, and we want the ObjectDisposedException
            // path below, not a NullReferenceException.
            var listener = _listener;

            while (_isRunning && _cancellation != null && !_cancellation.Token.IsCancellationRequested)
            {
                try
                {
                    var client = await listener.AcceptTcpClientAsync();
                    var clientId = Guid.NewGuid();
                    var handler = new MCPClientHandler(clientId, client, this);
                    _clients[clientId] = handler;

                    MelonMCPPlugin.Logger?.Msg($"Client connected: {clientId}");

                    // Handle client in background
                    _ = Task.Run(() => handler.HandleClientAsync(_cancellation.Token));
                }
                catch (ObjectDisposedException)
                {
                    // Server stopped
                    break;
                }
                catch (Exception ex)
                {
                    if (_isRunning)
                    {
                        MelonMCPPlugin.Logger?.Warning($"Error accepting client: {ex.Message}");
                    }
                }
            }
        }

        internal void OnClientDisconnected(Guid clientId)
        {
            _clients.TryRemove(clientId, out _);
            MelonMCPPlugin.Logger?.Msg($"Client disconnected: {clientId}");
        }

        internal JsonRpcResponse HandleRequest(JsonRpcRequest request)
        {
            try
            {
                return request.Method switch
                {
                    "initialize" => HandleInitialize(request),
                    "initialized" => HandleInitialized(request),
                    "ping" => HandlePing(request),
                    "tools/list" => HandleListTools(request),
                    "tools/call" => HandleCallTool(request),
                    "resources/list" => HandleListResources(request),
                    "resources/read" => HandleReadResource(request),
                    _ => new JsonRpcResponse
                    {
                        Id = request.Id,
                        Error = JsonRpcError.MethodNotFound($"Unknown method: {request.Method}")
                    }
                };
            }
            catch (Exception ex)
            {
                MelonMCPPlugin.Logger?.Error($"Error handling request {request.Method}: {ex}");
                return new JsonRpcResponse
                {
                    Id = request.Id,
                    Error = JsonRpcError.InternalError(ex.Message)
                };
            }
        }

        private JsonRpcResponse HandleInitialize(JsonRpcRequest request)
        {
            var result = new InitializeResult
            {
                ProtocolVersion = MCPProtocol.MCP_VERSION,
                Capabilities = new ServerCapabilities
                {
                    Tools = new ToolsCapability { ListChanged = false },
                    Resources = new ResourcesCapability { Subscribe = false, ListChanged = false },
                    Logging = new LoggingCapability()
                },
                ServerInfo = new ServerInfo
                {
                    Name = "MelonMCP",
                    Version = BuildInfo.Version
                },
                Instructions = "MelonMCP provides tools to interact with running Unity games through MelonLoader. " +
                               "You can read logs, execute C# code at runtime, inspect and control MonoBehaviours, " +
                               "and explore the game's object hierarchy."
            };

            return new JsonRpcResponse { Id = request.Id, Result = result };
        }

        private JsonRpcResponse HandleInitialized(JsonRpcRequest request)
        {
            // No response needed for notification
            return null;
        }

        private JsonRpcResponse HandlePing(JsonRpcRequest request)
        {
            return new JsonRpcResponse { Id = request.Id, Result = new { } };
        }

        private JsonRpcResponse HandleListTools(JsonRpcRequest request)
        {
            var result = new ListToolsResult();

            foreach (var tool in _tools.Values)
            {
                result.Tools.Add(tool.GetInfo());
            }

            return new JsonRpcResponse { Id = request.Id, Result = result };
        }

        private JsonRpcResponse HandleCallTool(JsonRpcRequest request)
        {
            CallToolParams callParams;
            try
            {
                callParams = request.Params?.ToObject<CallToolParams>(JsonSerializer.Create(MCPProtocol.JsonSettings));
                if (callParams == null)
                {
                    return new JsonRpcResponse
                    {
                        Id = request.Id,
                        Error = JsonRpcError.InvalidParams("Missing tool call params")
                    };
                }
            }
            catch (Exception ex)
            {
                return new JsonRpcResponse
                {
                    Id = request.Id,
                    Error = JsonRpcError.InvalidParams($"Failed to parse tool call params: {ex.Message}")
                };
            }

            if (!_tools.TryGetValue(callParams.Name, out var tool))
            {
                return new JsonRpcResponse
                {
                    Id = request.Id,
                    Error = JsonRpcError.MethodNotFound($"Tool not found: {callParams.Name}")
                };
            }

            try
            {
                CallToolResult result = null;
                Exception toolException = null;

                // Check if tool needs main thread
                if (tool.RequiresMainThread)
                {
                    // Execute tool on main thread for Unity access
                    var resetEvent = new ManualResetEventSlim(false);

                    // Queue work on Unity main thread
                    UnityMainThreadDispatcher.Enqueue(() =>
                    {
                        try
                        {
                            result = tool.Execute(callParams.Arguments ?? new Dictionary<string, JToken>());
                        }
                        catch (Exception ex)
                        {
                            toolException = ex;
                        }
                        finally
                        {
                            resetEvent.Set();
                        }
                    });

                    // Wait for completion with timeout. The timeout is deliberately short: a
                    // main-thread tool that has not run within a couple of seconds is not going to
                    // run soon, and the caller can decide what to do far better if it gets an answer
                    // quickly than if it blocks for half a minute.
                    if (!resetEvent.Wait(TimeSpan.FromSeconds(MainThreadToolTimeoutSeconds)))
                    {
                        // Do not guess. Ask the watchdog whether frames are still completing, which
                        // cleanly separates 'busy/paused' (retry) from 'wedged' (go to the OS).
                        MainThreadStatus status;
                        try { status = MainThreadWatchdog.Probe(400); }
                        catch (Exception probeEx) { status = null; MelonMCPPlugin.Logger?.Warning($"Watchdog probe failed: {probeEx.Message}"); }

                        var sb = new System.Text.StringBuilder();
                        sb.Append($"Tool '{callParams.Name}' needs the Unity main thread and did not run within {MainThreadToolTimeoutSeconds}s.");
                        if (status != null)
                        {
                            sb.Append($" Main-thread verdict: {status.Verdict}");
                            sb.Append($" ({status.FramesDuringWindow} frame(s) in {status.ProbedForMs}ms).");
                            if (status.StallSeconds > 0)
                            {
                                sb.Append($" Last frame was {status.StallSeconds:F1}s ago.");
                            }
                            sb.Append(' ').Append(status.Explanation);
                            if (!string.IsNullOrEmpty(status.NativeHint))
                            {
                                sb.Append(' ').Append(status.NativeHint);
                            }
                        }

                        return new JsonRpcResponse
                        {
                            Id = request.Id,
                            Result = new CallToolResult
                            {
                                Content = new List<ToolContent> { ToolContent.TextContent(sb.ToString()) },
                                IsError = true
                            }
                        };
                    }
                }
                else
                {
                    // Execute directly - tool doesn't need main thread
                    try
                    {
                        result = tool.Execute(callParams.Arguments ?? new Dictionary<string, JToken>());
                    }
                    catch (Exception ex)
                    {
                        toolException = ex;
                    }
                }

                if (toolException != null)
                {
                    return new JsonRpcResponse
                    {
                        Id = request.Id,
                        Result = new CallToolResult
                        {
                            Content = new List<ToolContent> { ToolContent.TextContent($"Tool execution error: {toolException.Message}\n{toolException.StackTrace}") },
                            IsError = true
                        }
                    };
                }

                return new JsonRpcResponse { Id = request.Id, Result = result };
            }
            catch (Exception ex)
            {
                return new JsonRpcResponse
                {
                    Id = request.Id,
                    Result = new CallToolResult
                    {
                        Content = new List<ToolContent> { ToolContent.TextContent($"Error: {ex.Message}") },
                        IsError = true
                    }
                };
            }
        }

        private JsonRpcResponse HandleListResources(JsonRpcRequest request)
        {
            var result = new ListResourcesResult
            {
                Resources = new List<ResourceInfo>
                {
                    new ResourceInfo
                    {
                        Uri = "melonmcp://logs/melon",
                        Name = "MelonLoader Logs",
                        Description = "Recent log output from MelonLoader",
                        MimeType = "text/plain"
                    },
                    new ResourceInfo
                    {
                        Uri = "melonmcp://scene/hierarchy",
                        Name = "Scene Hierarchy",
                        Description = "Current Unity scene object hierarchy",
                        MimeType = "application/json"
                    },
                    new ResourceInfo
                    {
                        Uri = "melonmcp://game/info",
                        Name = "Game Information",
                        Description = "Information about the running game",
                        MimeType = "application/json"
                    }
                }
            };

            return new JsonRpcResponse { Id = request.Id, Result = result };
        }

        private JsonRpcResponse HandleReadResource(JsonRpcRequest request)
        {
            ReadResourceParams readParams;
            try
            {
                readParams = request.Params?.ToObject<ReadResourceParams>(JsonSerializer.Create(MCPProtocol.JsonSettings));
                if (readParams == null)
                {
                    return new JsonRpcResponse
                    {
                        Id = request.Id,
                        Error = JsonRpcError.InvalidParams("Missing resource read params")
                    };
                }
            }
            catch (Exception ex)
            {
                return new JsonRpcResponse
                {
                    Id = request.Id,
                    Error = JsonRpcError.InvalidParams($"Failed to parse resource read params: {ex.Message}")
                };
            }

            var result = new ReadResourceResult();

            switch (readParams.Uri)
            {
                case "melonmcp://logs/melon":
                    var logs = MelonMCPPlugin.Instance.GetLogs(200);
                    result.Contents.Add(new ResourceContent
                    {
                        Uri = readParams.Uri,
                        MimeType = "text/plain",
                        Text = string.Join("\n", logs)
                    });
                    break;

                case "melonmcp://scene/hierarchy":
                case "melonmcp://game/info":
                    // Execute on main thread
                    string content = null;
                    var resetEvent = new ManualResetEventSlim(false);

                    UnityMainThreadDispatcher.Enqueue(() =>
                    {
                        try
                        {
                            content = readParams.Uri == "melonmcp://scene/hierarchy"
                                ? UnityHelper.GetSceneHierarchyJson()
                                : UnityHelper.GetGameInfoJson();
                        }
                        finally
                        {
                            resetEvent.Set();
                        }
                    });

                    resetEvent.Wait(TimeSpan.FromSeconds(10));

                    result.Contents.Add(new ResourceContent
                    {
                        Uri = readParams.Uri,
                        MimeType = "application/json",
                        Text = content ?? "{}"
                    });
                    break;

                default:
                    return new JsonRpcResponse
                    {
                        Id = request.Id,
                        Error = JsonRpcError.InvalidParams($"Unknown resource: {readParams.Uri}")
                    };
            }

            return new JsonRpcResponse { Id = request.Id, Result = result };
        }
    }

    /// <summary>
    /// Handles individual MCP client connections
    /// </summary>
    internal class MCPClientHandler
    {
        private readonly Guid _clientId;
        private readonly TcpClient _client;
        private readonly MCPServer _server;
        private NetworkStream _stream;

        public MCPClientHandler(Guid clientId, TcpClient client, MCPServer server)
        {
            _clientId = clientId;
            _client = client;
            _server = server;
        }

        public async Task HandleClientAsync(CancellationToken cancellationToken)
        {
            try
            {
                _stream = _client.GetStream();
                using var reader = new StreamReader(_stream, Encoding.UTF8, leaveOpen: true);

                while (!cancellationToken.IsCancellationRequested && _client.Connected)
                {
                    // Read line (JSON-RPC messages are newline-delimited)
                    var line = await reader.ReadLineAsync();
                    if (line == null) break; // Connection closed

                    if (string.IsNullOrWhiteSpace(line)) continue;

                    try
                    {
                        var request = JsonConvert.DeserializeObject<JsonRpcRequest>(line, MCPProtocol.JsonSettings);
                        var response = _server.HandleRequest(request);

                        if (response != null)
                        {
                            await SendResponseAsync(response);
                        }
                    }
                    catch (JsonException ex)
                    {
                        var errorResponse = new JsonRpcResponse
                        {
                            Id = null,
                            Error = JsonRpcError.ParseError($"Invalid JSON: {ex.Message}")
                        };
                        await SendResponseAsync(errorResponse);
                    }
                }
            }
            catch (Exception ex)
            {
                MelonMCPPlugin.Logger?.Warning($"Client handler error: {ex.Message}");
            }
            finally
            {
                Disconnect();
                _server.OnClientDisconnected(_clientId);
            }
        }

        private async Task SendResponseAsync(JsonRpcResponse response)
        {
            try
            {
                var json = JsonConvert.SerializeObject(response, MCPProtocol.JsonSettings);
                var bytes = Encoding.UTF8.GetBytes(json + "\n");
                await _stream.WriteAsync(bytes, 0, bytes.Length);
                await _stream.FlushAsync();
            }
            catch (Exception ex)
            {
                MelonMCPPlugin.Logger?.Warning($"Failed to send response: {ex.Message}");
            }
        }

        public void Disconnect()
        {
            try
            {
                _stream?.Close();
                _client?.Close();
            }
            catch { }
        }
    }
}
