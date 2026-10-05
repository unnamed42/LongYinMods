using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MelonMCP.Server;
using Newtonsoft.Json.Linq;

namespace MelonMCP.Tools
{
    /// <summary>
    /// Watches a field and records every change, so "some code changed this value" becomes
    /// "the value changed at frame N, from A to B".
    ///
    /// This is deliberately the polling implementation rather than a hardware write breakpoint.
    /// Polling costs no memory permissions, cannot corrupt the target, and cannot crash the game;
    /// a dr0-dr3 breakpoint would answer "who wrote it" more precisely but carries real risk and
    /// has only four slots. In practice most questions are answered by knowing WHEN and WHAT, and
    /// the log timeline can then be correlated by hand.
    ///
    /// Known limitation, documented rather than hidden: a value written and restored within a single
    /// poll interval is not observed.
    /// </summary>
    public class WatchFieldToolDefinition : ToolDefinitionBase
    {
        public override string Name => "watch_field";

        public override string Description => @"Poll a field or property every N frames and record every observed change, turning 'this value is
wrong and I do not know who changed it' into a timeline of (frame, old, new).

Call again with the returned watchId to read what accumulated, or unwatch_field to stop. Changes
accumulate between calls, so you can trigger an action then poll.

Samples, so a value written and restored between samples is missed, and it reports when a value
changed - never which code changed it.";

        protected override ToolInputSchema GetInputSchema()
        {
            return new ToolInputSchema
            {
                Properties = new Dictionary<string, ToolPropertySchema>
                {
                    ["typeName"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Type declaring the field. Only needed the first time; later calls can "
                                    + "pass watchId alone to read accumulated changes."
                    },
                    ["fieldName"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Field or property name to watch."
                    },
                    ["watchId"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Existing watch to read. Omit to start a new one."
                    },
                    ["intervalFrames"] = new ToolPropertySchema
                    {
                        Type = "integer",
                        Description = "Sample every N frames (default 1 = every frame).",
                        Default = 1,
                        Minimum = 1,
                        Maximum = 600
                    },
                    ["maxChanges"] = new ToolPropertySchema
                    {
                        Type = "integer",
                        Description = "Stop recording after this many changes (default 200).",
                        Default = 200,
                        Minimum = 1,
                        Maximum = 10000
                    },
                    ["clear"] = new ToolPropertySchema
                    {
                        Type = "boolean",
                        Description = "Discard the changes recorded so far before returning, so the next read "
                                    + "only shows what happens after this call."
                    }
                }
            };
        }

        public override CallToolResult Execute(Dictionary<string, JToken> arguments)
        {
            var watchId = GetStringArg(arguments, "watchId");
            var clear = GetBoolArg(arguments, "clear", false);

            // Read-back mode.
            if (!string.IsNullOrEmpty(watchId))
            {
                var existing = FieldWatcher.Get(watchId);
                if (existing == null)
                {
                    return ErrorResult($"No watch with id '{watchId}'. Start one by passing typeName + fieldName. "
                                     + "Use unwatch_field with 'all' to see nothing is left, then watch_field to start again.");
                }

                var snap = existing.ToReport();
                if (clear) existing.ClearChanges();
                return JsonResult(snap);
            }

            var typeName = GetStringArg(arguments, "typeName");
            var fieldName = GetStringArg(arguments, "fieldName");

            if (string.IsNullOrWhiteSpace(typeName) || string.IsNullOrWhiteSpace(fieldName))
            {
                return ErrorResult("Provide typeName and fieldName to start a watch, or watchId to read one.");
            }

            var type = TypeResolver.ResolveType(typeName);
            if (type == null)
            {
                return ErrorResult($"Type '{typeName}' not found.");
            }

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                                     | BindingFlags.Instance | BindingFlags.Static;

            var field = type.GetField(fieldName, flags);
            var prop = field == null ? type.GetProperty(fieldName, flags) : null;

            if (field == null && prop == null)
            {
                return ErrorResult($"No field or property '{fieldName}' on '{type.FullName}'.");
            }

            var interval = GetIntArg(arguments, "intervalFrames", 1);
            var maxChanges = GetIntArg(arguments, "maxChanges", 200);

            var watch = FieldWatcher.Start(type, field, prop, interval, maxChanges);

            if (watch.StartError != null)
            {
                return ErrorResult(watch.StartError);
            }

            return JsonResult(watch.ToReport());
        }
    }

