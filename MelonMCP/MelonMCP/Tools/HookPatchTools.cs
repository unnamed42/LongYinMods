using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MelonMCP.Server;
using Newtonsoft.Json.Linq;

namespace MelonMCP.Tools
{
    /// <summary>
    /// Answers, in one call, the three questions that are otherwise three separate slow loops:
    /// is the patch attached, will it fire, and is it a no-op.
    ///
    /// "Harmony reported the patch applied" only proves Patch() did not throw. On IL2CPP that is
    /// genuinely not enough: Il2CppDetourMethodPatcher silently leaves IsValid false when it cannot
    /// find the generated method's NativeMethodInfoPtr_* field, Harmony then falls back to a managed
    /// IL patch, and native callers bypass it entirely - with no log line anywhere.
    /// </summary>
    public class HookPatchInfoToolDefinition : ToolDefinitionBase
    {
        public override string Name => "hook_patch_info";

        public override string Description => @"Report the runtime patch state of a method: which patcher Harmony
actually selected and whether it is valid, the exact bound target signature and IL address, every
prefix/postfix/transpiler with its owner and priority, and the method's REAL runtime entry bytes.

Use this before concluding 'the patch did not fire'. Key readings:
- patcherIsValid == false on an Il2CppDetourMethodPatcher means the native detour was never installed;
  native callers bypass the patch even though Harmony reported success.
- entryBytes starting with 'ff 25' is the HEALTHY state for a patched IL2CPP method (Il2CppInterop's
  detour is installed). It does NOT mean Harmony was bypassed.
- prefixes/postfixes counts of 0 mean nothing is attached to this method at all.

Pass either typeName + methodName, or owner to list everything a given mod has patched.";

        protected override ToolInputSchema GetInputSchema()
        {
            return new ToolInputSchema
            {
                Properties = new Dictionary<string, ToolPropertySchema>
                {
                    ["typeName"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Type name, short ('BattleMapData') or full ('Il2Cpp.BattleMapData'). "
                                    + "Omit to use the 'owner' mode instead."
                    },
                    ["methodName"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Method name. Omit to inspect every patched method on the type."
                    },
                    ["argCount"] = new ToolPropertySchema
                    {
                        Type = "integer",
                        Description = "Select an overload by parameter count. STRONGLY recommended when "
                                    + "overloads exist: picking by name alone is unreliable and a "
                                    + "same-named empty stub may be selected instead of the real method."
                    },
                    ["owner"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Instead of a type, list every patched method owned by this Harmony "
                                    + "ID (e.g. the mod name). Shows what a mod actually hooked."
                    },
                    ["includeEntryBytes"] = new ToolPropertySchema
                    {
                        Type = "boolean",
                        Description = "Include the first 16 runtime entry bytes (default true).",
                        Default = true
                    }
                }
            };
        }

        public override CallToolResult Execute(Dictionary<string, JToken> arguments)
        {
            var typeName = GetStringArg(arguments, "typeName");
            var methodName = GetStringArg(arguments, "methodName");
            var argCount = GetIntArg(arguments, "argCount", -1);
            var owner = GetStringArg(arguments, "owner");
            var includeBytes = GetBoolArg(arguments, "includeEntryBytes", true);

            if (string.IsNullOrWhiteSpace(typeName) && string.IsNullOrWhiteSpace(owner))
            {
                return ErrorResult("Provide either 'typeName' (optionally with 'methodName') or 'owner'.");
            }

            // Owner mode: enumerate everything that mod has patched.
            if (string.IsNullOrWhiteSpace(typeName))
            {
                return JsonResult(BuildOwnerReport(owner, includeBytes));
            }

            var type = TypeResolver.ResolveType(typeName);
            if (type == null)
            {
                return ErrorResult($"Type '{typeName}' not found. Try the full name, e.g. 'Il2Cpp.{typeName}'.");
            }

            var targets = new List<MethodBase>();

            if (!string.IsNullOrWhiteSpace(methodName))
            {
                var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                            | BindingFlags.Instance | BindingFlags.Static
                                            | BindingFlags.DeclaredOnly)
                    .Where(m => m.Name == methodName)
                    .ToList();

                // Fall back to inherited members if the type declares nothing by that name.
                if (methods.Count == 0)
                {
                    methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                            | BindingFlags.Instance | BindingFlags.Static)
                        .Where(m => m.Name == methodName)
                        .ToList();
                }

