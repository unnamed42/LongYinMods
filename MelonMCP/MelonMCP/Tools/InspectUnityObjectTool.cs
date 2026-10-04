using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Il2CppInterop.Runtime;
using MelonMCP.Server;
using Newtonsoft.Json.Linq;

namespace MelonMCP.Tools
{
    /// <summary>
    /// Reads a set of named fields across every live instance of a type, in one call.
    ///
    /// WHY THIS EXISTS: answering "what are the current values of these fields on every
    /// AreaBuildingIconController" by hand means writing the same enumeration, the same null guards and
    /// the same string building every time - and each line is a chance to fail. In practice that loop
    /// was rewritten eight-plus times in a single investigation.
    ///
    /// HOW IT AVOIDS THE IL2CPP PROXY COLLAPSE that killed the older inspection tools:
    /// <c>list_components</c> / <c>inspect_component</c> / <c>toggle_behaviour</c> all failed because
    /// they tried to GENERICALLY DISCOVER components and properties, and under IL2CPP every component
    /// reports as <c>UnityEngine.Component</c> while reflection over proxy properties boxes into
    /// <c>Il2CppSystem.Object</c> and yields nothing readable. This tool inverts that: the caller
    /// supplies the exact type name AND the exact field paths, so nothing is discovered by reflection.
    /// That constraint is the reason it works - do not "improve" it into an automatic field finder.
    ///
    /// INSTANCE ENUMERATION uses <c>Resources.FindObjectsOfTypeAll</c>, NOT GetComponents. The
    /// non-generic overload takes an <c>Il2CppSystem.Type</c>, which lets a runtime <c>Type</c> be
    /// used without reflecting into a generic method (which the REPL and older helpers both failed at).
    ///
    /// FAILURE ISOLATION is the point, not a nicety: a single field on a single instance that throws
    /// (a destroyed object, a null intermediate in the path) renders as &lt;err:...&gt; in that cell
    /// only. Every other column and every other instance still returns. This was called out explicitly
    /// in the request that produced this tool, because the previous generation of tools failed
    /// all-or-nothing.
    /// </summary>
    public class InspectUnityObjectToolDefinition : ToolDefinitionBase
    {
        public override string Name => "inspect_unity_object";

        // Dereferences live Unity objects, so it must run on the main thread.
        public override bool RequiresMainThread => true;

        public override string Description => @"Read named fields across instances of a type, in one call.

Use this instead of hand-writing a foreach over Resources.FindObjectsOfTypeAll every time you need
to see current field values on live objects.

REQUIRED: typeName. RECOMMENDED: fields (dot paths into nested objects).

Fields are dot-separated paths evaluated one segment at a time, e.g.
  'buildingData.buildingID'  reads obj.buildingData, then .buildingID on it.
If a segment is null or throws, THAT CELL renders as <err:...> and everything else still returns -
a failure never aborts the batch. Omit 'fields' to just count instances.

Examples:
  inspect_unity_object(typeName='AreaBuildingIconController',
                       fields=['buildingData.buildingID', 'buildingData.lv'],
                       where='buildingData.buildingID=-1')
  inspect_unity_object(typeName='AreaUnitController', count=5)

NOTES
- Instances come from Resources.FindObjectsOfTypeAll, which includes inactive objects and objects in
  other scenes, so counts run higher than what is visible on screen.
- 'count' is capped (default 20) because these types are numerous: AreaUnitController and
  AreaBuildingIconController were measured at 226 and 114 instances respectively, and an unbounded
  dump buries the answer.
- 'where' matches on the string form of the field, so it works for numbers, enums and booleans alike.
- Field and property names are resolved by reflection on the ACTUAL instance type, walking base
  types, so inherited members work.";

