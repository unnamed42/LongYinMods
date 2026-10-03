using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace MelonMCP.Tools
{
    /// <summary>
    /// Resolves a type name to a <see cref="Type"/> in the running game process.
    ///
    /// Written for IL2CPP: types surface as Il2CppInterop proxies whose simple names usually carry
    /// no "Il2Cpp." prefix, and game types live in assemblies that are not statically known at
    /// build time. Resolution therefore goes through several strategies, in order of precision:
    ///
    ///   1. An explicit assembly-qualified name (Type.GetType handles this directly).
    ///   2. A full name match in any loaded assembly.
    ///   3. A namespace-qualified guess ("UnityEngine." + name, "Il2Cpp." + name, ...).
    ///   4. A unique simple-name match across all loaded assemblies (indexed once).
    ///
    /// Step 4 is the one that makes short names usable - the previous implementation stopped at
    /// step 3 and so failed for most game types, which is why callers had to pass full names.
    /// </summary>
    public static class TypeResolver
    {
        private static readonly object SyncRoot = new object();
        private static Dictionary<string, Type> _byFullName;
        private static Dictionary<string, List<Type>> _bySimpleName;

        /// <summary>Namespaces tried when a bare name is given, in order.</summary>
        private static readonly string[] NamePrefixes =
        {
            "",
            "UnityEngine.",
            "UnityEngine.SceneManagement.",
            "UnityEngine.UI.",
            "Il2Cpp.",
            "System.",
            "System.Collections.",
            "System.Collections.Generic.",
        };

        /// <summary>Seed assemblies are trusted to be indexed first, so they win simple-name ties.</summary>
        private static readonly string[] PreferredAssemblyPrefixes =
        {
            "Assembly-CSharp",
            "UnityEngine",
        };

        /// <summary>
        /// Resolves <paramref name="typeName"/>, or returns null. Never throws: callers treat a
        /// null result as "unknown type" and report it themselves.
        /// </summary>
        public static Type ResolveType(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return null;

            typeName = typeName.Trim();

            // 1. Assembly-qualified name, e.g. "Foo.Bar, Assembly-CSharp".
            if (typeName.Contains(","))
            {
                var qualified = TryTypeGetType(typeName);
                if (qualified != null) return qualified;
            }

            var index = EnsureIndex();

            // 2. Exact full name.
            if (index.FullNames.TryGetValue(typeName, out var exact))
            {
                return exact;
            }

            // 3. Prefix guesses for bare names.
            if (!typeName.Contains("."))
            {
                foreach (var prefix in NamePrefixes)
                {
                    if (prefix.Length == 0) continue;
                    if (index.FullNames.TryGetValue(prefix + typeName, out var prefixed))
                    {
                        return prefixed;
                    }
                }
            }

            // 4. Simple-name match, preferring game/Unity assemblies.
            if (index.SimpleNames.TryGetValue(typeName, out var candidates) && candidates.Count > 0)
            {
                return SelectBest(candidates);
            }

            // 5. Last resort: the same name cached on a nested type or a generic definition.
            var nested = FindBySuffix(index, "." + typeName);
            if (nested != null) return nested;

            return null;
        }

        private static Type TryTypeGetType(string name)
        {
            try
            {
                return Type.GetType(name, throwOnError: false);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Picks a winner when several assemblies define the same simple name. Game code is more
        /// likely to be the intended target than a framework type of the same name.
        /// </summary>
        private static Type SelectBest(List<Type> candidates)
        {
            foreach (var prefix in PreferredAssemblyPrefixes)
            {
                var preferred = candidates.FirstOrDefault(t =>
                    t.Assembly != null &&
                    t.Assembly.GetName().Name != null &&
                    t.Assembly.GetName().Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

                if (preferred != null) return preferred;
            }

            // Deterministic fallback so repeated calls agree.
            return candidates
                .OrderBy(t => t.Assembly == null ? string.Empty : t.Assembly.GetName().Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.FullName, StringComparer.OrdinalIgnoreCase)
                .First();
        }

        private static Type FindBySuffix(TypeIndex index, string suffix)
        {
            foreach (var pair in index.FullNames)
            {
                if (pair.Key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Value;
                }
            }
            return null;
        }

        private sealed class TypeIndex
        {
            public readonly Dictionary<string, Type> FullNames = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, List<Type>> SimpleNames = new Dictionary<string, List<Type>>(StringComparer.OrdinalIgnoreCase);
        }

        private static TypeIndex EnsureIndex()
        {
            lock (SyncRoot)
            {
                if (_byFullName != null && _bySimpleName != null)
                {
                    var cached = new TypeIndex();
                    foreach (var pair in _byFullName) cached.FullNames[pair.Key] = pair.Value;
                    foreach (var pair in _bySimpleName) cached.SimpleNames[pair.Key] = pair.Value;
                    return cached;
                }

                var index = new TypeIndex();

                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    AddAssemblyToIndex(assembly, index);
                }

                _byFullName = index.FullNames;
                _bySimpleName = index.SimpleNames;

                return index;
            }
        }

        private static void AddAssemblyToIndex(Assembly assembly, TypeIndex index)
        {
            if (assembly == null || assembly.IsDynamic) return;

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                // Partially loadable assemblies are common here; index whatever did load.
                types = ex.Types.Where(t => t != null).ToArray();
            }
            catch
            {
                return;
            }

            foreach (var type in types)
            {
                if (type == null) continue;

                var fullName = type.FullName;
                if (!string.IsNullOrEmpty(fullName) && !index.FullNames.ContainsKey(fullName))
                {
                    index.FullNames[fullName] = type;
                }

                var simpleName = type.Name;

                // Strip the generic arity suffix so "List`1" is also reachable as "List".
                int tick = simpleName.IndexOf('`');
                if (tick > 0) simpleName = simpleName.Substring(0, tick);

                if (string.IsNullOrEmpty(simpleName)) continue;

                if (!index.SimpleNames.TryGetValue(simpleName, out var list))
                {
                    list = new List<Type>();
                    index.SimpleNames[simpleName] = list;
                }

                // A simple name can be defined once per assembly; keep each definition.
                if (!list.Contains(type)) list.Add(type);
            }
        }

        /// <summary>Drops the cached index so newly loaded assemblies become visible.</summary>
        public static void InvalidateCache()
        {
            lock (SyncRoot)
            {
                _byFullName = null;
                _bySimpleName = null;
            }
        }
    }
}
