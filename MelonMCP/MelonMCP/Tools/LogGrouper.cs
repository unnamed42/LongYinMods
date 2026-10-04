using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MelonMCP.Tools
{
    /// <summary>
    /// Collapses log lines into counted groups, so "which message is drowning the log" is answerable
    /// directly instead of by dropping to a shell and piping through sort/uniq.
    ///
    /// This exists because a plain text filter cannot answer a counting question: read_logs' filter
    /// returns the matching lines one by one, which tells you what is there but not what dominates.
    ///
    /// THE HARD PART IS THE GROUPING KEY, not the counting. Two failure modes both produce a useless
    /// result that still looks like a result:
    ///
    ///   1. Keying on the whole line - every line is its own group, because log lines carry timestamps
    ///      and per-call values. Output: N groups of 1.
    ///   2. Failing to normalise sequence numbers - "[Probe] call 32" and "[Probe] call 33" become
    ///      separate groups, so a message logged 32 times is reported as 32 groups of 1.
    ///
    /// Both are avoided below: the key is the bracketed tag at the start of the message, and
    /// embedded counters are replaced with a placeholder before grouping.
    /// </summary>
    internal static class LogGrouper
    {
        /// <summary>
        /// Matches the in-buffer prefix MelonMCP itself writes when it captures a log line:
        ///     [HH:mm:ss] [WARNING] [ModName] message
        ///     [HH:mm:ss] [MSG]     [ModName] message
        /// Grouping must ignore both the timestamp and the level, or every line lands in its own group.
        /// </summary>
        private static readonly Regex PrefixPattern = new Regex(
            @"^\[\d{2}:\d{2}:\d{2}\]\s*(?:\[(?:MSG|WARNING|ERROR)\]\s*)?",
            RegexOptions.Compiled);

        /// <summary>
        /// Extracts the grouping key from what remains after the prefix: either ONE tag or TWO.
        ///
        /// Two shapes occur in practice, and picking the wrong one is what makes this tool useless
        /// rather than broken:
        ///
        ///   [MelonMCP] Registered tool: read_logs          -> mod name only
        ///   [MelonMCP] [探针·图标点击] 第 1 次             -> mod name, then the message's own tag
        ///
        /// Taking the FIRST tag always yields the logger's name, which in this project is [MelonMCP]
        /// for every line the mod emits - so every message collapses into one group and the answer is
        /// "1 group, N lines". The message's own tag is the useful discriminator whenever it exists,
        /// so a second tag wins over the first.
        ///
        /// Anchored loosely (leading whitespace allowed) rather than to position 0, because some
        /// lines carry a little text before the tag.
        /// </summary>
        private static readonly Regex TagPattern = new Regex(
            @"^\s*(\[[^\]\r\n]{1,60}\])\s*(?:\[([^\]\r\n]{1,60})\])?",
            RegexOptions.Compiled);

        /// <summary>
        /// Sequence counters that must be normalised away, or every call becomes its own group.
        ///
        /// Covers the shapes seen in practice: "第 32 次" (Chinese ordinal), "#32", "count=32",
        /// "32 times", and a bare number in parentheses. Order matters only in that the more specific
        /// patterns run first; each replacement is independent.
        ///
        /// Deliberately conservative about bare integers elsewhere in the line: replacing every digit
        /// run would merge genuinely distinct messages ("item 32" vs "item 45" may be meaningfully
        /// different), so only counter-shaped occurrences are touched.
        /// </summary>
        private static readonly (Regex Pattern, string Replacement)[] CounterPatterns =
        {
            (new Regex(@"第\s*\d+\s*次", RegexOptions.Compiled), "第 N 次"),
            (new Regex(@"#\s*\d+\b", RegexOptions.Compiled), "#N"),
            (new Regex(@"\bcount\s*=\s*\d+", RegexOptions.Compiled | RegexOptions.IgnoreCase), "count=N"),
            (new Regex(@"\b\d+\s+times\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "N times"),
            (new Regex(@"\(\s*\d+\s*\)$", RegexOptions.Compiled), "(N)"),
        };

        /// <summary>One counted group, ordered by descending count by the caller.</summary>
        internal sealed class LogGroup
        {
            public string Key { get; set; }
            public int Count { get; set; }
            public string Example { get; set; }
        }

        /// <summary>
        /// Builds the group key for one line: tag if present, otherwise a normalised head of the line.
        /// Never returns null or empty - an unparseable line falls back to a fixed placeholder so it is
        /// still counted rather than silently dropped.
        /// </summary>
        internal static string KeyFor(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return "(blank)";

            var body = PrefixPattern.Replace(line, string.Empty);

            // Normalise counters BEFORE extracting the fallback head, so "[Probe] call 32" and
            // "[Probe] call 33" share a key. The tag itself is normally counter-free, but a tag like
            // "[Probe 12]" would otherwise defeat the grouping.
            foreach (var (pattern, replacement) in CounterPatterns)
            {
                if (pattern.IsMatch(body))
                {
                    body = pattern.Replace(body, replacement);
                }
            }

            var tag = TagPattern.Match(body);
            if (tag.Success)
            {
                var inner = tag.Groups[2];

                // A second tag means: logger name, then the message's own tag. The message's tag is
                // the useful discriminator, so prefer it.
                if (inner.Success) return inner.Value;

                // Only one tag. That tag is the logger/mod name, which is IDENTICAL for every line a
                // given mod emits - so using it as the key would merge unrelated messages. That is
                // acceptable only when the message body is empty; otherwise fall through and key on
                // the message text, which is what actually distinguishes the lines.
                var afterTag = body.Substring(tag.Length).Trim();
                if (afterTag.Length == 0) return tag.Groups[1].Value;
                return HeadKey(afterTag);
            }

            return HeadKey(body);
        }

        /// <summary>
        /// Keys a message that has no usable tag: the first few words, bounded in both word count and
        /// length. Bounded so that free-form messages sharing a prefix but differing in a unique tail
        /// still collapse together, which is the whole point of grouping.
        /// </summary>
        private static string HeadKey(string body)
        {
            var trimmed = body.Trim();
            if (trimmed.Length == 0) return "(untagged)";

            var words = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            const int MaxWords = 4;
            var head = string.Join(" ", words.Take(MaxWords));

            const int MaxChars = 72;
            if (head.Length > MaxChars) head = head.Substring(0, MaxChars) + "…";
            return head;
        }

        /// <summary>
        /// Groups lines and returns them ordered by descending count (ties broken by key, so the
        /// output is stable across calls - important when comparing two runs).
        /// </summary>
        internal static List<LogGroup> Group(IEnumerable<string> lines)
        {
            var map = new Dictionary<string, LogGroup>(StringComparer.Ordinal);

            foreach (var line in lines)
            {
                if (line == null) continue;
                var key = KeyFor(line);

                if (!map.TryGetValue(key, out var group))
                {
                    group = new LogGroup { Key = key, Count = 0, Example = line };
                    map[key] = group;
                }

                group.Count++;
            }

            return map.Values
                .OrderByDescending(g => g.Count)
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .ToList();
        }
    }
}