        protected override ToolInputSchema GetInputSchema()
        {
            return new ToolInputSchema
            {
                Properties = new Dictionary<string, ToolPropertySchema>
                {
                    ["typeName"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Type to enumerate. Short name works ('AreaBuildingIconController'); "
                                    + "it is resolved through TypeResolver, which also accepts "
                                    + "namespace-qualified and assembly-qualified forms."
                    },
                    ["fields"] = new ToolPropertySchema
                    {
                        Type = "array",
                        Description = "Field paths to read, dot-separated for nesting. Omit to only "
                                    + "count instances.",
                        Items = new ToolPropertySchema { Type = "string" }
                    },
                    ["count"] = new ToolPropertySchema
                    {
                        Type = "integer",
                        Description = "Maximum instances to return (default 20, max 200).",
                        Default = 20,
                        Minimum = 1,
                        Maximum = 200
                    },
                    ["where"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Filter as 'fieldPath=value'. Only instances whose field renders "
                                    + "exactly equal to value are returned. The filter is applied "
                                    + "BEFORE 'count', so a sparse match is not lost to the limit."
                    },
                    ["includeNullFields"] = new ToolPropertySchema
                    {
                        Type = "boolean",
                        Description = "When true, instances whose filter field is unreadable are kept "
                                    + "rather than skipped. Default false.",
                        Default = false
                    }
                },
                Required = new List<string> { "typeName" }
            };
        }

        public override CallToolResult Execute(Dictionary<string, JToken> arguments)
        {
            var typeName = GetStringArg(arguments, "typeName");
            if (string.IsNullOrWhiteSpace(typeName))
                return ErrorResult("'typeName' is required.");

            var fields = GetStringArrayArg(arguments, "fields");
            var count = GetIntArg(arguments, "count", 20);
            if (count < 1) count = 1;
            if (count > 200) count = 200;

            var where = GetStringArg(arguments, "where");
            var includeNullFields = GetBoolArg(arguments, "includeNullFields");

            var type = TypeResolver.ResolveType(typeName);
            if (type == null)
            {
                return ErrorResult(
                    $"could not resolve type '{typeName}'. Try the fully qualified name, or use "
                    + "list_types to find it. Note that some Unity engine types are not resolvable by "
                    + "short name through this path.");
            }

            // Enumerate instances. A null array is a legitimate "none exist for this type" answer and
            // is reported as such, not as an error - the distinction matters when narrowing a search.
            System.Collections.IList instances;
            string enumError = null;
            try
            {
                instances = FindInstances(type);
            }
            catch (Exception ex)
            {
                instances = null;
                enumError = $"{ex.GetType().Name}: {ex.Message}";
            }

            if (enumError != null)
            {
                return ErrorResult(
                    $"enumerating instances of '{type.FullName}' threw {enumError}. "
                    + "Resources.FindObjectsOfTypeAll only accepts types deriving from UnityEngine.Object.");
            }

            int totalSeen = instances?.Count ?? 0;

            // Parse the filter once, outside the loop.
            string filterPath = null, filterValue = null;
            if (!string.IsNullOrWhiteSpace(where))
            {
                var eq = where.IndexOf('=');
                if (eq <= 0)
                {
                    return ErrorResult(
                        $"'where' must look like 'field.path=value' (got '{where}').");
                }
                filterPath = where.Substring(0, eq).Trim();
                filterValue = where.Substring(eq + 1).Trim();
            }

            var rows = new List<object>();
            int matched = 0;
            int skippedUnreadable = 0;

            foreach (var instance in instances ?? (System.Collections.IList)new object[0])
            {
                // Per-instance isolation: one destroyed or half-initialised object must not abort
                // the batch. This is the behaviour the tool exists to provide.
                try
                {
                    if (instance == null) continue;

                    if (filterPath != null)
                    {
                        var probe = ReadPath(instance, filterPath, out var probeError);
                        var probeText = probeError != null ? null : Render(probe);

                        if (probeText == null)
                        {
                            skippedUnreadable++;
                            if (!includeNullFields) continue;
                        }
                        else if (!ValuesMatch(probeText, filterValue))
                        {
                            continue;
                        }
                    }

                    matched++;

                    // Apply the limit AFTER filtering, so a narrow filter over many instances is
                    // not defeated by the cap.
                    if (rows.Count >= count) continue;

                    var cellValues = new List<object>();
                    foreach (var path in fields)
                    {
                        try
                        {
                            var v = ReadPath(instance, path, out var err);
                            cellValues.Add(err != null ? $"<err:{err}>" : Render(v));
                        }
                        catch (Exception ex)
                        {
                            cellValues.Add($"<err:{ex.GetType().Name}>");
                        }
                    }

                    // Report the instance identity where we can get it cheaply; a failure here must
                    // not lose the row.
                    string name = null;
                    try
                    {
                        var nameMember = FindMember(instance.GetType(), "name");
                        if (nameMember != null) name = Render(GetMemberValue(instance, nameMember));
                    }
                    catch { }

                    rows.Add(new
                    {
                        index = rows.Count,
                        name,
                        fields = fields.Count == 0
                            ? null
                            : fields.Select((p, i) => new { path = p, value = cellValues[i] }).ToList()
                    });
                }
                catch (Exception ex)
                {
                    rows.Add(new
                    {
                        index = rows.Count,
                        name = (string)null,
                        error = $"{ex.GetType().Name}: {ex.Message}"
                    });
                }
            }

            var header = $"{matched} matching instance(s) of {type.FullName}";
            if (filterPath != null)
                header += $"  [where {filterPath}={filterValue}]";

            return JsonResult(new
            {
                type = type.FullName,
                totalInstances = totalSeen,
                matched,
                returned = rows.Count,
                truncated = matched > rows.Count,
                skippedUnreadable,
                filter = filterPath == null ? null : new { path = filterPath, value = filterValue },
                summary = header
                    + (rows.Count < matched ? $" (showing first {rows.Count})" : string.Empty)
                    + (skippedUnreadable > 0
                        ? $"  [{skippedUnreadable} instance(s) skipped: filter field unreadable]"
                        : string.Empty),
                instances = rows
            });
        }

