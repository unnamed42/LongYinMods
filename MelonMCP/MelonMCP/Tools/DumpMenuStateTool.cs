using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MelonMCP.Server;
using Newtonsoft.Json.Linq;

namespace MelonMCP.Tools
{
    /// <summary>
    /// Enumerates UI buttons and reports the state flags that decide whether each one is usable.
    ///
    /// WHY THIS EXISTS: working out which button is "really" live means reading three different flags
    /// (enabled / interactable / activeInHierarchy) for every candidate and comparing them by eye.
    /// That loop was hand-written six-plus times in a single investigation, and the answer it produced
    /// - that a leftover button is identified by enabled=false while the other two stay true - was the
    /// key finding of that session. It should not depend on someone writing the loop correctly again.
    ///
    /// WHY IT ENUMERATES AND REPORTS IN ONE PASS: the content of some menus is rebuilt inside the
    /// game's own Update (a build-choice grid was observed rebuilding every frame). Reading the set of
    /// buttons in one MCP call and their states in another therefore compares two DIFFERENT sets -
    /// observed as "four buttons last time, one this time", which looks like a data problem but is a
    /// timing one. Everything is gathered here before anything is returned.
    ///
    /// WHICH INSTANCE SOURCE, AND WHY IT MATTERS MORE THAN ANYTHING ELSE HERE:
    /// UnityEngine.Object.FindObjectsOfType&lt;Button&gt;() returns only the buttons in ACTIVE scenes -
    /// measured on this game: 9. Resources.FindObjectsOfTypeAll&lt;Button&gt;() returns 951, because it
    /// also yields inactive objects and objects in other loaded scenes. Both are correct answers to
    /// different questions, and the difference is 100x, so the tool exposes it as 'includeInactive'
    /// (default false = the 9 actually on screen) rather than silently picking one.
    /// </summary>
    public class DumpMenuStateToolDefinition : ToolDefinitionBase
    {
        public override string Name => "dump_menu_state";

        public override string Description => @"List UI buttons with their enabled / interactable / activeInHierarchy flags - read all three
together, since a button that looks present can still be a dead leftover.

Gathered in one pass, because menus rebuilt inside the game's Update would otherwise change between
two reads and look like a data problem.

includeInactive changes the result by ~100x (9 buttons on screen vs 951 total, most pooled or in
other scenes).";

        public override bool RequiresMainThread => true;

        protected override ToolInputSchema GetInputSchema()
        {
            return new ToolInputSchema
            {
                Properties = new Dictionary<string, ToolPropertySchema>
                {
                    ["rootPath"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Optional GameObject path (e.g. 'MainMenu' or 'Canvas/Panel'). "
                                    + "Restricts the search to that object and its descendants. "
                                    + "Omit to scan every button globally."
                    },
                    ["includeInactive"] = new ToolPropertySchema
                    {
                        Type = "boolean",
                        Description = "Include buttons not currently on screen (inactive objects and "
                                    + "other scenes). Default false. This changes the result by "
                                    + "roughly 100x, so set it deliberately.",
                        Default = false
                    },
                    ["limit"] = new ToolPropertySchema
                    {
                        Type = "integer",
                        Description = "Maximum buttons to return (default 50, max 500).",
                        Default = 50,
                        Minimum = 1,
                        Maximum = 500
                    },
                    ["labelOnly"] = new ToolPropertySchema
                    {
                        Type = "boolean",
                        Description = "Only buttons whose first Text child has a non-empty label. "
                                    + "Default false, because an unlabelled button is still a button "
                                    + "and silently dropping them hides state.",
                        Default = false
                    }
                }
            };
        }

        public override CallToolResult Execute(Dictionary<string, JToken> arguments)
        {
            var rootPath = GetStringArg(arguments, "rootPath");
            var includeInactive = GetBoolArg(arguments, "includeInactive", false);
            var limit = GetIntArg(arguments, "limit", 50);
            if (limit < 1) limit = 1;
            if (limit > 500) limit = 500;
            var labelOnly = GetBoolArg(arguments, "labelOnly", false);

            UnityEngine.GameObject root = null;
            if (!string.IsNullOrWhiteSpace(rootPath))
            {
                root = UnityEngine.GameObject.Find(rootPath);
                if (root == null)
                {
                    return ErrorResult(
                        $"no active GameObject at path '{rootPath}'. Paths are case-sensitive and "
                        + "must include parents (e.g. 'Canvas/Panel'). Note Find only matches ACTIVE "
                        + "objects, so an inactive panel cannot be addressed this way - omit rootPath "
                        + "and use includeInactive instead.");
                }
            }

            // Gather and read in one pass - see the class comment for why this is not split.
            var candidates = CollectButtons(root, includeInactive);
            var totalBeforeFilter = candidates.Count;

            var rows = new List<object>();
            var skippedUnlabelled = 0;
            var readErrors = 0;

            foreach (var button in candidates)
            {
                // Per-button isolation: one destroyed or half-built button must not abort the dump,
                // and these lists come from pooled objects where that is routine.
                try
                {
                    if (button == null) continue;

                    var label = ReadLabel(button);

                    if (labelOnly && string.IsNullOrEmpty(label))
                    {
                        skippedUnlabelled++;
                        continue;
                    }

                    if (rows.Count >= limit) continue;

                    var transform = button.transform;
                    rows.Add(new
                    {
                        label,
                        enabled = SafeRead(() => button.enabled),
                        interactable = SafeRead(() => button.interactable),
                        activeInHierarchy = SafeRead(() => button.gameObject.activeInHierarchy),
                        activeSelf = SafeRead(() => button.gameObject.activeSelf),
                        siblingIndex = SafeRead(() => transform.GetSiblingIndex()),
                        name = SafeRead(() => button.gameObject.name),
                        parent = SafeRead(() => transform.parent == null
                            ? null
                            : transform.parent.gameObject.name),
                        path = SafeRead(() => BuildPath(transform))
                    });
                }
                catch (Exception ex)
                {
                    readErrors++;
                    rows.Add(new
                    {
                        error = $"{ex.GetType().Name}: {ex.Message}"
                    });
                }
            }

            var usable = rows.Count(r => IsUsable(r));

            return JsonResult(new
            {
                source = includeInactive
                    ? "Resources.FindObjectsOfTypeAll<Button> (includes off-screen)"
                    : "Object.FindObjectsOfType<Button> (active scenes only)",
                rootPath,
                totalButtonsFound = totalBeforeFilter,
                returned = rows.Count,
                truncated = totalBeforeFilter - skippedUnlabelled > rows.Count,
                skippedUnlabelled = skippedUnlabelled > 0 ? skippedUnlabelled : (int?)null,
                readErrors = readErrors > 0 ? readErrors : (int?)null,
                // "Usable" is all three flags true. Stated explicitly because the whole point of this
                // tool is that the three disagree, and a caller reading only one of them will be wrong.
                usableCount = usable,
                note = "A button is usable only when enabled && interactable && activeInHierarchy are "
                     + "ALL true. A 'leftover' button typically shows enabled=false with the other two "
                     + "still true.",
                buttons = rows
            });
        }

