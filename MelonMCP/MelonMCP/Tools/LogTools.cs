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

    /// <summary>
    /// Reports whether the Unity main thread is still completing frames.
    ///
    /// This is the one question worth being able to answer while the game is unresponsive, and it is
    /// answerable without the main thread: the watchdog counter is bumped from OnUpdate, so a reader
    /// on the MCP thread can watch it advance. It separates 'the game is busy, retry' from 'the game
    /// is wedged, stop waiting and go look at the process from the OS' - a distinction the old
    /// blanket 30-second timeout could not make.
    /// </summary>
    public class MainThreadStatusToolDefinition : ToolDefinitionBase
    {
        public override string Name => "main_thread_status";

        public override string Description => @"Check whether the Unity main thread is still running.

Reports the game's frame counter, how much it advanced during the probe, and a verdict:
- running       : frames are completing; main-thread tools will work.
- stuck         : ZERO frames during the probe. Main-thread tools (find_objects_of_type,
                  evaluate_expression, execute_csharp, watch_field, get_game_info, ...) will fail
                  until it recovers. Use the main-thread-free tools instead and inspect the process
                  from the OS.
- no-frames-yet : no frame has been observed at all; the game is likely still starting up.

Call this FIRST whenever a main-thread tool times out. It never touches Unity, so it still answers
when nothing else will.";

        // The whole point: this must work while the main thread is wedged.
        public override bool RequiresMainThread => false;

        protected override ToolInputSchema GetInputSchema()
        {
            return new ToolInputSchema
            {
                Properties = new Dictionary<string, ToolPropertySchema>
                {
                    ["windowMs"] = new ToolPropertySchema
                    {
                        Type = "integer",
                        Description = "How long to watch the frame counter, in milliseconds (default 750). "
                                    + "Longer windows are more conclusive but slower.",
                        Default = 750,
                        Minimum = 50,
                        Maximum = 10000
                    }
                }
            };
        }

        public override CallToolResult Execute(Dictionary<string, JToken> arguments)
        {
            var window = GetIntArg(arguments, "windowMs", 750);
            if (window < 50) window = 50;
            if (window > 10000) window = 10000;

            var status = MainThreadWatchdog.Probe(window);
            return JsonResult(status);
        }
    }
}
