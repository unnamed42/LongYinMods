using System.Collections.Generic;
using MelonMCP.Server;
using Newtonsoft.Json.Linq;

namespace MelonMCP.Tools
{
    /// <summary>
    /// Tool for reading MelonLoader logs
    /// </summary>
    public class ReadLogsToolDefinition : ToolDefinitionBase
    {
        public override string Name => "read_logs";
        public override string Description => "Read recent MelonLoader log messages. Use this to see console output, errors, warnings, and messages from mods.";
        public override bool RequiresMainThread => false;

        protected override ToolInputSchema GetInputSchema()
        {
            return new ToolInputSchema
            {
                Properties = new Dictionary<string, ToolPropertySchema>
                {
                    ["count"] = new ToolPropertySchema
                    {
                        Type = "integer",
                        Description = "Number of recent log entries to retrieve (default: 100, max: 1000)",
                        Default = 100,
                        Minimum = 1,
                        Maximum = 1000
                    },
                    ["filter"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Optional text filter - only returns logs containing this text (case-insensitive)"
                    },
                    ["level"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Filter by log level",
                        Enum = new List<string> { "all", "error", "warning" }
                    }
                }
            };
        }

        public override CallToolResult Execute(Dictionary<string, JToken> arguments)
        {
            var count = GetIntArg(arguments, "count", 100);
            var filter = GetStringArg(arguments, "filter");
            var level = GetStringArg(arguments, "level", "all");

            if (count > 1000) count = 1000;
            if (count < 1) count = 1;

            // Adjust filter based on level
            string effectiveFilter = filter;
            if (level == "error")
            {
                effectiveFilter = string.IsNullOrEmpty(filter) ? "[ERROR]" : $"[ERROR].*{filter}|{filter}.*[ERROR]";
            }
            else if (level == "warning")
            {
                effectiveFilter = string.IsNullOrEmpty(filter) ? "[WARNING]" : $"[WARNING].*{filter}|{filter}.*[WARNING]";
            }

            var logs = MelonMCPPlugin.Instance.GetLogs(count, effectiveFilter);

            if (logs.Count == 0)
            {
                return TextResult("No log entries found matching the criteria.");
            }

            return TextResult(string.Join("\n", logs));
        }
    }

    /// <summary>
    /// Tool for clearing the log buffer
    /// </summary>
    public class ClearLogsToolDefinition : ToolDefinitionBase
    {
        public override string Name => "clear_logs";
        public override string Description => "Clear the MelonMCP log buffer. This removes all stored log messages.";
        public override bool RequiresMainThread => false;

        protected override ToolInputSchema GetInputSchema()
        {
            return new ToolInputSchema
            {
                Properties = new Dictionary<string, ToolPropertySchema>()
            };
        }

        public override CallToolResult Execute(Dictionary<string, JToken> arguments)
        {
            MelonMCPPlugin.Instance.ClearLogs();
            return TextResult("Log buffer cleared.");
        }
    }
}