                if (methods.Count == 0)
                {
                    return ErrorResult($"Method '{methodName}' not found on '{type.FullName}'.");
                }

                if (argCount >= 0)
                {
                    methods = methods.Where(m => m.GetParameters().Length == argCount).ToList();
                    if (methods.Count == 0)
                    {
                        return ErrorResult(
                            $"No overload of '{type.FullName}.{methodName}' takes {argCount} parameter(s). "
                            + "Available overloads were considered but none matched.");
                    }
                }

                targets.AddRange(methods);
            }
            else
            {
                // No method name: report every patched method declared on this type.
                targets.AddRange(PatchIntrospection.GetAllPatchedMethods()
                    .Where(m => m.DeclaringType == type));
            }

            if (targets.Count == 0)
            {
                return JsonResult(new
                {
                    type = type.FullName,
                    patched = false,
                    note = "No Harmony patches are attached to any method of this type in this process."
                });
            }

            var reports = targets.Select(m => BuildReport(m, includeBytes)).ToList();

            return JsonResult(new
            {
                type = type.FullName,
                overloadCount = targets.Count,
                report = reports
            });
        }

        private object BuildOwnerReport(string owner, bool includeBytes)
        {
            var rows = new List<object>();

            foreach (var method in PatchIntrospection.GetAllPatchedMethods())
            {
                var info = PatchIntrospection.GetPatchInfo(method);
                if (info == null) continue;

                int prefixHits = info.prefixes.Count(p => MatchesOwner(p, owner));
                int postfixHits = info.postfixes.Count(p => MatchesOwner(p, owner));
                int transpilerHits = info.transpilers.Count(p => MatchesOwner(p, owner));
                int finalizerHits = info.finalizers.Count(p => MatchesOwner(p, owner));

                if (prefixHits + postfixHits + transpilerHits + finalizerHits == 0) continue;

                rows.Add(new
                {
                    target = PatchIntrospection.FullTargetName(method),
                    signature = PatchIntrospection.DescribeMethod(method),
                    argCount = method.GetParameters().Length,
                    prefix = prefixHits,
                    postfix = postfixHits,
                    transpiler = transpilerHits,
                    finalizer = finalizerHits
                });
            }

            return new
            {
                owner,
                patchedMethodCount = rows.Count,
                methods = rows,
                note = rows.Count == 0
                    ? "Nothing in this process is patched by that Harmony owner ID. Check the exact ID "
                    + "used when the Harmony instance was constructed."
                    : null
            };
        }

        private static bool MatchesOwner(HarmonyLib.Patch p, string owner)
        {
            if (string.IsNullOrEmpty(owner)) return true;
            return p.owner != null && p.owner.IndexOf(owner, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Assembles the per-method report. Kept separate so the owner mode can reuse it.
        /// </summary>
        internal static object BuildReport(MethodBase method, bool includeBytes)
        {
            var state = new PatchIntrospection.MethodPatchState
            {
                TargetType = method.DeclaringType?.FullName,
                TargetMethod = method.Name,
                TargetSignature = PatchIntrospection.DescribeMethod(method)
            };

            // The IL address is what disambiguates same-named overloads; it is not a native address
            // and must not be compared against one.
            try
            {
                state.TargetIlAddress = $"0x{method.MethodHandle.GetFunctionPointer().ToInt64():X}";
            }
            catch
            {
                state.TargetIlAddress = null;
            }

            PatchIntrospection.DescribePatcher(method, state);

            var info = PatchIntrospection.GetPatchInfo(method);
            if (info != null)
            {
                state.Prefixes = PatchIntrospection.ToEntries(info.prefixes);
                state.Postfixes = PatchIntrospection.ToEntries(info.postfixes);
                state.Transpilers = PatchIntrospection.ToEntries(info.transpilers);
                state.Finalizers = PatchIntrospection.ToEntries(info.finalizers);
                state.IlManipulators = PatchIntrospection.ToEntries(info.ilmanipulators);
            }

            string entryInterpretation = null;
            if (includeBytes)
            {
                state.EntryBytes = PatchIntrospection.ReadEntryBytes(method, 16, out _);
                entryInterpretation = PatchIntrospection.InterpretEntryBytes(state.EntryBytes);
            }

            int totalPatches = state.Prefixes.Count + state.Postfixes.Count
                             + state.Transpilers.Count + state.Finalizers.Count
                             + state.IlManipulators.Count;

            return new
            {
                target = PatchIntrospection.FullTargetName(method),
                signature = state.TargetSignature,
                argCount = method.GetParameters().Length,
                isStatic = method.IsStatic,
                ilAddress = state.TargetIlAddress,
                patcherType = state.PatcherType,
                patcherIsValid = state.PatcherIsValid,
                patchCounts = new
                {
                    prefixes = state.Prefixes.Count,
                    postfixes = state.Postfixes.Count,
                    transpilers = state.Transpilers.Count,
                    finalizers = state.Finalizers.Count,
                    ilmanipulators = state.IlManipulators.Count
                },
                prefixes = state.Prefixes,
                postfixes = state.Postfixes,
                transpilers = state.Transpilers,
                entryBytes = state.EntryBytes,
                entryInterpretation,
                attached = totalPatches > 0,
                notes = state.Notes.Count > 0 ? state.Notes : null
            };
        }
    }

    /// <summary>
    /// Lists every Harmony patch in the process, including other mods'. Needed when several mods
    /// touch the same method and you have to find out who else is in the way.
    /// </summary>
    public class ListPatchesToolDefinition : ToolDefinitionBase
    {
        public override string Name => "list_patches";

        public override string Description => @"List all Harmony patches in the process, including those applied
by OTHER mods. Each row gives the target's full signature, the owner Harmony ID, and how many
prefixes/postfixes/transpilers/finalizers are attached.

Use this to find conflicts: if two mods patch the same method, both rows appear here.";

        protected override ToolInputSchema GetInputSchema()
        {
            return new ToolInputSchema
            {
                Properties = new Dictionary<string, ToolPropertySchema>
                {
                    ["filter"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Only include targets whose type or method name contains this text."
                    },
                    ["owner"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Only include patches whose owner Harmony ID contains this text."
                    },
                    ["limit"] = new ToolPropertySchema
                    {
                        Type = "integer",
                        Description = "Maximum rows to return (default 200).",
                        Default = 200,
                        Minimum = 1,
                        Maximum = 2000
                    }
                }
            };
        }

        public override CallToolResult Execute(Dictionary<string, JToken> arguments)
        {
            var filter = GetStringArg(arguments, "filter");
            var owner = GetStringArg(arguments, "owner");
            var limit = GetIntArg(arguments, "limit", 200);

            var all = PatchIntrospection.GetAllPatchedMethods().ToList();
            var rows = new List<object>();
            int totalMatching = 0;

            foreach (var method in all)
            {
                var info = PatchIntrospection.GetPatchInfo(method);
                if (info == null) continue;

                var target = PatchIntrospection.FullTargetName(method);

                if (!string.IsNullOrEmpty(filter)
                    && target.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                var owners = info.prefixes.Select(p => p.owner)
                    .Concat(info.postfixes.Select(p => p.owner))
                    .Concat(info.transpilers.Select(p => p.owner))
                    .Concat(info.finalizers.Select(p => p.owner))
                    .Where(o => o != null)
                    .Distinct()
                    .ToList();

                if (!string.IsNullOrEmpty(owner)
                    && !owners.Any(o => o.IndexOf(owner, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    continue;
                }

                totalMatching++;

                if (rows.Count >= limit) continue;

                rows.Add(new
                {
                    target,
                    signature = PatchIntrospection.DescribeMethod(method),
                    argCount = method.GetParameters().Length,
                    owners,
                    prefixes = info.prefixes.Length,
                    postfixes = info.postfixes.Length,
                    transpilers = info.transpilers.Length,
                    finalizers = info.finalizers.Length
                });
            }

            return JsonResult(new
            {
                totalPatchedMethodsInProcess = all.Count,
                totalMatching,
                returned = rows.Count,
                truncated = totalMatching > rows.Count,
                patches = rows
            });
        }
    }
}
