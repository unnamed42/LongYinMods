using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace MelonMCP.Server
{
    /// <summary>
    /// MCP Protocol message types and structures
    /// Implements JSON-RPC 2.0 based MCP protocol
    /// </summary>
    public static class MCPProtocol
    {
        public const string JSONRPC_VERSION = "2.0";

        /// <summary>
        /// Protocol revisions this server actually implements.
        ///
        /// DELIBERATELY SHORT, and that is the point. The Streamable HTTP revision history is:
        ///   2024-11-05  HTTP+SSE transport; no protocol-level sessions
        ///   2025-03-26  Streamable HTTP; sessions introduced, GET stream optional
        ///   2025-06-18  sessions become OPTIONAL; MCP-Protocol-Version header validation required
        ///   2025-11-25  further refinements
        ///   2026-07-28  sessions and GET stream REMOVED; header/body mirroring contract
        ///
        /// This server is stateless, sends no session id, and answers 405 to GET - i.e. it satisfies
        /// the 2025-03-26 rules and everything before them. It does NOT implement the 2025-06-18+
        /// header-mirroring contract, so it does not claim those revisions. Declaring a version
        /// without implementing its rules is a lie whose cost lands on the CLIENT side, where it is
        /// very hard to diagnose; the spec provides a clean "unsupported" path instead.
        ///
        /// 2026-07-28 in particular is refused on purpose: it REQUIRES validating that the
        /// MCP-Protocol-Version header matches the version inside the message body, answering 400
        /// HeaderMismatch otherwise. Not implemented here, so not advertised.
        /// </summary>
        public static readonly string[] SupportedProtocolVersions =
        {
            "2025-03-26",
            "2024-11-05",
        };

        /// <summary>The newest revision this server speaks; also the fallback default.</summary>
        public const string MCP_VERSION = "2025-03-26";

        /// <summary>
        /// Version assumed for a request carrying no `MCP-Protocol-Version` header.
        ///
        /// The spec permits (does not require) this, because clients written before 2025-06-18 never
        /// sent the header. Using the same value as our newest revision keeps the no-header and
        /// has-header paths identical, so there is only one path to reason about.
        /// </summary>
        public const string LegacyVersionWithoutHeader = "2025-03-26";

        public static JsonSerializerSettings JsonSettings { get; } = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            Formatting = Formatting.None,
            NullValueHandling = NullValueHandling.Ignore,
            Converters = { new StringEnumConverter(new CamelCaseNamingStrategy()) }
        };
    }

    #region Base Message Types

    public class JsonRpcRequest
    {
        [JsonProperty("jsonrpc")]
        public string JsonRpc { get; set; } = MCPProtocol.JSONRPC_VERSION;

        [JsonProperty("id")]
        public object Id { get; set; }

        [JsonProperty("method")]
        public string Method { get; set; }

        [JsonProperty("params")]
        public JToken Params { get; set; }
    }

    public class JsonRpcResponse
    {
        [JsonProperty("jsonrpc")]
        public string JsonRpc { get; set; } = MCPProtocol.JSONRPC_VERSION;

        [JsonProperty("id")]
        public object Id { get; set; }

        [JsonProperty("result")]
        public object Result { get; set; }

        [JsonProperty("error")]
        public JsonRpcError Error { get; set; }
    }

    public class JsonRpcError
    {
        [JsonProperty("code")]
        public int Code { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }

        [JsonProperty("data")]
        public object Data { get; set; }

        public static JsonRpcError ParseError(string message) => new JsonRpcError { Code = -32700, Message = message };
        public static JsonRpcError InvalidRequest(string message) => new JsonRpcError { Code = -32600, Message = message };
        public static JsonRpcError MethodNotFound(string message) => new JsonRpcError { Code = -32601, Message = message };
        public static JsonRpcError InvalidParams(string message) => new JsonRpcError { Code = -32602, Message = message };
        public static JsonRpcError InternalError(string message) => new JsonRpcError { Code = -32603, Message = message };
    }

    public class JsonRpcNotification
    {
        [JsonProperty("jsonrpc")]
        public string JsonRpc { get; set; } = MCPProtocol.JSONRPC_VERSION;

        [JsonProperty("method")]
        public string Method { get; set; }

        [JsonProperty("params")]
        public object Params { get; set; }
    }

    #endregion

    #region MCP Initialize

    public class InitializeParams
    {
        [JsonProperty("protocolVersion")]
        public string ProtocolVersion { get; set; }

        [JsonProperty("capabilities")]
        public ClientCapabilities Capabilities { get; set; }

        [JsonProperty("clientInfo")]
        public ClientInfo ClientInfo { get; set; }
    }

    public class ClientCapabilities
    {
        [JsonProperty("roots")]
        public RootsCapability Roots { get; set; }

        [JsonProperty("sampling")]
        public SamplingCapability Sampling { get; set; }
    }

    public class RootsCapability
    {
        [JsonProperty("listChanged")]
        public bool ListChanged { get; set; }
    }

    public class SamplingCapability { }

    public class ClientInfo
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("version")]
        public string Version { get; set; }
    }

    public class InitializeResult
    {
        [JsonProperty("protocolVersion")]
        public string ProtocolVersion { get; set; } = MCPProtocol.MCP_VERSION;

        [JsonProperty("capabilities")]
        public ServerCapabilities Capabilities { get; set; }

        [JsonProperty("serverInfo")]
        public ServerInfo ServerInfo { get; set; }

        [JsonProperty("instructions")]
        public string Instructions { get; set; }
    }

    public class ServerCapabilities
    {
        [JsonProperty("tools")]
        public ToolsCapability Tools { get; set; }

        [JsonProperty("resources")]
        public ResourcesCapability Resources { get; set; }

        [JsonProperty("logging")]
        public LoggingCapability Logging { get; set; }
    }

    public class ToolsCapability
    {
        [JsonProperty("listChanged")]
        public bool ListChanged { get; set; }
    }

    public class ResourcesCapability
    {
        [JsonProperty("subscribe")]
        public bool Subscribe { get; set; }

        [JsonProperty("listChanged")]
        public bool ListChanged { get; set; }
    }

    public class LoggingCapability { }

    public class ServerInfo
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("version")]
        public string Version { get; set; }
    }

    #endregion

    #region Tools

    public class ListToolsResult
    {
        [JsonProperty("tools")]
        public List<ToolInfo> Tools { get; set; } = new List<ToolInfo>();
    }

    public class ToolInfo
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("inputSchema")]
        public ToolInputSchema InputSchema { get; set; }
    }

    public class ToolInputSchema
    {
        [JsonProperty("type")]
        public string Type { get; set; } = "object";

        [JsonProperty("properties")]
        public Dictionary<string, ToolPropertySchema> Properties { get; set; } = new Dictionary<string, ToolPropertySchema>();

        [JsonProperty("required")]
        public List<string> Required { get; set; } = new List<string>();

        [JsonProperty("additionalProperties")]
        public bool AdditionalProperties { get; set; } = false;
    }

    public class ToolPropertySchema
    {
        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("enum")]
        public List<string> Enum { get; set; }

        [JsonProperty("default")]
        public object Default { get; set; }

        [JsonProperty("items")]
        public ToolPropertySchema Items { get; set; }

        [JsonProperty("minimum")]
        public int? Minimum { get; set; }

        [JsonProperty("maximum")]
        public int? Maximum { get; set; }
    }

    public class CallToolParams
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("arguments")]
        public Dictionary<string, JToken> Arguments { get; set; }
    }

    public class CallToolResult
    {
        [JsonProperty("content")]
        public List<ToolContent> Content { get; set; } = new List<ToolContent>();

        [JsonProperty("isError")]
        public bool? IsError { get; set; }
    }

    public class ToolContent
    {
        [JsonProperty("type")]
        public string Type { get; set; } = "text";

        [JsonProperty("text")]
        public string Text { get; set; }

        [JsonProperty("mimeType")]
        public string MimeType { get; set; }

        [JsonProperty("data")]
        public string Data { get; set; }

        public static ToolContent TextContent(string text) => new ToolContent { Type = "text", Text = text };
        public static ToolContent ImageContent(string base64Data, string mimeType = "image/png") =>
            new ToolContent { Type = "image", Data = base64Data, MimeType = mimeType };
    }

    #endregion

    #region Resources

    public class ListResourcesResult
    {
        [JsonProperty("resources")]
        public List<ResourceInfo> Resources { get; set; } = new List<ResourceInfo>();
    }

    public class ResourceInfo
    {
        [JsonProperty("uri")]
        public string Uri { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("mimeType")]
        public string MimeType { get; set; }
    }

    public class ReadResourceParams
    {
        [JsonProperty("uri")]
        public string Uri { get; set; }
    }

    public class ReadResourceResult
    {
        [JsonProperty("contents")]
        public List<ResourceContent> Contents { get; set; } = new List<ResourceContent>();
    }

    public class ResourceContent
    {
        [JsonProperty("uri")]
        public string Uri { get; set; }

        [JsonProperty("mimeType")]
        public string MimeType { get; set; }

        [JsonProperty("text")]
        public string Text { get; set; }

        [JsonProperty("blob")]
        public string Blob { get; set; }
    }

    #endregion

    #region Logging

    public class LoggingMessageParams
    {
        [JsonProperty("level")]
        public string Level { get; set; }

        [JsonProperty("logger")]
        public string Logger { get; set; }

        [JsonProperty("data")]
        public object Data { get; set; }
    }

    #endregion
}
