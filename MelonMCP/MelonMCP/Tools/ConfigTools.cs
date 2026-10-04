using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MelonMCP.Server;
using Newtonsoft.Json.Linq;

namespace MelonMCP.Tools
{
    /// <summary>
    /// Reads and writes MelonLoader's own preferences (MelonPreferences.cfg).
    ///
    /// WHY THIS IS NOT "just edit the cfg file": MelonPreferences keeps every entry in memory and
    /// rewrites the whole file from that model. Editing the .cfg on disk while the game runs is
    /// therefore silently lost the next time any mod (or the user) calls Save() - the in-memory
    /// value wins. These tools go through MelonPreferences so the write lands in the same model the
    /// game is actually using, then explicitly persists it.
    ///
    /// WHY IT DOES NOT DEPEND ON MelonPreferencesManager: that mod provides a human-facing in-game
    /// UI. An agent cannot open an F5 window, and requiring that mod to be installed would make
    /// these tools useless on any other setup. Everything here is built on MelonPreferences, which
    /// ships with MelonLoader itself and is present in every MelonLoader game.
    ///
    /// WHAT THIS DELIBERATELY DOES NOT DO: judge whether a change takes effect immediately.
    /// Most MelonPreferences values are read as `entry.Value` at the point of use, so they apply at
    /// once - but values consumed once during OnInitializeMelon (typically anything that INSTALLS a
    /// native hook or a Harmony patch) are already baked into the running process and cannot be
    /// changed without a restart. Only the mod itself knows which is which, and guessing would
    /// reproduce the exact trap this project keeps hitting: reading `true` back and assuming it took
    /// effect. So set_config reports the write and says plainly that restart semantics are the
    /// caller's to determine.
    /// </summary>
    internal static class ConfigIntrospection
    {
        private static Type _prefsType;
        private static FieldInfo _categoriesField;

        /// <summary>Resolved lazily: a missing MelonLoader type must not break plugin startup.</summary>
        private static bool EnsureTypes()
        {
            if (_prefsType != null) return true;

            _prefsType = TypeResolver.ResolveType("MelonLoader.MelonPreferences");
            if (_prefsType == null) return false;

            _categoriesField = _prefsType.GetField(
                "Categories",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);

            return _categoriesField != null;
        }

        public static bool Available => EnsureTypes();

        /// <summary>
        /// Enumerate every category. Categories is a static List&lt;MelonPreferences_Category&gt;, NOT
        /// a dictionary - a detail worth stating because a Dictionary cast fails with a bare
        /// NullReferenceException that says nothing about the real mistake.
        /// </summary>
        public static IList Categories
        {
            get
            {
                if (!EnsureTypes()) return null;
                return _categoriesField.GetValue(null) as IList;
            }
        }

        public static object FindCategory(string identifier)
        {
            var cats = Categories;
            if (cats == null) return null;

            foreach (var c in cats)
            {
                var id = CategoryIdentifier(c);
                if (string.Equals(id, identifier, StringComparison.OrdinalIgnoreCase))
                    return c;
            }
            return null;
        }

        public static string CategoryIdentifier(object category)
        {
            // Identifier is a property on the category but 'Entries' is a FIELD. Verified against
            // MelonLoader 0.7.3 rather than assumed - the two are not consistently shaped.
            return category?.GetType().GetProperty("Identifier")?.GetValue(category) as string;
        }

        public static IList EntriesOf(object category)
        {
            return category?.GetType().GetField("Entries")?.GetValue(category) as IList;
        }

        public static object FindEntry(object category, string identifier)
        {
            var entries = EntriesOf(category);
            if (entries == null) return null;

            foreach (var e in entries)
            {
                var id = EntryIdentifier(e);
                if (string.Equals(id, identifier, StringComparison.OrdinalIgnoreCase))
                    return e;
            }
            return null;
        }

        public static string EntryIdentifier(object entry)
        {
            return entry?.GetType().GetProperty("Identifier")?.GetValue(entry) as string;
        }

        private static object Prop(object entry, string name)
        {
            return entry?.GetType().GetProperty(name)?.GetValue(entry);
        }

        public static object Value(object entry) => Prop(entry, "BoxedValue");
        public static object DefaultValue(object entry) => Prop(entry, "DefaultValue");
        public static string Description(object entry) => Prop(entry, "Description") as string;
        public static string DisplayName(object entry) => Prop(entry, "DisplayName") as string;