    /// <summary>
    /// Stops a watch and returns whatever it recorded.
    /// </summary>
    public class UnwatchFieldToolDefinition : ToolDefinitionBase
    {
        public override string Name => "unwatch_field";

        public override string Description => @"Stop a field watch and return everything it recorded.

Pass 'all' as watchId to stop every active watch at once.";

        protected override ToolInputSchema GetInputSchema()
        {
            return new ToolInputSchema
            {
                Properties = new Dictionary<string, ToolPropertySchema>
                {
                    ["watchId"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Watch to stop, or 'all'."
                    }
                },
                Required = new List<string> { "watchId" }
            };
        }

        public override CallToolResult Execute(Dictionary<string, JToken> arguments)
        {
            var watchId = GetStringArg(arguments, "watchId");
            if (string.IsNullOrWhiteSpace(watchId))
            {
                return ErrorResult("watchId is required (use 'all' to stop everything).");
            }

            if (watchId.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                var all = FieldWatcher.StopAll();
                return JsonResult(new { stopped = all.Count, results = all });
            }

            var result = FieldWatcher.Stop(watchId);
            if (result == null)
            {
                return ErrorResult($"No watch with id '{watchId}'.");
            }

            return JsonResult(result);
        }
    }

    /// <summary>
    /// A single active watch: what to read, how often, and what changed.
    ///
    /// Internal and concrete rather than anonymous/private so the tools can call it directly -
    /// the earlier draft poked at a private type through reflection, which was both fragile and
    /// pointless.
    /// </summary>
    internal sealed class FieldWatch
    {
        internal string Id;
        internal Type TargetType;
        internal FieldInfo Field;
        internal PropertyInfo Property;
        internal int IntervalFrames;
        internal int MaxChanges;

        /// <summary>Non-null when the watch could not be started; reported verbatim to the caller.</summary>
        internal string StartError;

        /// <summary>Non-null when sampling is failing at runtime (e.g. the instance went away).</summary>
        internal string SampleError;

        private object _instance;
        private bool _instanceResolved;
        private object _lastValue;
        private bool _hasLastValue;
        private int _frameCounter;
        private int _sampleCount;
        private bool _overflowed;

        private readonly List<FieldChange> _changes = new List<FieldChange>();

        /// <summary>Guards _changes/_lastValue against concurrent read from the RPC thread.</summary>
        private readonly object _sync = new object();

        internal static FieldWatch Create(Type type, FieldInfo field, PropertyInfo prop,
                                         int intervalFrames, int maxChanges)
        {
            var w = new FieldWatch
            {
                TargetType = type,
                Field = field,
                Property = prop,
                IntervalFrames = intervalFrames,
                MaxChanges = maxChanges
            };

            bool isStatic = (field != null && field.IsStatic)
                         || (prop?.GetMethod != null && prop.GetMethod.IsStatic);

            if (isStatic)
            {
                w._instance = null;
                w._instanceResolved = true;
            }
            else if (FieldWatcher.TryResolveInstance(type, out var instance, out var why))
            {
                w._instance = instance;
                w._instanceResolved = true;
            }
            else
            {
                w.StartError = why;
                return w;
            }

            // Take an initial reading so the first reported change has a meaningful "from" value.
            w.Sample();
            return w;
        }

        internal void ClearChanges()
        {
            lock (_sync)
            {
                _changes.Clear();
                _overflowed = false;
            }
        }

