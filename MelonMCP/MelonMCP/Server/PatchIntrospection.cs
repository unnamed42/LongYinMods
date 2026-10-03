using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using HarmonyLib.Public.Patching;

namespace MelonMCP.Server
{
    /// <summary>
    /// Reflection helpers over Harmony's patch bookkeeping.
    ///
    /// The reason this exists rather than plain Harmony calls: the piece of information that
    /// actually answers "is my patch going to fire?" is the patcher kind and its IsValid flag, and
    /// both live on types that are not part of the public surface.
    ///
    ///  - <c>HarmonyLib.Public.Patching.MethodPatcher</c> is public but has NO IsValid member; the
    ///    flag is declared on the concrete subclass.
    ///  - For IL2CPP the concrete subclass is
    ///    <c>Il2CppInterop.HarmonySupport.Il2CppDetourMethodPatcher</c>, which is <c>internal</c>,
    ///    and its <c>IsValid</c> is an <c>internal</c> property.
    ///
    /// So everything below goes through reflection deliberately, and the code is written to degrade
    /// to "unknown" rather than throw when the game's Harmony/Il2CppInterop version does not match
    /// what was compiled against.
    /// </summary>
    public static class PatchIntrospection
    {
        private static readonly string[] PatcherTypeNames =
        {
            "Il2CppInterop.HarmonySupport.Il2CppDetourMethodPatcher",
            "HarmonyLib.Public.Patching.NativeDetourMethodPatcher",
            "HarmonyLib.Public.Patching.ManagedMethodPatcher"
        };

        /// <summary>
        /// Describes the runtime patch state of a single method.
        /// </summary>
        public sealed class MethodPatchState
        {
            public string TargetType { get; set; }
            public string TargetMethod { get; set; }
            public string TargetSignature { get; set; }
            public string TargetIlAddress { get; set; }
            public string PatcherType { get; set; }
            public bool? PatcherIsValid { get; set; }
            public List<PatchEntry> Prefixes { get; set; } = new List<PatchEntry>();
            public List<PatchEntry> Postfixes { get; set; } = new List<PatchEntry>();
            public List<PatchEntry> Transpilers { get; set; } = new List<PatchEntry>();
            public List<PatchEntry> Finalizers { get; set; } = new List<PatchEntry>();
            public List<PatchEntry> IlManipulators { get; set; } = new List<PatchEntry>();
            public string EntryBytes { get; set; }
            public List<string> Notes { get; set; } = new List<string>();
        }

        public sealed class PatchEntry
        {
            public string Owner { get; set; }
            public int Priority { get; set; }
            public string PatchMethod { get; set; }
            public string DeclaringAssembly { get; set; }
            public bool IsStatic { get; set; }
            public string State { get; set; }
        }