        /// <summary>
        /// The instance list, chosen by `includeInactive`.
        ///
        /// Two different Unity APIs answer two different questions and differ by ~100x here, so this
        /// is a deliberate choice rather than an implementation detail.
        /// </summary>
        private static List<UnityEngine.UI.Button> CollectButtons(
            UnityEngine.GameObject root, bool includeInactive)
        {
            if (root != null)
            {
                // Scoped to one panel: FindObjectsOfType has no overload for that, so enumerate the
                // descendants through the same generic path the proposal used (verified working).
                var found = root.GetComponentsInChildren<UnityEngine.UI.Button>(includeInactive);
                var list = new List<UnityEngine.UI.Button>();
                if (found != null)
                {
                    for (var i = 0; i < found.Count; i++) list.Add(found[i]);
                }
                return list;
            }

            if (includeInactive)
            {
                var all = UnityEngine.Resources.FindObjectsOfTypeAll<UnityEngine.UI.Button>();
                var list = new List<UnityEngine.UI.Button>();
                if (all != null)
                {
                    for (var i = 0; i < all.Count; i++) list.Add(all[i]);
                }
                return list;
            }

            return new List<UnityEngine.UI.Button>(
                UnityEngine.Object.FindObjectsOfType<UnityEngine.UI.Button>());
        }

        /// <summary>
        /// First non-null Text under the button.
        ///
        /// GetComponentsInChildren is used rather than a typed GetComponentInChildren&lt;Text&gt;()
        /// because a button's caption is often on a child rather than the button itself; both were
        /// measured working here, and this form also tolerates several Text children.
        /// </summary>
        private static string ReadLabel(UnityEngine.UI.Button button)
        {
            try
            {
                var texts = button.GetComponentsInChildren<UnityEngine.UI.Text>(true);
                if (texts == null || texts.Count == 0) return null;

                for (var i = 0; i < texts.Count; i++)
                {
                    var t = texts[i];
                    if (t == null) continue;
                    var s = t.text;
                    if (!string.IsNullOrEmpty(s)) return s;
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        private static string BuildPath(UnityEngine.Transform t)
        {
            try
            {
                var parts = new List<string>();
                for (var cur = t; cur != null && parts.Count < 12; cur = cur.parent)
                {
                    parts.Add(cur.gameObject.name);
                }
                parts.Reverse();
                return string.Join("/", parts);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Reads one flag, returning null rather than throwing.
        ///
        /// A destroyed MonoBehaviour throws on any member access, and these lists include pooled
        /// objects in exactly that state - one bad entry must not lose the whole dump.
        /// </summary>
        private static bool? SafeRead(Func<bool> read)
        {
            try { return read(); }
            catch { return null; }
        }

        private static int? SafeRead(Func<int> read)
        {
            try { return read(); }
            catch { return null; }
        }

        private static string SafeRead(Func<string> read)
        {
            try { return read(); }
            catch { return null; }
        }

        /// <summary>
        /// True only when all three flags are true, evaluated against the anonymous row via reflection.
        ///
        /// Reflection is acceptable here: the row count is bounded by 'limit' (max 500) and this runs
        /// once per call, not per frame. The alternative - a concrete DTO - buys nothing for a value
        /// that is only ever rendered.
        /// </summary>
        private static bool IsUsable(object row)
        {
            try
            {
                var t = row.GetType();
                var e = t.GetProperty("enabled")?.GetValue(row) as bool?;
                var i = t.GetProperty("interactable")?.GetValue(row) as bool?;
                var a = t.GetProperty("activeInHierarchy")?.GetValue(row) as bool?;
                return e == true && i == true && a == true;
            }
            catch
            {
                return false;
            }
        }
    }
}