        /// <summary>
        /// Returns every live instance of <paramref name="type"/> as a plain List&lt;object&gt;.
        ///
        /// Uses the GENERIC Resources.FindObjectsOfTypeAll&lt;T&gt;() overload, invoked through
        /// MakeGenericMethod. That is not the obvious choice - the non-generic overload taking an
        /// Il2CppSystem.Type looks simpler and is what this first used - but it is WRONG, and wrong
        /// in a way that produces plausible-looking output rather than an error:
        ///
        /// The non-generic overload is declared as returning Il2CppReferenceArray&lt;Object&gt;, so
        /// every element is wrapped as Il2CppObjectBase of the STATIC element type. Only index 0
        /// happens to carry the concrete proxy type; from index 1 on, GetType() reports
        /// UnityEngine.Object even though the underlying native class is e.g. RectTransform. Reading
        /// a member that exists only on the derived type then fails with "no member 'position' on
        /// Object" - while members that also exist on Object (like `name`) keep working, which makes
        /// it look like a per-field problem instead of a per-instance one.
        ///
        /// The generic overload is typed Il2CppReferenceArray&lt;T&gt; and preserves the concrete type
        /// on every element, because the array's element type is the requested type. One
        /// MakeGenericMethod call per query is negligible; the earlier stack overflow came from
        /// repeated heavy reflection inside a snippet, not from a single generic instantiation.
        /// </summary>
        private static System.Collections.IList FindInstances(Type type)
        {
            if (!typeof(UnityEngine.Object).IsAssignableFrom(type))
            {
                throw new ArgumentException(
                    $"{type.FullName} does not derive from UnityEngine.Object");
            }

            var generic = GenericFindMethod.Value.MakeGenericMethod(type);
            var array = generic.Invoke(null, null);
            if (array == null) return new List<object>();

            // Il2CppReferenceArray<T> implements IList<T> but not the non-generic IList, so it is
            // walked as IEnumerable. Result order matches the underlying array.
            var result = new List<object>();
            foreach (var item in (System.Collections.IEnumerable)array)
            {
                result.Add(item);
            }
            return result;
        }

        /// <summary>
        /// The open generic Resources.FindObjectsOfTypeAll&lt;T&gt;() MethodInfo, resolved once.
        ///
        /// Cached because resolving by LINQ over GetMethods on every call would be wasteful, and
        /// because the selection has to be exact: the non-generic overload has the same name, and
        /// picking it would reintroduce the element-type collapse documented above.
        /// </summary>
        private static readonly Lazy<MethodInfo> GenericFindMethod = new Lazy<MethodInfo>(() =>
        {
            var method = typeof(UnityEngine.Resources)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "FindObjectsOfTypeAll"
                                  && m.IsGenericMethodDefinition
                                  && m.GetParameters().Length == 0);

            if (method == null)
            {
                throw new InvalidOperationException(
                    "Resources.FindObjectsOfTypeAll<T>() not found; the Unity version may differ.");
            }

            return method;
        });