        /// <summary>
        /// Reads the PatchInfo for a method, if it has any patches at all.
        /// </summary>
        public static PatchInfo GetPatchInfo(MethodBase method)
        {
            try
            {
                return method.GetPatchInfo();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Names the patcher Harmony actually selected for this method, and reads IsValid off it.
        ///
        /// This is the datum the whole design hinges on: Il2CppDetourMethodPatcher only sets IsValid
        /// when it successfully located the generated method's NativeMethodInfoPtr_* field. When it
        /// cannot, it leaves IsValid false and logs nothing, Harmony silently falls back to a
        /// managed IL patch, and a patch that "applied successfully" never sees a native call.
        /// </summary>
        public static void DescribePatcher(MethodBase method, MethodPatchState state)
        {
            MethodPatcher patcher = null;
            try
            {
                patcher = method.GetMethodPatcher();
            }
            catch (Exception ex)
            {
                state.Notes.Add($"GetMethodPatcher threw: {ex.GetType().Name}: {ex.Message}");
            }

            if (patcher == null)
            {
                state.PatcherType = null;
                state.Notes.Add("No MethodPatcher resolved; Harmony is using the plain managed path.");
                return;
            }

            var type = patcher.GetType();
            state.PatcherType = type.FullName;

            // IsValid is internal on Il2CppDetourMethodPatcher, so it is not reachable through the
            // public MethodPatcher base type. Walk the concrete type, then its bases.
            var prop = FindProperty(type, "IsValid");
            if (prop != null)
            {
                try
                {
                    state.PatcherIsValid = prop.GetValue(patcher) as bool?;
                }
                catch (Exception ex)
                {
                    state.Notes.Add($"IsValid getter threw: {ex.GetType().Name}");
                }
            }
            else
            {
                // Not every patcher has the flag; ManagedMethodPatcher legitimately does not.
                state.PatcherIsValid = null;
            }

            if (type.FullName != null && type.FullName.Contains("Il2CppDetourMethodPatcher")
                && state.PatcherIsValid == false)
            {
                state.Notes.Add(
                    "Il2CppDetourMethodPatcher.IsValid is FALSE: this is the dangerous case. The native "
                    + "detour was NOT installed, so native callers bypass the patch entirely while "
                    + "Harmony still reports the patch as applied. The usual cause is that the generated "
                    + "method has no NativeMethodInfoPtr_* field to hook.");
            }
        }

        /// <summary>
        /// Reads the first bytes of the method's NATIVE entry point as they exist at runtime.
        ///
        /// Deliberately the runtime bytes, not the on-disk ones: Il2CppInterop rewrites native entries
        /// to `ff 25 &lt;disp32&gt;` (jmp qword ptr [rip+disp32]) with the jump slot living outside the
        /// module image. Seeing ff 25 here is the healthy, already-hooked state.
        ///
        /// IMPORTANT: this does NOT use MethodHandle.GetFunctionPointer(). For an IL2CPP proxy that
        /// returns the MANAGED WRAPPER address, which is not inside any native function at all - it
        /// lives in an unmapped-looking region and disassembling it yields meaningless bytes. An
        /// earlier version of this method made exactly that mistake and reported a bogus "no jump at
        /// entry, patch not installed" conclusion for a perfectly healthy detour.
        ///
        /// The correct path is the generated proxy's own NativeMethodInfoPtr_* static field, which
        /// points at the Il2CppMethodInfo, whose first pointer-sized member is methodPointer - the
        /// real native entry. That is precisely the address Il2CppInterop installs its detour on.
        /// </summary>
        public static string ReadEntryBytes(MethodBase method, int count, out long entryAddress, out string how)
        {
            entryAddress = 0;
            how = null;

            var nativeEntry = ResolveNativeEntry(method, out how);
            if (nativeEntry == 0) return null;

            entryAddress = nativeEntry;

            var bytes = new byte[count];
            if (!NativeMemory.TryReadBytes(nativeEntry, count, out bytes)) return null;
            return BitConverter.ToString(bytes).Replace("-", " ");
        }

        /// <summary>
        /// Resolves the real native entry of an IL2CPP method.
        ///
        /// Returns 0 and sets <paramref name="how"/> to the reason when it cannot be found, so the
        /// caller can say "unknown" instead of printing bytes from the wrong address.
        /// </summary>
        internal static long ResolveNativeEntry(MethodBase method, out string how)
        {
            how = null;

            if (method == null || method.DeclaringType == null)
            {
                how = "no method or declaring type";
                return 0;
            }

            // The generated Il2CppInterop proxy declares one static field per method, named
            // NativeMethodInfoPtr_<Method>_<Access>_<Return>_<n>. The exact name is not derivable with
            // certainty (access/return/overload suffixes vary), so match on the method name.
            var flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

            FieldInfo field = null;
            try
            {
                var prefix = "NativeMethodInfoPtr_" + method.Name + "_";
                field = method.DeclaringType
                    .GetFields(flags)
                    .FirstOrDefault(f => f.Name.StartsWith(prefix, StringComparison.Ordinal));
            }
            catch (Exception ex)
            {
                how = $"enumerating NativeMethodInfoPtr_* fields threw {ex.GetType().Name}";
                return 0;
            }

            if (field == null)
            {
                how = $"no NativeMethodInfoPtr_{method.Name}_* field on {method.DeclaringType.Name}. "
                    + "Not an IL2CPP proxy method, or the field was renamed by a different generator version.";
                return 0;
            }

            long infoPtr;
            try
            {
                var value = field.GetValue(null);
                infoPtr = value is IntPtr ip ? ip.ToInt64() : Convert.ToInt64(value);
            }
            catch (Exception ex)
            {
                how = $"reading {field.Name} threw {ex.GetType().Name}";
                return 0;
            }

            if (infoPtr == 0)
            {
                how = $"{field.Name} is null";
                return 0;
            }

            // Il2CppMethodInfo's first pointer-sized member is methodPointer. Reading offset 0 is
            // deliberate and is the documented layout used by Il2CppInterop itself when it installs
            // the detour (BepInEx IL2CPPDetourMethodPatcher reads originalNativeMethodInfo.MethodPointer).
            if (!NativeMemory.TryReadUInt64(infoPtr, out var entry) || entry == 0)
            {
                how = $"could not read methodPointer from Il2CppMethodInfo at 0x{infoPtr:X}";
                return 0;
            }

            how = $"{field.Name} -> Il2CppMethodInfo 0x{infoPtr:X} -> methodPointer";
            return (long)entry;
        }

        /// <summary>
        /// Every patched method in the process, including other mods'. Enumerating this is the only
        /// reliable way to answer "how many patches are on this method".
        /// </summary>
        public static IEnumerable<MethodBase> GetAllPatchedMethods()
        {
            try
            {
                // PatchProcessor is where the enumerator actually lives; Harmony.GetAllPatchedMethods
                // is only a thin passthrough and is not resolvable from every 0Harmony build.
                return PatchProcessor.GetAllPatchedMethods().ToList();
            }
            catch
            {
                return Enumerable.Empty<MethodBase>();
            }
        }

        public static List<PatchEntry> ToEntries(Patch[] patches)
        {
            var list = new List<PatchEntry>();
            if (patches == null) return list;

            foreach (var p in patches)
            {
                var entry = new PatchEntry
                {
                    Owner = p.owner,
                    Priority = p.priority
                };

                try
                {
                    var mi = p.PatchMethod;
                    if (mi != null)
                    {
                        entry.PatchMethod = DescribeMethod(mi);
                        entry.IsStatic = mi.IsStatic;
                        entry.DeclaringAssembly = mi.DeclaringType?.Assembly?.GetName()?.Name;
                    }
                }
                catch (Exception ex)
                {
                    entry.PatchMethod = $"<unresolved: {ex.GetType().Name}>";
                }

                list.Add(entry);
            }

            return list;
        }

        /// <summary>
        /// Human-readable "ReturnType Type.Method(paramType paramName, ...)" including parameter
        /// names, because Harmony binds prefixes by parameter name and a wrong name silently
        /// produces a no-op patch.
        /// </summary>
        public static string DescribeMethod(MethodBase method)
        {
            if (method == null) return "<null>";

            try
            {
                var pars = string.Join(", ", method.GetParameters()
                    .Select(p => $"{SimpleName(p.ParameterType)} {p.Name}"));

                var ret = method is MethodInfo mi ? SimpleName(mi.ReturnType) : (method is ConstructorInfo ? "ctor" : "?");
                var decl = method.DeclaringType != null ? SimpleName(method.DeclaringType) + "." : "";
                return $"{ret} {decl}{method.Name}({pars})";
            }
            catch
            {
                return method.Name;
            }
        }

        public static string SimpleName(Type t)
        {
            if (t == null) return "?";

            if (t.IsByRef) return SimpleName(t.GetElementType()) + "&";
            if (t.IsArray) return SimpleName(t.GetElementType()) + "[]";

            if (t.IsGenericType)
            {
                var name = t.Name;
                var tick = name.IndexOf('`');
                if (tick >= 0) name = name.Substring(0, tick);
                return name + "<" + string.Join(", ", t.GetGenericArguments().Select(SimpleName)) + ">";
            }

            return t.Name;
        }

        private static PropertyInfo FindProperty(Type type, string name)
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            for (var t = type; t != null; t = t.BaseType)
            {
                var prop = t.GetProperty(name, flags);
                if (prop != null) return prop;
            }
            return null;
        }

        /// <summary>
        /// Formats a MethodBase as "full type name::method name" for matching against tool args.
        /// </summary>
        public static string FullTargetName(MethodBase method)
        {
            if (method == null) return null;
            var decl = method.DeclaringType?.FullName;
            return decl == null ? method.Name : $"{decl}.{method.Name}";
        }

        /// <summary>
        /// Explains what the entry bytes mean, so the caller does not have to remember the ff 25
        /// convention.
        ///
        /// Deliberately careful in the no-jump case: a raw prologue means the detour is not at this
        /// address, but on its own it does NOT prove the patch is inactive. Cross-check
        /// patcherType / patcherIsValid and the patch counts before drawing a conclusion - an earlier
        /// version of this text asserted "not installed" outright and produced a wrong verdict when
        /// pointed at the wrong address.
        /// </summary>
        public static string InterpretEntryBytes(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return null;

            var first = hex.Split(' ')[0];
            switch (first.ToUpperInvariant())
            {
                case "FF":
                    if (hex.StartsWith("FF 25", StringComparison.OrdinalIgnoreCase))
                        return "ff 25 = jmp qword ptr [rip+disp32]: the native entry is hooked by "
                             + "Il2CppInterop's detour. This is the NORMAL state for a patched IL2CPP "
                             + "method and does NOT mean Harmony was bypassed.";
                    return "ff .. = indirect jump: entry appears redirected.";
                case "E9":
                    return "e9 = rel32 jump: entry redirected to another function (native detour or thunk).";
                default:
                    return "Raw prologue, no jump at entry: nothing is detoured at THIS address. "
                         + "If patcherType is Il2CppDetourMethodPatcher with patcherIsValid true, "
                         + "suspect that this is not the real native entry rather than that the patch "
                         + "is missing - check nativeEntryResolvedVia.";
            }
        }
    }
}
