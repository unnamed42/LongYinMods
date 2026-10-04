using System.Collections.Generic;
using MelonMCP.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MelonMCP.Tools
{
    /// <summary>
    /// Interface for MCP tool definitions
    /// </summary>
    public interface IToolDefinition
    {
        /// <summary>
        /// The unique name of the tool
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Whether this tool requires Unity main thread access.
        ///
        /// Set this to false whenever the tool touches no Unity object. Main-thread tools are queued
        /// onto the Unity main thread and therefore become unreachable whenever that thread is busy or
        /// wedged - precisely the situation in which native/reflection diagnostics are needed most.
        /// Only tools that actually dereference Unity objects belong on the main thread.
        /// </summary>
        bool RequiresMainThread { get; }

        /// <summary>
        /// Get the tool's MCP info
        /// </summary>
        ToolInfo GetInfo();

        /// <summary>
        /// Execute the tool with the given arguments
        /// </summary>
        CallToolResult Execute(Dictionary<string, JToken> arguments);
    }

    /// <summary>
    /// Base class for tool definitions
    /// </summary>
    public abstract class ToolDefinitionBase : IToolDefinition
    {
        public abstract string Name { get; }
        public abstract string Description { get; }

        /// <summary>
        /// Most tools need Unity main thread. Override to false for tools that don't.
        ///
        /// Default true is the safe choice for anything that dereferences a Unity object, but it is
        /// also a liability: a main-thread tool cannot answer while the main thread is busy, so
        /// diagnostics that only read native memory or managed reflection should override it.
        /// </summary>
        public virtual bool RequiresMainThread => true;

        public virtual ToolInfo GetInfo()
        {
            return new ToolInfo
            {
                Name = Name,
                Description = Description,
                InputSchema = GetInputSchema()
            };
        }

        protected abstract ToolInputSchema GetInputSchema();

        public abstract CallToolResult Execute(Dictionary<string, JToken> arguments);

        #region Argument Helpers

        protected string GetStringArg(Dictionary<string, JToken> arguments, string name, string defaultValue = null)
        {
            if (arguments.TryGetValue(name, out var token))
            {
                if (token.Type == JTokenType.String)
                {
                    return token.Value<string>();
                }
            }
            return defaultValue;
        }

        protected int GetIntArg(Dictionary<string, JToken> arguments, string name, int defaultValue = 0)
        {
            if (arguments.TryGetValue(name, out var token))
            {
                if (token.Type == JTokenType.Integer)
                {
                    return token.Value<int>();
                }
            }
            return defaultValue;
        }

        protected bool GetBoolArg(Dictionary<string, JToken> arguments, string name, bool defaultValue = false)
        {
            if (arguments.TryGetValue(name, out var token))
            {
                if (token.Type == JTokenType.Boolean)
                {
                    return token.Value<bool>();
                }
            }
            return defaultValue;
        }

        protected double GetDoubleArg(Dictionary<string, JToken> arguments, string name, double defaultValue = 0.0)
        {
            if (arguments.TryGetValue(name, out var token))
            {
                if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
                {
                    return token.Value<double>();
                }
            }
            return defaultValue;
        }

        protected T GetArg<T>(Dictionary<string, JToken> arguments, string name, T defaultValue = default)
        {
            if (arguments.TryGetValue(name, out var token))
            {
                try
                {
                    return token.ToObject<T>();
                }
                catch { }
            }
            return defaultValue;
        }

        /// <summary>
        /// Reads a string-array argument, tolerating the shapes a client may actually send.
        ///
        /// Accepts both a JSON array (["a","b"]) and a single bare string ("a"), because an MCP
        /// client that has only one value to pass has no reason to wrap it in an array and several
        /// do not. Elements are coerced with ToString rather than cast, so a numeric entry like [1,2]
        /// yields ["1","2"] instead of dropping the whole argument.
        ///
        /// Returns an empty list (never null) when absent, so callers can iterate unconditionally.
        /// </summary>
        protected List<string> GetStringArrayArg(Dictionary<string, JToken> arguments, string name)
        {
            var result = new List<string>();

            if (!arguments.TryGetValue(name, out var token) || token == null)
            {
                return result;
            }

            try
            {
                if (token.Type == JTokenType.Array)
                {
                    foreach (var item in (JArray)token)
                    {
                        var text = item?.ToString();
                        if (!string.IsNullOrWhiteSpace(text)) result.Add(text);
                    }
                }
                else if (token.Type != JTokenType.Null)
                {
                    var text = token.ToString();
                    if (!string.IsNullOrWhiteSpace(text)) result.Add(text);
                }
            }
            catch
            {
                // A malformed argument yields an empty list; the caller reports "no fields" rather
                // than failing the whole call over a type mismatch.
            }

            return result;
        }

        #endregion

        #region Result Helpers

        protected CallToolResult TextResult(string text)
        {
            return new CallToolResult
            {
                Content = new List<ToolContent> { ToolContent.TextContent(text) }
            };
        }

        protected CallToolResult JsonResult(object obj)
        {
            var json = JsonConvert.SerializeObject(obj, MCPProtocol.JsonSettings);
            return new CallToolResult
            {
                Content = new List<ToolContent> { ToolContent.TextContent(json) }
            };
        }

        protected CallToolResult ErrorResult(string message)
        {
            return new CallToolResult
            {
                Content = new List<ToolContent> { ToolContent.TextContent($"Error: {message}") },
                IsError = true
            };
        }

        protected CallToolResult ImageResult(byte[] imageData, string mimeType = "image/png")
        {
            return new CallToolResult
            {
                Content = new List<ToolContent>
                {
                    ToolContent.ImageContent(System.Convert.ToBase64String(imageData), mimeType)
                }
            };
        }

        #endregion
    }
}