        /// <summary>
        /// The declared T of MelonPreferences_Entry&lt;T&gt;. Read from the generic argument rather
        /// than from the current value's runtime type, because a null or default value would
        /// otherwise report as "Object" and make the value impossible to coerce on write.
        /// </summary>
        public static Type EntryValueType(object entry)
        {
            var t = entry?.GetType();
            while (t != null)
            {
                if (t.IsGenericType && t.GetGenericTypeDefinition().Name.StartsWith(
                        "MelonPreferences_Entry", StringComparison.Ordinal))
                {
                    return t.GetGenericArguments()[0];
                }
                t = t.BaseType;
            }
            return Value(entry)?.GetType();
        }

        /// <summary>Writes through BoxedValue, which exists on the non-generic entry base class.</summary>
        public static void SetValue(object entry, object value)
        {
            var p = entry.GetType().GetProperty("BoxedValue");
            if (p == null || !p.CanWrite)
                throw new InvalidOperationException(
                    "BoxedValue is not writable on " + entry.GetType().FullName);

            p.SetValue(entry, value);
        }

        public static void Save() => _prefsType.GetMethod("Save", BindingFlags.Public | BindingFlags.Static)
                                             ?.Invoke(null, null);

        /// <summary>
        /// Convert a JSON-ish value from the MCP request into the entry's declared type.
        /// Enum names are accepted (the cfg file stores e.g. KeyCode as a bare name), which matters
        /// because several mods here store KeyCode and writing an int would be silently wrong.
        /// </summary>
        public static object Coerce(JToken token, Type targetType, out string error)
        {
            error = null;
            if (targetType == null) { error = "entry value type could not be determined"; return null; }

            var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

            try
            {
                if (underlying == typeof(bool)) return token.Value<bool>();
                if (underlying == typeof(string)) return token.Value<string>();
                if (underlying == typeof(int)) return token.Value<int>();
                if (underlying == typeof(long)) return token.Value<long>();
                if (underlying == typeof(float)) return token.Value<float>();
                if (underlying == typeof(double)) return token.Value<double>();
                if (underlying == typeof(byte)) return token.Value<byte>();
                if (underlying == typeof(short)) return token.Value<short>();

                if (underlying.IsEnum)
                {
                    // Accept both "LeftAlt" and 5 - the cfg uses names, the schema may not.
                    if (token.Type == JTokenType.String)
                        return Enum.Parse(underlying, token.Value<string>(), ignoreCase: true);
                    return Enum.ToObject(underlying, token.Value<long>());
                }

                return token.ToObject(underlying);
            }
            catch (Exception ex)
            {
                error = $"cannot convert '{token}' to {underlying.Name}: {ex.Message}";
                return null;
            }
        }