        /// <summary>
        /// Reads the member once and records a change if the value differs from the last reading.
        /// </summary>
        internal void Sample()
        {
            if (!_instanceResolved) return;

            bool isStatic = (Field != null && Field.IsStatic)
                         || (Property?.GetMethod != null && Property.GetMethod.IsStatic);

            if (!isStatic && _instance == null) return;

            object current;
            try
            {
                current = Field != null
                    ? Field.GetValue(_instance)
                    : Property.GetValue(_instance);
            }
            catch (Exception ex)
            {
                SampleError = $"reading '{MemberName}' threw {ex.GetType().Name}: {ex.Message}";
                return;
            }

            SampleError = null;

            lock (_sync)
            {
                _sampleCount++;

                if (!_hasLastValue)
                {
                    _lastValue = current;
                    _hasLastValue = true;
                    return;
                }

                if (ValuesEqual(_lastValue, current)) return;

                if (_changes.Count >= MaxChanges)
                {
                    _overflowed = true;
                    _lastValue = current;
                    return;
                }

                _changes.Add(new FieldChange
                {
                    Frame = UnityEngine.Time.frameCount,
                    Time = DateTime.Now.ToString("HH:mm:ss.fff"),
                    OldValue = Render(_lastValue),
                    NewValue = Render(current)
                });

                _lastValue = current;
            }
        }

        internal string MemberName => Field?.Name ?? Property?.Name;

        /// <summary>
        /// Value equality is by-value for primitives/strings/enums and by-reference for everything
        /// else, because for a reference the interesting event is that it now points somewhere else.
        /// </summary>
        private static bool ValuesEqual(object a, object b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;

            var t = a.GetType();
            if (a is string || t.IsPrimitive || t.IsEnum)
            {
                return a.Equals(b);
            }

            return false;
        }

        private static string Render(object v)
        {
            if (v == null) return "null";
            try
            {
                // A collection's ToString is useless; report its size instead so a change in
                // contents is at least visible as a count change.
                if (v is System.Collections.IEnumerable e && !(v is string))
                {
                    int n = 0;
                    foreach (var _ in e) { n++; if (n > 99999) break; }
                    return $"<{v.GetType().Name} count={n}>";
                }
                return v.ToString();
            }
            catch
            {
                return "<unprintable>";
            }
        }

        internal object ToReport()
        {
            lock (_sync)
            {
                return new
                {
                    watchId = Id,
                    targetType = TargetType?.FullName,
                    member = MemberName,
                    isStatic = (Field != null && Field.IsStatic)
                            || (Property?.GetMethod != null && Property.GetMethod.IsStatic),
                    intervalFrames = IntervalFrames,
                    samples = _sampleCount,
                    changeCount = _changes.Count,
                    overflowed = _overflowed,
                    currentValue = _hasLastValue ? Render(_lastValue) : null,
                    error = StartError ?? SampleError,
                    changes = _changes.Select(c => new
                    {
                        frame = c.Frame,
                        time = c.Time,
                        oldValue = c.OldValue,
                        newValue = c.NewValue
                    }).ToList()
                };
            }
        }

        internal void Tick()
        {
            _frameCounter++;
            if (_frameCounter % IntervalFrames != 0) return;
            Sample();
        }
    }

    /// <summary>One observed change of a watched member.</summary>
    internal sealed class FieldChange
    {
        internal int Frame;
        internal string Time;
        internal string OldValue;
        internal string NewValue;
    }

    /// <summary>
    /// Owns the active watches and drives sampling from the Unity update loop.
    ///
    /// Sampling runs on the main thread (the plugin's OnUpdate) because reading game state from a
    /// background thread while the game mutates it is a data race.
    /// </summary>
    public static class FieldWatcher
    {
        private static readonly Dictionary<string, FieldWatch> Watches =
            new Dictionary<string, FieldWatch>(StringComparer.Ordinal);

        private static readonly object RegistryLock = new object();
        private static int _nextId = 1;

        internal static FieldWatch Start(Type type, FieldInfo field, PropertyInfo prop,
                                        int intervalFrames, int maxChanges)
        {
            var watch = FieldWatch.Create(type, field, prop, intervalFrames, maxChanges);

            // A failed start is not registered: there is nothing to sample, and leaving it in the
            // table would make a later unwatch_field('all') report a phantom watch.
            if (watch.StartError != null) return watch;

            lock (RegistryLock)
            {
                watch.Id = "w" + _nextId++;
                Watches[watch.Id] = watch;
            }

            return watch;
        }

