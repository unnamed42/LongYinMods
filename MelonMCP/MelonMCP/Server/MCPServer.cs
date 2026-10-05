using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        private readonly Dictionary<string, IToolDefinition> _tools = new Dictionary<string, IToolDefinition>(StringComparer.OrdinalIgnoreCase);

        public int ToolCount => _tools.Count;

        public MCPServer(int port)
        {
            _port = port;
        }

        public void RegisterTool(IToolDefinition tool)
        {
            _tools[tool.Name] = tool;
            MelonMCPPlugin.Logger?.Msg($"Registered tool: {tool.Name}");
        }

        public void Stop()
        {
            // Nothing to release here: this class owns no socket. The transport that does own one is
            // MCPHttpServer, and the plugin stops it first. Kept as a method because the plugin's
            // teardown calls it and a future transport will want the same hook.
        }

        /// <summary>
        /// Dispatches one JSON-RPC request.
        ///
        /// <paramref name="protocolVersion"/> is the revision the TRANSPORT negotiated for this
        /// request, or null when the transport has no opinion. It is passed as an argument rather
        /// than stored on the server because HTTP handles requests concurrently: a field would mean
        /// two overlapping `initialize` calls could each write it and then read the other's value,
        /// answering with a version the client never asked for. Passing it keeps the server
        /// stateless with respect to the transport, which is also what lets both transports share
        /// this method safely.
        /// </summary>
        internal JsonRpcResponse HandleRequest(JsonRpcRequest request, string protocolVersion = null)
        {
            try
            {
                return request.Method switch
                {
                    "initialize" => HandleInitialize(request, protocolVersion),
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

        /// <summary>
        /// Chooses the version to answer `initialize` with, in order of preference:
        ///
        ///   1. the transport-negotiated version (HTTP: the validated header);
        ///   2. otherwise the version the CLIENT proposed in params.protocolVersion, IF this server
        ///      supports it;
        ///   3. otherwise this server's newest supported revision.
        ///
        /// Step 2 checks SUPPORT rather than echoing blindly. Echoing an arbitrary client value would
        /// be a lie the client discovers later, when it relies on a behaviour this server does not
        /// have - and that failure surfaces as a tool bug, not a version mismatch.
        ///
        /// This matters more over HTTP than it did over TCP: an HTTP client validates the echoed
        /// version and may drop the connection when it does not match what it asked for. The previous
        /// hardcoded echo worked only because the bridge in between did not check.
        /// </summary>
        private static string ResolveNegotiatedVersion(JsonRpcRequest request, string transportVersion)
        {
            if (!string.IsNullOrEmpty(transportVersion)) return transportVersion;

            try
            {
                var proposed = request.Params?["protocolVersion"]?.ToString();
                if (!string.IsNullOrEmpty(proposed)
                    && MCPProtocol.SupportedProtocolVersions.Contains(proposed, StringComparer.Ordinal))
                {
                    return proposed;
                }
            }
            catch
            {
                // A malformed params object must not turn a version echo into a failed initialize.
            }

            return MCPProtocol.MCP_VERSION;
        }

        private JsonRpcResponse HandleInitialize(JsonRpcRequest request, string transportVersion)
        {
            var result = new InitializeResult
            {
                ProtocolVersion = ResolveNegotiatedVersion(request, transportVersion),
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
}