        /// <summary>Render a value for display, keeping enum names readable and nulls explicit.</summary>
        public static string Render(object v)
        {
            if (v == null) return "null";
            if (v is string s) return s;
            return Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Opens with no arguments: nothing to supply, so the schema stays absent.</summary>
    public class ListConfigsToolDefinition : ToolDefinitionBase
    {
        public override string Name => "list_configs";

        // Reads the MelonPreferences object graph only - no Unity object involved.
        public override bool RequiresMainThread => false;


        public override string Description => @"List every MelonLoader preference category and entry, with current
value, default value, declared type and description.

This reads the LIVE in-memory model (MelonPreferences), not the .cfg file. The two can differ: a mod
or the user may have changed a value without saving.

Use this to find the exact category + key to pass to get_config / set_config. Pass 'mod' to narrow
it to one category (e.g. 'FriendlyNoclip'), and 'changedOnly' to see just the entries whose current
value differs from its default - usually the fastest way to see what has been tweaked.

NOTE: a value shown here is what the process intends to use; it does not prove the setting is
currently in effect. Values read at their point of use apply immediately; values consumed during
OnInitializeMelon (typically anything that installs a native hook) were baked in at startup.";

        protected override ToolInputSchema GetInputSchema()
        {
            return new ToolInputSchema
            {
                Properties = new Dictionary<string, ToolPropertySchema>
                {
                    ["mod"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Only this category, case-insensitive (e.g. 'FriendlyNoclip'). "
                                    + "Omit to list every category."
                    },
                    ["changedOnly"] = new ToolPropertySchema
                    {
                        Type = "boolean",
                        Description = "Only entries whose current value differs from the default. "
                                    + "Default false.",
                        Default = false
                    },
                    ["includeHidden"] = new ToolPropertySchema
                    {
                        Type = "boolean",
                        Description = "Include entries a mod marked hidden. Default true, because "
                                    + "hidden entries are still real and settable.",
                        Default = true
                    }
                }
            };
        }

        public override CallToolResult Execute(Dictionary<string, JToken> arguments)
        {
            if (!ConfigIntrospection.Available)
                return ErrorResult("MelonLoader.MelonPreferences is unavailable in this process.");

            var modFilter = GetStringArg(arguments, "mod");
            var changedOnly = GetBoolArg(arguments, "changedOnly");
            var includeHidden = GetBoolArg(arguments, "includeHidden", true);

            var cats = ConfigIntrospection.Categories;
            var result = new List<object>();
            int totalEntries = 0, totalChanged = 0;

            foreach (var cat in cats)
            {
                var identifier = ConfigIntrospection.CategoryIdentifier(cat);

                if (!string.IsNullOrEmpty(modFilter)
                    && !string.Equals(identifier, modFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var entries = ConfigIntrospection.EntriesOf(cat);
                var rows = new List<object>();
                int catChanged = 0;

                foreach (var e in entries)
                {
                    totalEntries++;

                    var hidden = e.GetType().GetProperty("IsHidden")?.GetValue(e) as bool? ?? false;
                    if (hidden && !includeHidden) continue;

                    var current = ConfigIntrospection.Value(e);
                    var def = ConfigIntrospection.DefaultValue(e);

                    // Compare rendered forms: it is stable across boxed-types and enums, and the
                    // question here is only "did someone change this away from its default".
                    var changed = !string.Equals(
                        ConfigIntrospection.Render(current),
                        ConfigIntrospection.Render(def),
                        StringComparison.Ordinal);

                    if (changed) { catChanged++; totalChanged++; }
                    if (changedOnly && !changed) continue;

                    rows.Add(new
                    {
                        key = ConfigIntrospection.EntryIdentifier(e),
                        type = ConfigIntrospection.EntryValueType(e)?.Name,
                        value = ConfigIntrospection.Render(current),
                        @default = ConfigIntrospection.Render(def),
                        changed,
                        hidden = hidden ? true : (bool?)null,
                        display = ConfigIntrospection.DisplayName(e),
                        description = ConfigIntrospection.Description(e)
                    });
                }

                if (rows.Count > 0 || string.IsNullOrEmpty(modFilter))
                {
                    result.Add(new
                    {
                        mod = identifier,
                        entryCount = entries?.Count ?? 0,
                        changedCount = catChanged,
                        entries = rows
                    });
                }
            }

            return JsonResult(new
            {
                categoryCount = result.Count,
                totalEntries,
                changedEntries = totalChanged,
                categories = result
            });
        }
    }

    /// <summary>Reads one entry, or a whole category when the key is omitted.</summary>
    public class GetConfigToolDefinition : ToolDefinitionBase
    {
        public override string Name => "get_config";

        // MelonPreferences lookup; independent of the Unity main thread.
        public override bool RequiresMainThread => false;


        public override string Description => @"Read one MelonLoader preference entry, or every entry in a
category when 'key' is omitted.

Values come from the live in-memory model. 'changed' tells you whether the current value differs
from the declared default - useful before deciding whether a reset is warranted.";

        protected override ToolInputSchema GetInputSchema()
        {
            return new ToolInputSchema
            {
                Properties = new Dictionary<string, ToolPropertySchema>
                {
                    ["mod"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Category identifier, case-insensitive (e.g. 'FriendlyNoclip')."
                    },
                    ["key"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Entry identifier. Omit to return the whole category."
                    }
                },
                Required = new List<string> { "mod" }
            };
        }

        public override CallToolResult Execute(Dictionary<string, JToken> arguments)
        {
            if (!ConfigIntrospection.Available)
                return ErrorResult("MelonLoader.MelonPreferences is unavailable in this process.");

            var mod = GetStringArg(arguments, "mod");
            var key = GetStringArg(arguments, "key");

            if (string.IsNullOrEmpty(mod))
                return ErrorResult("'mod' is required.");

            var cat = ConfigIntrospection.FindCategory(mod);
            if (cat == null)
            {
                var known = ConfigIntrospection.Categories
                    .Cast<object>()
                    .Select(ConfigIntrospection.CategoryIdentifier)
                    .Where(x => x != null)
                    .OrderBy(x => x);
                return ErrorResult($"no category '{mod}'. Known: {string.Join(", ", known)}");
            }

            if (string.IsNullOrEmpty(key))
            {
                var entries = ConfigIntrospection.EntriesOf(cat);
                var all = new List<object>();
                foreach (var e in entries)
                {
                    var cur = ConfigIntrospection.Value(e);
                    var def = ConfigIntrospection.DefaultValue(e);
                    all.Add(new
                    {
                        key = ConfigIntrospection.EntryIdentifier(e),
                        type = ConfigIntrospection.EntryValueType(e)?.Name,
                        value = ConfigIntrospection.Render(cur),
                        @default = ConfigIntrospection.Render(def),
                        changed = !string.Equals(ConfigIntrospection.Render(cur),
                                                 ConfigIntrospection.Render(def),
                                                 StringComparison.Ordinal),
                        description = ConfigIntrospection.Description(e)
                    });
                }
                return JsonResult(new { mod, entryCount = all.Count, entries = all });
            }

            var entry = ConfigIntrospection.FindEntry(cat, key);
            if (entry == null)
            {
                var known = ConfigIntrospection.EntriesOf(cat)
                    .Cast<object>()
                    .Select(ConfigIntrospection.EntryIdentifier)
                    .Where(x => x != null)
                    .OrderBy(x => x);
                return ErrorResult($"no entry '{key}' in category '{mod}'. Known: {string.Join(", ", known)}");
            }

            var value = ConfigIntrospection.Value(entry);
            var dflt = ConfigIntrospection.DefaultValue(entry);

            return JsonResult(new
            {
                mod,
                key = ConfigIntrospection.EntryIdentifier(entry),
                type = ConfigIntrospection.EntryValueType(entry)?.Name,
                value = ConfigIntrospection.Render(value),
                @default = ConfigIntrospection.Render(dflt),
                changed = !string.Equals(ConfigIntrospection.Render(value),
                                         ConfigIntrospection.Render(dflt),
                                         StringComparison.Ordinal),
                display = ConfigIntrospection.DisplayName(entry),
                description = ConfigIntrospection.Description(entry)
            });
        }
    }

    /// <summary>Writes one entry and persists it. Deliberately silent about restart semantics.</summary>
    public class SetConfigToolDefinition : ToolDefinitionBase
    {
        public override string Name => "set_config";

        // Writes a MelonPreferences entry. Off the main thread on purpose: a mod's own setting is
        // often the thing you want to flip (e.g. to bisect) while the main thread is stuck.
        public override bool RequiresMainThread => false;


        public override string Description => @"Set one MelonLoader preference entry and save it to disk.

Writes through MelonPreferences (the live model) rather than editing MelonPreferences.cfg directly.
This matters: MelonPreferences rewrites the whole file from memory, so a hand-edited .cfg is lost the
next time anything calls Save(). Callers must pass 'confirm' with the same mod+key, so a mistargeted
write cannot land silently.

IMPORTANT - restart semantics are yours to determine, not this tool's to guess:
- A value read as `entry.Value` at its point of use applies immediately.
- A value consumed once inside OnInitializeMelon - typically anything that INSTALLS a native hook or
  a Harmony patch - is already baked into the running process. Writing the new value here changes
  what the NEXT launch will do; it does not retract a hook that is already installed.
Reading 'true' back after this call proves the value was stored, NOT that the behaviour changed.
This tool does not attempt to detect which case applies, because that depends on each mod's
internals and a wrong guess is worse than no guess.

Returns the previous value so the change can be undone or verified.";

        protected override ToolInputSchema GetInputSchema()
        {
            return new ToolInputSchema
            {
                Properties = new Dictionary<string, ToolPropertySchema>
                {
                    ["mod"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Category identifier, case-insensitive."
                    },
                    ["key"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Entry identifier."
                    },
                    ["value"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "New value. JSON type matters: pass true/false for booleans, "
                                    + "numbers unquoted for numerics, and bare names for enums "
                                    + "(e.g. \"LeftAlt\" for a KeyCode entry)."
                    },
                    ["confirm"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Must equal \"<mod>.<key>\" exactly. Guards against writing to "
                                    + "the wrong entry."
                    },
                    ["save"] = new ToolPropertySchema
                    {
                        Type = "boolean",
                        Description = "Persist to MelonPreferences.cfg (default true). Set false to "
                                    + "change only the running process - useful for a temporary "
                                    + "experiment that should not survive a restart.",
                        Default = true
                    }
                },
                Required = new List<string> { "mod", "key", "value", "confirm" }
            };
        }

        public override CallToolResult Execute(Dictionary<string, JToken> arguments)
        {
            if (!ConfigIntrospection.Available)
                return ErrorResult("MelonLoader.MelonPreferences is unavailable in this process.");

            var mod = GetStringArg(arguments, "mod");
            var key = GetStringArg(arguments, "key");
            var confirm = GetStringArg(arguments, "confirm");
            var save = GetBoolArg(arguments, "save", true);

            if (string.IsNullOrEmpty(mod) || string.IsNullOrEmpty(key))
                return ErrorResult("'mod' and 'key' are required.");

            var expected = $"{mod}.{key}";
            if (!string.Equals(confirm, expected, StringComparison.Ordinal))
            {
                return ErrorResult(
                    $"'confirm' must be exactly '{expected}' (got '{(confirm ?? "<null>")}'). " +
                    "This guard exists so a mistargeted write cannot land silently.");
            }

            if (!arguments.TryGetValue("value", out var rawValue))
                return ErrorResult("'value' is required.");

            var cat = ConfigIntrospection.FindCategory(mod);
            if (cat == null)
                return ErrorResult($"no category '{mod}'.");

            var entry = ConfigIntrospection.FindEntry(cat, key);
            if (entry == null)
                return ErrorResult($"no entry '{key}' in category '{mod}'.");

            var targetType = ConfigIntrospection.EntryValueType(entry);
            var coerced = ConfigIntrospection.Coerce(rawValue, targetType, out var convError);
            if (convError != null) return ErrorResult(convError);

            var before = ConfigIntrospection.Value(entry);
            var beforeText = ConfigIntrospection.Render(before);
            var afterText = ConfigIntrospection.Render(coerced);

            if (string.Equals(beforeText, afterText, StringComparison.Ordinal))
            {
                // Still persist if asked: the caller may be re-asserting a value after a cfg edit.
                if (save) ConfigIntrospection.Save();
                return JsonResult(new
                {
                    mod,
                    key,
                    type = targetType?.Name,
                    previousValue = beforeText,
                    value = afterText,
                    changed = false,
                    saved = save,
                    note = "Value already had this setting; nothing changed."
                });
            }

            try
            {
                ConfigIntrospection.SetValue(entry, coerced);
            }
            catch (Exception ex)
            {
                return ErrorResult($"writing '{expected}' threw {ex.GetType().Name}: {ex.Message}");
            }

            string readBack = null;
            try { readBack = ConfigIntrospection.Render(ConfigIntrospection.Value(entry)); }
            catch { /* a validator may have rejected it; reported below */ }

            if (save)
            {
                try { ConfigIntrospection.Save(); }
                catch (Exception ex)
                {
                    return ErrorResult(
                        $"value was set in memory to {afterText}, but saving failed: " +
                        $"{ex.GetType().Name}: {ex.Message}. It will be lost on exit.");
                }
            }

            var applied = string.Equals(readBack, afterText, StringComparison.Ordinal);

            return JsonResult(new
            {
                mod,
                key,
                type = targetType?.Name,
                previousValue = beforeText,
                value = readBack,
                changed = true,
                saved = save,
                applied = applied,
                restartRequired = "unknown - see tool description",
                note = applied
                    ? (save
                        ? "Set and saved. Whether this affects the running process depends on when "
                        + "the mod reads the value; if it installs a native hook or Harmony patch "
                        + "during OnInitializeMelon, a restart is required."
                        : "Set in memory only (save=false); will not survive a restart.")
                    : "Value was written but reads back differently, so a validator or setter "
                      + "transformed it. Treat the reported value as authoritative."
            });
        }
    }

    /// <summary>Restores defaults, the natural counterpart to a bisection experiment.</summary>
    public class ResetConfigToolDefinition : ToolDefinitionBase
    {
        public override string Name => "reset_config";

        // MelonPreferences write; no Unity object access.
        public override bool RequiresMainThread => false;


        public override string Description => @"Restore MelonLoader preference entries to their declared
defaults.

Use this to clean up after a bisection experiment. Scope it with 'key' for a single entry, or omit
'key' to reset a whole category. Pass 'dryRun' to see exactly what would change first.

Same restart caveat as set_config: resetting a value that a mod consumed during OnInitializeMelon
does not undo anything already installed in the running process.";

        protected override ToolInputSchema GetInputSchema()
        {
            return new ToolInputSchema
            {
                Properties = new Dictionary<string, ToolPropertySchema>
                {
                    ["mod"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Category identifier, case-insensitive."
                    },
                    ["key"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Single entry to reset. Omit to reset every entry in the "
                                    + "category."
                    },
                    ["dryRun"] = new ToolPropertySchema
                    {
                        Type = "boolean",
                        Description = "Report what would change without writing anything. "
                                    + "Default false.",
                        Default = false
                    },
                    ["save"] = new ToolPropertySchema
                    {
                        Type = "boolean",
                        Description = "Persist to MelonPreferences.cfg (default true).",
                        Default = true
                    }
                },
                Required = new List<string> { "mod" }
            };
        }

        public override CallToolResult Execute(Dictionary<string, JToken> arguments)
        {
            if (!ConfigIntrospection.Available)
                return ErrorResult("MelonLoader.MelonPreferences is unavailable in this process.");

            var mod = GetStringArg(arguments, "mod");
            var key = GetStringArg(arguments, "key");
            var dryRun = GetBoolArg(arguments, "dryRun");
            var save = GetBoolArg(arguments, "save", true);

            if (string.IsNullOrEmpty(mod))
                return ErrorResult("'mod' is required.");

            var cat = ConfigIntrospection.FindCategory(mod);
            if (cat == null)
                return ErrorResult($"no category '{mod}'.");

            IEnumerable<object> targets;
            if (!string.IsNullOrEmpty(key))
            {
                var single = ConfigIntrospection.FindEntry(cat, key);
                if (single == null)
                    return ErrorResult($"no entry '{key}' in category '{mod}'.");
                targets = new[] { single };
            }
            else
            {
                targets = ConfigIntrospection.EntriesOf(cat).Cast<object>();
            }

            var changes = new List<object>();
            var failures = new List<string>();

            foreach (var e in targets)
            {
                var cur = ConfigIntrospection.Render(ConfigIntrospection.Value(e));
                var def = ConfigIntrospection.Render(ConfigIntrospection.DefaultValue(e));

                if (string.Equals(cur, def, StringComparison.Ordinal))
                {
                    changes.Add(new
                    {
                        key = ConfigIntrospection.EntryIdentifier(e),
                        from = cur,
                        to = def,
                        changed = false
                    });
                    continue;
                }

                if (!dryRun)
                {
                    try
                    {
                        ConfigIntrospection.SetValue(e, ConfigIntrospection.DefaultValue(e));
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{ConfigIntrospection.EntryIdentifier(e)}: " +
                                     $"{ex.GetType().Name}: {ex.Message}");
                        continue;
                    }
                }

                changes.Add(new
                {
                    key = ConfigIntrospection.EntryIdentifier(e),
                    from = cur,
                    to = def,
                    changed = true
                });
            }

            var changedCount = changes.Count(c => (bool)c.GetType().GetProperty("changed").GetValue(c));

            if (!dryRun && changedCount > 0 && save)
            {
                try { ConfigIntrospection.Save(); }
                catch (Exception ex)
                {
                    return ErrorResult(
                        $"reset applied in memory but saving failed: {ex.GetType().Name}: {ex.Message}");
                }
            }

            return JsonResult(new
            {
                mod,
                key = string.IsNullOrEmpty(key) ? null : key,
                dryRun,
                saved = !dryRun && save,
                changedCount,
                changes,
                failures = failures.Count > 0 ? failures : null,
                note = "Resetting a value does not retract anything already installed in the "
                     + "running process; restart if the mod consumed it during OnInitializeMelon."
            });
        }
    }
}
