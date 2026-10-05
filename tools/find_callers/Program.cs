using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;

namespace FindCallers
{
    /// <summary>
    /// Answers "who calls this method" and "what does this method call", from the cpp2il output.
    ///
    /// WHY THIS IS A STANDALONE TOOL AND NOT AN MCP TOOL
    /// It reads STATIC files in output/cpp2il_out/, not the running game. An MCP tool would have to
    /// load 20 MB of assemblies inside the game process, add a Cecil dependency to the mod, and
    /// would be unusable whenever the game is closed - all to answer a question that has nothing to
    /// do with runtime state. The project already keeps offline analyzers in tools/
    /// (il2cpp_unwind.py, recompile_mod.sh); this belongs with them.
    ///
    /// WHY CECIL AND NOT DECOMPILATION
    /// The cpp2il assemblies carry Cpp2ILInjected.CallAnalysis attributes recording every call edge.
    /// Reading those attributes takes ~0.4 s for Assembly-CSharp and ~3.6 s for all 58 assemblies.
    /// The alternative - decompiling everything with ilspycmd and grepping - was measured at roughly
    /// ten minutes and produces a large intermediate tree. Both produce the same answer; only one
    /// of them is worth doing repeatedly.
    ///
    /// WHICH ATTRIBUTE, AND WHY THE OBVIOUS CHOICE IS WRONG
    /// There are two, and they point in opposite directions:
    ///   [Calls(Type, Member)]    on the CALLER, listing a callee       -> use for "who calls X"
    ///   [CalledBy(Type, Member)] on the CALLEE, listing a caller        -> looks right, is not
    /// Searching CalledBy for a method name finds nothing, because the attribute is attached to the
    /// method being called, and what it names is its callers. Verified: querying CalledBy for
    /// GetObstacleRemoveCostResource returns 0 hits, while Calls returns the 5 real callers.
    ///
    /// ACCURACY CAVEAT - DO NOT SKIP
    /// These edges are the call analyzer's INFERENCE, not a disassembly of the call instructions.
    /// The project has been burned by trusting them before (docs/decompilation.md records a method
    /// that [Calls] implied was exercised but which had zero callers in the binary). This tool
    /// therefore prints the caveat with every result. For a patch target, confirm with a real
    /// instruction scan - the `e8 rel32` sweep in docs/decompilation.md section 3.
    /// </summary>
    internal static class Program
    {
        private const string CallsAttribute = "Cpp2ILInjected.CallAnalysis.CallsAttribute";
        private const string CalledByAttribute = "Cpp2ILInjected.CallAnalysis.CalledByAttribute";
        private const string CallerCountAttribute = "Cpp2ILInjected.CallAnalysis.CallerCountAttribute";