        /// <summary>
        /// Walks a dot-separated member path, one segment at a time.
        ///
        /// Returns null and sets <paramref name="error"/> at the first failing segment, naming the
        /// segment that failed - "which part of the path broke" is the useful part of the message.
        /// </summary>
        private static object ReadPath(object root, string path, out string error)
        {
            error = null;
            if (root == null) { error = "null root"; return null; }
            if (string.IsNullOrWhiteSpace(path)) { error = "empty path"; return null; }

            object current = root;
            var segments = path.Split('.');

            for (int i = 0; i < segments.Length; i++)
            {
                var segment = segments[i].Trim();
                if (segment.Length == 0) { error = $"empty segment at position {i}"; return null; }

                if (current == null)
                {
                    error = $"{string.Join(".", segments.Take(i))} is null";
                    return null;
                }

                var member = FindMember(current.GetType(), segment);
                if (member == null)
                {
                    error = $"no member '{segment}' on {current.GetType().Name}";
                    return null;
                }

                try
                {
                    current = GetMemberValue(current, member);
                }
                catch (Exception ex)
                {
                    // The inner exception carries the real cause; the outer one is usually a
                    // TargetInvocationException wrapper that says nothing useful.
                    var inner = ex.InnerException ?? ex;
                    error = $"{segment}: {inner.GetType().Name}";
                    return null;
                }
            }

            return current;
        }

        /// <summary>
        /// Finds a field or property by name, including inherited and non-public ones.
        ///
        /// Both are tried because IL2CPP proxies expose some members as properties and others as
        /// fields, and the caller should not have to know which. The walk up BaseType is explicit
        /// because GetMember with a NonPublic binding flag does not reliably return private members
        /// of base classes.
        /// </summary>
        private static MemberInfo FindMember(Type type, string name)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                                     | BindingFlags.Instance | BindingFlags.Static;

            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                try
                {
                    var prop = t.GetProperty(name, flags);
                    if (prop != null && prop.GetIndexParameters().Length == 0) return prop;

                    var field = t.GetField(name, flags);
                    if (field != null) return field;
                }
                catch
                {
                    // A proxy type can throw from GetProperty on malformed metadata; keep walking.
                }
            }

            return null;
        }

        private static object GetMemberValue(object instance, MemberInfo member)
        {
            switch (member)
            {
                case PropertyInfo p:
                    return p.GetValue(instance);
                case FieldInfo f:
                    return f.GetValue(instance);
                default:
                    return null;
            }
        }

        /// <summary>
        /// Compares a rendered field value against the caller's 'where' value.
        ///
        /// Case-insensitive on purpose. Booleans render as "true"/"false" (lowercase, C# convention)
        /// while a caller - and any JSON-derived argument - naturally writes "True". An exact match
        /// then silently selects nothing, which looks identical to "no instance has that value" and
        /// sends you hunting for a bug in the data. Numeric and string values are unaffected by the
        /// insensitivity, and no realistic filter depends on case to discriminate.
        /// </summary>
        private static bool ValuesMatch(string actual, string expected)
        {
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Renders a value for one cell.
        ///
        /// Deliberately string-based rather than typed: the caller compares against text and reads
        /// text, and the underlying values include IL2CPP proxy objects whose ToString is the most
        /// reliable thing available. Nulls are explicit so "read as null" is distinguishable from
        /// "failed to read".
        /// </summary>
        private static string Render(object value)
        {
            if (value == null) return "null";
            if (value is string s) return s;
            if (value is bool b) return b ? "true" : "false";

            try
            {
                var text = value.ToString();
                return text ?? "null";
            }
            catch (Exception ex)
            {
                return $"<err:{ex.GetType().Name}>";
            }
        }
    }
}