        /// <summary>
        /// Finds a live instance of the type.
        ///
        /// Three paths, because game data types are frequently plain C# objects that Unity does not
        /// track: FindObjectsOfType covers Unity objects, a static self-typed member covers the
        /// singleton pattern used throughout this codebase, and a MonoBehaviour scan with an exact
        /// runtime-type match covers the rest.
        /// </summary>
        internal static bool TryResolveInstance(Type type, out object instance, out string error)
        {
            instance = null;
            error = null;

            try
            {
                // Path 1: a static self-typed field or property (the singleton pattern used
                // throughout this codebase). Checked first because it is exact and cheap.
                foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (!type.IsAssignableFrom(f.FieldType)) continue;
                    var v = f.GetValue(null);
                    if (v != null) { instance = v; return true; }
                }

                foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (p.GetMethod == null || !type.IsAssignableFrom(p.PropertyType)) continue;
                    try
                    {
                        var v = p.GetValue(null);
                        if (v != null) { instance = v; return true; }
                    }
                    catch { }
                }

                // Path 2: Unity objects. Invoked through the UnityHelper cache rather than calling
                // UnityEngine.Object.FindObjectsOfType directly, because the generic overload returns
                // an Il2CppArrayBase<T> that would drag a reference to Il2Cppmscorlib into this
                // project purely for the array element type.
                if (typeof(UnityEngine.Object).IsAssignableFrom(type))
                {
                    var found = UnityHelper.FindObjectsOfType(type);
                    if (found != null && found.Length > 0)
                    {
                        instance = found[0];
                        return true;
                    }
                }

                // Path 3: any MonoBehaviour whose exact runtime type matches.
                var behaviours = UnityHelper.FindObjectsOfType(typeof(UnityEngine.MonoBehaviour));
                foreach (var b in behaviours)
                {
                    if (b == null) continue;
                    if (b.GetType() == type) { instance = b; return true; }
                }
            }
            catch (Exception ex)
            {
                error = $"instance lookup threw {ex.GetType().Name}: {ex.Message}";
                return false;
            }

            error = $"no live instance of '{type.FullName}' found. Non-Unity types have no global registry; "
                  + "watch a field you can reach through a known singleton, or read it with execute_csharp.";
            return false;
        }

        internal static FieldWatch Get(string id)
        {
            lock (RegistryLock)
            {
                return Watches.TryGetValue(id, out var w) ? w : null;
            }
        }

        internal static object Stop(string id)
        {
            FieldWatch w;
            lock (RegistryLock)
            {
                if (!Watches.TryGetValue(id, out w)) return null;
                Watches.Remove(id);
            }
            return w.ToReport();
        }

        internal static List<object> StopAll()
        {
            List<FieldWatch> all;
            lock (RegistryLock)
            {
                all = Watches.Values.ToList();
                Watches.Clear();
            }
            return all.Select(w => w.ToReport()).ToList();
        }

        /// <summary>Drives all watches. Called from the plugin update loop on the main thread.</summary>
        public static void Tick()
        {
            List<FieldWatch> snapshot;
            lock (RegistryLock)
            {
                if (Watches.Count == 0) return;
                snapshot = Watches.Values.ToList();
            }

            foreach (var w in snapshot)
            {
                try
                {
                    w.Tick();
                }
                catch (Exception ex)
                {
                    // A broken watch must not take down the update loop.
                    MelonMCPPlugin.Logger?.Warning($"Field watch {w.Id} failed: {ex.Message}");
                }
            }
        }

        /// <summary>Drops all watches; used during mod teardown so nothing survives a hot reload.</summary>
        public static void Reset()
        {
            lock (RegistryLock)
            {
                Watches.Clear();
            }
        }

        /// <summary>Number of active watches, for diagnostics.</summary>
        public static int Count
        {
            get { lock (RegistryLock) { return Watches.Count; } }
        }
    }
}