        private static int Main(string[] args)
        {
            if (args.Length == 0 || args[0] is "-h" or "--help")
            {
                PrintUsage();
                return args.Length == 0 ? 1 : 0;
            }

            var options = CliOptions.Parse(args);
            if (options.Error != null)
            {
                Console.Error.WriteLine("error: " + options.Error);
                Console.Error.WriteLine();
                PrintUsage();
                return 1;
            }

            var dir = options.Directory;
            if (!Directory.Exists(dir))
            {
                Console.Error.WriteLine($"error: directory not found: {dir}");
                Console.Error.WriteLine();
                Console.Error.WriteLine("This directory holds the cpp2il output. Generate it with the command in");
                Console.Error.WriteLine("docs/decompilation.md (section on cpp2il); the folder should contain");
                Console.Error.WriteLine("Assembly-CSharp.dll and friends.");
                return 1;
            }

            var assemblies = Directory.GetFiles(dir, "*.dll")
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (assemblies.Count == 0)
            {
                Console.Error.WriteLine($"error: no .dll files in {dir}");
                return 1;
            }

            var attribute = options.Direction == Direction.Callers ? CallsAttribute : CalledByAttribute;

            var results = new List<Hit>();
            var warnings = new List<string>();
            var scannedMethods = 0;
            var assembliesWithGraph = 0;

            foreach (var path in assemblies)
            {
                try
                {
                    using var asm = AssemblyDefinition.ReadAssembly(
                        path, new ReaderParameters { ReadSymbols = false });

                    var foundHere = Scan(asm, attribute, options, out var methodCount, out var hasGraph);
                    scannedMethods += methodCount;
                    if (hasGraph) assembliesWithGraph++;
                    results.AddRange(foundHere);
                }
                catch (Exception ex)
                {
                    // One unreadable assembly must not abort a directory scan: the corpus includes
                    // stripped Unity modules that may not parse.
                    warnings.Add($"{Path.GetFileName(path)}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            Report(results, warnings, options, assemblies.Count, assembliesWithGraph, scannedMethods);
            return results.Count > 0 ? 0 : 2;
        }

        /// <summary>Walks every method of every type looking for the requested call edges.</summary>
        private static List<Hit> Scan(
            AssemblyDefinition asm, string attributeName, CliOptions options,
            out int methodCount, out bool hasCallGraph)
        {
            var hits = new List<Hit>();
            methodCount = 0;
            hasCallGraph = false;

            foreach (var module in asm.Modules)
            {
                IEnumerable<TypeDefinition> types;
                try { types = module.GetTypes(); }
                catch { continue; }

                foreach (var type in types)
                {
                    foreach (var method in type.Methods)
                    {
                        methodCount++;
                        foreach (var ca in method.CustomAttributes)
                        {
                            if (ca.AttributeType.FullName != attributeName) continue;
                            hasCallGraph = true;

                            var fields = ReadNamedFields(ca);
                            if (!fields.TryGetValue("Member", out var memberObj)) continue;

                            var member = memberObj as string;
                            var owner = fields.TryGetValue("Type", out var t) ? Unwrap(t) : null;

                            // `member` is the QUERIED method's name in both directions; `owner` is the
                            // DECLARING TYPE of the queried method (verified by printing the values:
                            // for GetObstacleRemoveCostResource the Calls attributes all carry
                            // owner=AreaBuildingData, which is the queried method's own type, not the
                            // caller). The annotated method itself - `type.method` - is therefore the
                            // other end: the caller under --incoming, the callee under --outgoing.
                            if (!MatchMember(member, options.MethodName)) continue;

                            // --type restricts that other end. Getting this backwards makes the filter
                            // match the queried method's own type, so `-t GameController` returns
                            // nothing for a query whose callers are all in GameController.
                            var otherEndType = options.Direction == Direction.Callers
                                ? type.FullName
                                : owner;
                            if (options.TypeName != null && !MatchType(otherEndType, options.TypeName))
                            {
                                continue;
                            }

                            hits.Add(new Hit
                            {
                                // Source is always the CALLER, Target always the CALLEE, regardless of
                                // which direction was asked for. Both attributes carry the queried
                                // method in `member`/`owner`; the annotated method is the other end:
                                //
                                //   CallsAttribute    sits on the caller  -> caller = type.method
                                //   CalledByAttribute sits on the callee  -> callee = type.method
                                //
                                // Getting this backwards yields a plausible-looking list naming the
                                // wrong method - only a run against a known case reveals it, which is
                                // how the first version of this was caught.
                                Source = options.Direction == Direction.Callers
                                    ? Describe(type.FullName, method.Name)
                                    : Describe(owner, member),
                                Target = options.Direction == Direction.Callers
                                    ? Describe(owner, member)
                                    : Describe(type.FullName, method.Name),
                                OwnerType = options.Direction == Direction.Callers
                                    ? type.FullName
                                    : owner,
                                OwnerMethod = options.Direction == Direction.Callers ? method.Name : null,
                                Assembly = Path.GetFileName(asm.MainModule.FileName),
                                Signature = options.Direction == Direction.Callers
                                    ? DescribeSignature(method)
                                    : null,
                                CallerCount = ReadCallerCount(method)
                            });
                        }
                    }
                }
            }

            return hits;
        }

        /// <summary>
        /// Reads a custom attribute's named fields into a dictionary.
        ///
        /// Named fields (not constructor arguments) are where Cpp2IL puts Type/Member: the attribute
        /// constructor takes no parameters and everything is set through named arguments. Reading
        /// ConstructorArguments instead silently yields nothing.
        /// </summary>
        private static Dictionary<string, object> ReadNamedFields(CustomAttribute ca)
        {
            var map = new Dictionary<string, object>(StringComparer.Ordinal);
            try
            {
                foreach (var f in ca.Fields) map[f.Name] = f.Argument.Value;
                foreach (var p in ca.Properties) map[p.Name] = p.Argument.Value;
            }
            catch
            {
                // Malformed blob; caller treats a missing member as "no match".
            }
            return map;
        }

        /// <summary>
        /// Cecil surfaces a named field value as CustomAttributeArgument, whose Value may itself be
        /// another argument (that is how typeof(X) is encoded). Unwrap until a stable value appears,
        /// then render it as the type name the attribute is meant to convey.
        /// </summary>
        private static string Unwrap(object value)
        {
            var guard = 0;
            while (value is CustomAttributeArgument arg && guard++ < 8)
            {
                value = arg.Value;
            }

            if (value is TypeReference tr) return tr.FullName ?? tr.Name;
            return value?.ToString();
        }

        private static int? ReadCallerCount(MethodDefinition method)
        {
            foreach (var ca in method.CustomAttributes)
            {
                if (ca.AttributeType.FullName != CallerCountAttribute) continue;
                foreach (var f in ca.Fields)
                {
                    if (f.Name == "Count" && f.Argument.Value is int n) return n;
                }
            }
            return null;
        }

        private static bool MatchMember(string member, string wanted)
        {
            if (string.IsNullOrEmpty(member) || string.IsNullOrEmpty(wanted)) return false;
            return string.Equals(member, wanted, StringComparison.Ordinal)
                || member.EndsWith("." + wanted, StringComparison.Ordinal);
        }

        private static bool MatchType(string owner, string wanted)
        {
            if (string.IsNullOrEmpty(owner)) return false;
            if (string.Equals(owner, wanted, StringComparison.Ordinal)) return true;

            // Accept a short name ("AreaBuildingData") for a full name ("Il2Cpp.AreaBuildingData"),
            // which is what callers naturally type.
            var shortOwner = owner.Contains('.') ? owner.Substring(owner.LastIndexOf('.') + 1) : owner;
            return string.Equals(shortOwner, wanted, StringComparison.OrdinalIgnoreCase);
        }

        private static string Describe(object owner, string member)
            => owner == null ? member : $"{owner}.{member}";

        private static string DescribeSignature(MethodDefinition m)
        {
            var pars = string.Join(", ", m.Parameters.Select(p => p.ParameterType.Name + " " + p.Name));
            return $"{m.ReturnType.Name} {m.Name}({pars})";
        }

        private static void Report(
            List<Hit> hits, List<string> warnings, CliOptions options,
            int assemblyCount, int assembliesWithGraph, int scannedMethods)
        {
            var label = options.Direction == Direction.Callers ? "caller" : "callee";
            var plural = hits.Count == 1 ? "" : "s";

            Console.WriteLine(
                $"{hits.Count} {label}{plural} of {options.MethodName}"
                + (options.TypeName != null ? $" (on {options.TypeName})" : "")
                + $"  [{assemblyCount} assemblies, {scannedMethods} methods]");

            if (assembliesWithGraph == 0)
            {
                Console.WriteLine();
                Console.WriteLine("No call-graph attributes were found at all. The assemblies in this");
                Console.WriteLine("directory were not produced with the call analyzer enabled.");
                Console.WriteLine("Regenerate with --use-processor attributeanalyzer,callanalyzer.");
                return;
            }

            // Group by source so a method with several edges to the same target is listed once.
            var grouped = hits
                // Group by the end the user asked about: the caller for "who calls X", the callee for
                // "what does X call". Grouping both by Source collapses all of a method's outgoing
                // edges into one line, since they share a single source.
                .GroupBy(h => options.Direction == Direction.Callers ? h.Source : h.Target,
                         StringComparer.Ordinal)
                .OrderByDescending(g => g.Max(x => x.CallerCount ?? int.MinValue))
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .ToList();

            Console.WriteLine();
            foreach (var g in grouped)
            {
                var first = g.First();
                Console.Write("  " + g.Key);

                var count = first.CallerCount;
                if (count.HasValue && options.Direction == Direction.Callers)
                {
                    Console.Write($"    [total callers of this method: {count}]");
                }
                Console.WriteLine();

                if (options.Verbose && first.Signature != null)
                {
                    Console.WriteLine("      " + first.Signature);
                }
            }

            if (warnings.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"{warnings.Count} assembly(ies) could not be read:");
                foreach (var w in warnings.Take(5)) Console.WriteLine("  " + w);
                if (warnings.Count > 5) Console.WriteLine($"  ... and {warnings.Count - 5} more");
            }

            Console.WriteLine();
            Console.WriteLine("NOTE: these edges come from the call analyzer's inference, not from disassembling");
            Console.WriteLine("the call instructions. Treat them as leads. Before relying on one as a patch");
            Console.WriteLine("target, confirm it in the disassembly (docs/decompilation.md section 3).");
        }

        private static void PrintUsage()
        {
            Console.WriteLine(@"find_callers - who calls a method (or what it calls), from cpp2il output

USAGE
  find_callers <methodName> [options]

OPTIONS
  -t, --type <name>       Restrict to a declaring type (short name works)
  -d, --dir <path>        Directory of cpp2il assemblies
                          (default: output/cpp2il_out)
  -i, --incoming          Who calls this method  (default)
  -o, --outgoing          What this method calls
  -v, --verbose           Also show full signatures
  -h, --help              This text

EXAMPLES
  find_callers GetObstacleRemoveCostResource
  find_callers GetObstacleRemoveCostResource -t AreaBuildingData
  find_callers ObstacleCanDestroy --outgoing -v

EXIT CODES
  0  found at least one edge
  1  bad arguments / unusable input
  2  ran fine, found nothing (the method may be uncalled, or the name misspelled)

WHY NOT AN MCP TOOL
  It reads static files, not the running game - see the header of Program.cs.");
        }
    }

    internal enum Direction { Callers, Callees }

    internal sealed class Hit
    {
        public string Source { get; set; }
        public string Target { get; set; }
        public string OwnerType { get; set; }
        public string OwnerMethod { get; set; }
        public string Assembly { get; set; }
        public string Signature { get; set; }
        public int? CallerCount { get; set; }
    }

    internal sealed class CliOptions
    {
        public string MethodName { get; private set; }
        public string TypeName { get; private set; }
        public string Directory { get; private set; } = "output/cpp2il_out";
        public Direction Direction { get; private set; } = Direction.Callers;
        public bool Verbose { get; private set; }
        public string Error { get; private set; }

        public static CliOptions Parse(string[] args)
        {
            var o = new CliOptions();

            for (var i = 0; i < args.Length; i++)
            {
                var a = args[i];
                switch (a)
                {
                    case "-t" or "--type":
                        if (++i >= args.Length) { o.Error = "--type needs a value"; return o; }
                        o.TypeName = args[i];
                        break;
                    case "-d" or "--dir":
                        if (++i >= args.Length) { o.Error = "--dir needs a value"; return o; }
                        o.Directory = args[i];
                        break;
                    case "-i" or "--incoming":
                        o.Direction = Direction.Callers;
                        break;
                    case "-o" or "--outgoing":
                        o.Direction = Direction.Callees;
                        break;
                    case "-v" or "--verbose":
                        o.Verbose = true;
                        break;
                    default:
                        if (a.StartsWith('-')) { o.Error = $"unknown option: {a}"; return o; }
                        if (o.MethodName != null) { o.Error = $"unexpected extra argument: {a}"; return o; }
                        o.MethodName = a;
                        break;
                }
            }

            if (string.IsNullOrWhiteSpace(o.MethodName))
                o.Error = "a method name is required";

            return o;
        }
    }
}
