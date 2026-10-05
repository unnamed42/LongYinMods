using System;
using System.Collections.Generic;
using System.Linq;
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
        public override string Description => @"Read recent MelonLoader log lines from an in-memory buffer.

The buffer only holds what was captured after this mod loaded, so absent messages here were not
necessarily never logged - the MelonLoader log file has the complete record.

group_by='prefix' counts messages by leading tag instead of listing them, which answers 'which
message is flooding the log'. A plain filter cannot: it returns matches one by one, so a message
logged 32 times looks like 32 distinct messages.";
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
                    },
                    ["group_by"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Set to 'prefix' to return counted GROUPS instead of individual "
                                    + "lines, ordered by frequency. Use this to answer 'which message "
                                    + "is flooding the log' - a plain text filter returns the matching "
                                    + "lines one by one and cannot answer a counting question."
                    },
                    ["limit"] = new ToolPropertySchema
                    {
                        Type = "integer",
                        Description = "With group_by=prefix: how many top groups to return "
                                    + "(default 20). Ignored otherwise.",
                        Default = 20,
                        Minimum = 1,
                        Maximum = 500
                    },
                    ["includeExample"] = new ToolPropertySchema
                    {
                        Type = "boolean",
                        Description = "With group_by=prefix: include one full sample line per group, "
                                    + "so a group key can be traced back to the real message "
                                    + "(default true).",
                        Default = true
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

            var groupBy = GetStringArg(arguments, "group_by");
            if (string.Equals(groupBy, "prefix", StringComparison.OrdinalIgnoreCase))
            {
                return RenderGroups(logs, arguments);
            }

            return TextResult(string.Join("\n", logs));
        }

        /// <summary>
        /// Renders counted groups, highest count first.
        ///
        /// Counts are computed over the FILTERED buffer slice, so a group's number answers "how many
        /// of these matched" rather than "how many exist in total". 'count' still bounds how much of
        /// the buffer is examined, which is why the header states the number of lines grouped - a
        /// group count read without that denominator is easy to misjudge.
        /// </summary>
        private CallToolResult RenderGroups(List<string> logs, Dictionary<string, JToken> arguments)
        {
            var limit = GetIntArg(arguments, "limit", 20);
            if (limit < 1) limit = 1;
            if (limit > 500) limit = 500;

            var includeExample = GetBoolArg(arguments, "includeExample", true);

            var groups = LogGrouper.Group(logs);

            var byCount = groups.Count;
            var shown = groups.Take(limit).ToList();

            var builder = new System.Text.StringBuilder();
            builder.Append($"Log groups by prefix  ({byCount} group(s) over {logs.Count} line(s)");

            if (byCount > shown.Count)
            {
                builder.Append($", showing top {shown.Count}");
            }
            builder.AppendLine(")");
            builder.AppendLine();

            // Pad the count column so the keys line up; width follows the largest count actually shown.
            var countWidth = shown.Count == 0 ? 1 : shown.Max(g => g.Count.ToString().Length);

            foreach (var g in shown)
            {
                builder.Append(g.Count.ToString().PadLeft(countWidth));
                builder.Append("  ");
                builder.AppendLine(g.Key);

                if (includeExample)
                {
                    builder.Append(new string(' ', countWidth + 2));
                    builder.AppendLine("  " + Truncate(g.Example, 160));
                }
            }

            if (byCount > shown.Count)
            {
                builder.AppendLine();
                builder.Append($"... {byCount - shown.Count} more group(s) not shown; raise 'limit' to see them.");
            }

            return TextResult(builder.ToString().TrimEnd());
        }

        /// <summary>Truncates for display without throwing on short input.</summary>
        private static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Length <= max ? text : text.Substring(0, max) + "…";
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

        public override string Description => @"Report whether the Unity main thread is still completing frames.

Every tool that touches a Unity object is queued onto the main thread, so when this reports 'stuck'
those tools will all time out. The tools that keep working are the ones that never touch Unity:
read_logs, list_patches, hook_patch_info, list_assemblies and the config tools. Use them, and the
native hint in the result, to diagnose from outside.";

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
