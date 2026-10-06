using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Mono.CSharp;

namespace MelonMCP.Tools
{
    /// <summary>
    /// Session that compiles and runs C# snippets against the game's loaded assemblies.
    ///
    /// Backed by Mono.CSharp, which is embedded directly into MelonMCP.dll by ILRepack (see
    /// MelonMCP.csproj). That gives real C# semantics - operators, string concatenation,
    /// foreach, object construction, generics, LINQ and lambdas - none of which the previous
    /// hand-written reflection evaluator supported despite advertising them.
    ///
    /// State (variables, usings, defined types) persists across calls within one script session,
    /// matching an interactive REPL, and can be reset explicitly.
    /// </summary>
    public sealed class ScriptSession : IDisposable
    {
        private readonly StringWriter _diagnostics = new StringWriter();
        private readonly CapturingReportPrinter _printer;
        private Evaluator _evaluator;
        private bool _disposed;

        /// <summary>Assemblies already offered to the compiler, so we do not re-reference them.</summary>
        private readonly HashSet<string> _referencedAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Namespaces imported for every snippet, so common types need no qualification.</summary>
        private static readonly string[] DefaultUsings =
        {
            "System",
            "System.Collections",
            "System.Collections.Generic",
            "System.Linq",
            "System.Text",
            "UnityEngine",
            "UnityEngine.SceneManagement",
        };

        public ScriptSession()
        {
            _printer = new CapturingReportPrinter(_diagnostics);
            Initialize();
        }

        private void Initialize()
        {
            var settings = new CompilerSettings
            {
                Version = LanguageVersion.Experimental,
                GenerateDebugInfo = false,
                StdLib = true,
                Target = Target.Library,
                WarningLevel = 1,
                EnhancedWarnings = false,
                Unsafe = true,
            };

            var context = new CompilerContext(settings, _printer);
            _evaluator = new Evaluator(context);

            ImportLoadedAssemblies();
            ApplyDefaultUsings();
            WarmUpExtensionMethods();
        }

        /// <summary>
        /// Forces extension methods to resolve once during initialisation.
        ///
        /// WHY THIS IS NEEDED (measured, reproducible): after a session is created or reset, the
        /// FIRST snippet calling an extension method - <c>x.Count()</c>, <c>x.Where(...)</c> -
        /// returns no value, and only the SECOND one works. Non-extension calls are unaffected:
        /// <c>x.Length</c> works on the first try. Deterministic across repeated trials, and not a
        /// caller-side caching artefact.
        ///
        /// The likely mechanism is that extension-method lookup needs one compilation pass before the
        /// imported extension types become visible, and the failing snippet's own compilation is what
        /// would have supplied it. Running a throwaway snippet here pays that cost up front, so the
        /// caller's first LINQ query behaves like its second.
        ///
        /// This works around a Mono.CSharp behaviour rather than fixing it: the extension methods are
        /// imported correctly by ReferenceAssembly, which calls ImportTypes with
        /// importExtensionTypes: true internally. If a future mcs build resolves extensions on the
        /// first pass this becomes dead weight and can be deleted.
        ///
        /// Failures are swallowed by design - this is an optimisation, and a session that cannot warm
        /// up still works; the caller merely pays the cost on their first LINQ call.
        /// </summary>
        private void WarmUpExtensionMethods()
        {
            try
            {
                _evaluator.Evaluate("(new int[]{1}).Count()");
            }
            catch
            {
                // Ignored by design; see the summary.
            }
        }

        /// <summary>
        /// References every assembly already loaded in the game process. This is what makes game
        /// types (Il2Cpp.*, Assembly-CSharp) resolvable from a snippet without any manual wiring.
        /// </summary>
        private void ImportLoadedAssemblies()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                TryReferenceAssembly(assembly);
            }

            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
        }

        private void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            // Game and mod assemblies keep loading after startup; pick them up as they appear.
            try
            {
                TryReferenceAssembly(args.LoadedAssembly);
            }
            catch
            {
                // A single unloadable assembly must not break the session.
            }
        }

        private void TryReferenceAssembly(Assembly assembly)
        {
            if (assembly == null || assembly.IsDynamic) return;

            string name;
            try
            {
                name = assembly.GetName().Name;
            }
            catch
            {
                return;
            }

            if (string.IsNullOrEmpty(name)) return;
            if (!_referencedAssemblies.Add(name)) return;

            try
            {
                _evaluator.ReferenceAssembly(assembly);
            }
            catch
            {
                // Assemblies built against incompatible runtimes can fail to reference; skip them
                // rather than aborting the whole import. Remove from the set so a later successful
                // load of the same name can retry.
                _referencedAssemblies.Remove(name);
            }
        }

        /// <summary>
        /// Imports the default namespaces.
        ///
        /// On extension methods and LINQ: importing the namespace is necessary but NOT sufficient -
        /// the DEFINING ASSEMBLY must also be referenced. Extension methods are discovered through
        /// assembly imports, not through using directives. Verified against this Mono.CSharp build:
        /// ReflectionImporter.ImportAssembly internally calls
        /// ImportTypes(types, ns, importExtensionTypes: true), so ReferenceAssembly already imports
        /// them and no extra work is needed.
        ///
        /// Two consequences worth knowing before touching ImportLoadedAssemblies:
        ///
        /// 1. Do NOT additionally call ImportTypes(importExtensionTypes: true, ...) for an assembly
        ///    ReferenceAssembly already handled. It registers every extension method a second time,
        ///    and every LINQ call then fails with CS0121 listing the SAME signature twice - which
        ///    reads like a compiler bug and is not. Referencing System.Core alongside System.Linq
        ///    does the same thing, because both expose System.Linq.Enumerable (netstandard too);
        ///    only the defining assembly may be imported.
        ///
        /// 2. Extension syntax failing while a static call to the same method works means the
        ///    assembly was never imported - not that a using directive is missing. Here the failure
        ///    is silent (the snippet reports no value instead of raising CS1061), so it is easily
        ///    misread as "my query returned nothing".
        /// </summary>
        private void ApplyDefaultUsings()
        {
            foreach (var ns in DefaultUsings)
            {
                try
                {
                    _evaluator.Run("using " + ns + ";");
                }
                catch
                {
                    // A namespace that does not exist in this process is simply not imported.
                }
            }
        }

        /// <summary>Sentinel separating "the snippet produced no value" from "it produced null".</summary>
        private static readonly object NoValue = new object();

        /// <summary>
        /// Upper bound on how many pieces one snippet may be split into. A run of consecutive
        /// declarations is consumed by a single parse, so only a snippet that alternates declarations
        /// and statements many times comes anywhere near this.
        /// </summary>
        private const int MaxSubmissions = 64;

        /// <summary>
        /// Compiles and executes a snippet, returning the value of the final expression when there is
        /// one, otherwise any captured diagnostics.
        ///
        /// A snippet may contain several SUBMISSIONS, the way an interactive console accepts them one
        /// after another: a run of declarations, then statements, then more declarations. That is not
        /// a nicety - Mono.CSharp decides how to parse the whole input from its FIRST token (see
        /// RunAsSubmissions), so without this a snippet that defines a class and then uses it is
        /// rejected with CS1525 and forces the caller into two round trips.
        /// </summary>
        public ScriptResult Run(string code)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ScriptSession));
            if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("Code is required");

            var submission = RunOneSubmission(code);
            if (submission.Status == SubmissionStatus.Ok
                || submission.Status == SubmissionStatus.RuntimeFailed)
            {
                return submission.ToResult();
            }

            // It did not compile as one submission. It may simply be several of them.
            return RunAsSubmissions(code, submission);
        }

        /// <summary>
        /// Compiles <paramref name="text"/> as ONE submission and, if it compiles, runs it exactly once.
        ///
        /// Compile success is decided by <c>compiled != null</c> and never by the return value of
        /// Evaluator.Evaluate/Evaluator.Run. Those cannot report a parse failure at all: Evaluate
        /// returns null both when the snippet parsed and produced no value AND when it did not parse,
        /// and Run returns true in both cases. Treating them as a success signal is what made every
        /// compile error come back as a SUCCESSFUL tool result, with the compiler's message sitting in
        /// the output field where nothing would look for it.
        /// </summary>
        private Submission RunOneSubmission(string text)
        {
            _diagnostics.GetStringBuilder().Clear();
            _printer.Clear();

            string partial = _evaluator.Compile(text, out CompiledMethod compiled);

            // Errors - not a null CompiledMethod - decide failure. The compiler returns no delegate
            // at all for a submission that only declares types, because a compilation unit has no
            // method to invoke, so compiled == null happens on success too. What separates a
            // declaration that took effect from one that did not is whether anything was reported.
            if (_printer.HasError)
            {
                return new Submission
                {
                    Status = SubmissionStatus.CompileFailed,
                    Diagnostics = _diagnostics.ToString().Trim(),
                    ErrorLocation = _printer.FirstErrorLocation,
                };
            }

            if (partial != null)
            {
                return new Submission
                {
                    Status = SubmissionStatus.PartialInput,
                    Diagnostics = _diagnostics.ToString().Trim(),
                };
            }

            // Declarations only: they took effect, there is simply nothing to execute.
            if (compiled == null)
            {
                return new Submission
                {
                    Status = SubmissionStatus.Ok,
                    Diagnostics = _diagnostics.ToString().Trim(),
                };
            }

            object retvalue = NoValue;
            try
            {
                // Calling the delegate the compiler just handed us is the same call
                // Evaluator.Evaluate makes internally. Doing it here rather than through Evaluate
                // means one compile and one execution per text; the previous Evaluate-then-Run
                // implementation compiled twice and therefore EXECUTED TWICE whenever the snippet
                // produced no value, so a void statement's side effects happened twice.
                compiled(ref retvalue);
            }
            catch (Exception ex)
            {
                return new Submission
                {
                    Status = SubmissionStatus.RuntimeFailed,
                    Diagnostics = _diagnostics.ToString().Trim(),
                    Exception = ex,
                };
            }

            return new Submission
            {
                Status = SubmissionStatus.Ok,
                Value = retvalue,
                HasValue = !ReferenceEquals(retvalue, NoValue),
                Diagnostics = _diagnostics.ToString().Trim(),
            };
        }

        /// <summary>
        /// Retries a snippet that failed to compile as one submission by splitting it where the
        /// compiler itself says the current submission ended.
        ///
        /// WHY A BOUNDARY EXISTS AT ALL: Mono.CSharp picks the parse mode from the FIRST token
        /// (Evaluator.ToplevelOrStatement). A declaration keyword first - class, struct, enum,
        /// interface, namespace, or "using X" - makes it parse a COMPILATION UNIT, which accepts
        /// declarations and nothing else, so the parser stops at the first statement and reports
        /// CS1525 at it. A statement first makes it parse STATEMENTS, which rejects a following
        /// declaration. Either way the reported location IS the split point, so no C# parser of our
        /// own is needed.
        ///
        /// The split validates itself, because a piece that does not compile aborts the whole
        /// attempt: if the compiler's boundary does not yield a compiling prefix, the original
        /// whole-input error is reported instead. A wrong guess can therefore only fail to help,
        /// never quietly change what the snippet means.
        ///
        /// Consumed text is blanked out of a copy of the input rather than cut from it, so every
        /// later piece still compiles with the ORIGINAL line numbers and its diagnostics point at the
        /// line the caller actually wrote.
        /// </summary>
        private ScriptResult RunAsSubmissions(string code, Submission wholeInputFailure)
        {
            var work = code.ToCharArray();
            var pieces = new List<Submission>();
            var failure = wholeInputFailure;
            int consumed = 0;

            while (true)
            {
                if (failure.Status == SubmissionStatus.RuntimeFailed) return failure.ToResult();

                if (failure.Status != SubmissionStatus.CompileFailed || pieces.Count >= MaxSubmissions)
                {
                    return GiveUp(failure, wholeInputFailure);
                }

                int boundary = OffsetOf(failure.ErrorLocation, work);
                if (boundary <= consumed || boundary >= code.Length)
                {
                    return GiveUp(failure, wholeInputFailure);
                }

                var head = RunOneSubmission(new string(work, 0, boundary));
                if (head.Status == SubmissionStatus.RuntimeFailed) return head.ToResult();
                if (head.Status != SubmissionStatus.Ok) return GiveUp(failure, wholeInputFailure);

                pieces.Add(head);

                Blank(work, consumed, boundary);
                consumed = boundary;

                failure = RunOneSubmission(new string(work));
                if (failure.Status == SubmissionStatus.Ok)
                {
                    pieces.Add(failure);
                    break;
                }
            }

            var output = new List<string>();
            foreach (var piece in pieces)
            {
                if (!string.IsNullOrWhiteSpace(piece.Diagnostics)) output.Add(piece.Diagnostics);
            }

            // Only the LAST piece's value is the snippet's value, matching "a trailing expression is
            // returned": an earlier piece that happened to produce one is a step, not the answer.
            var last = pieces[pieces.Count - 1];
            return ScriptResult.Ok(
                last.HasValue ? Format(last.Value) : null,
                last.HasValue,
                output.Count == 0 ? null : string.Join("\n", output));
        }

        /// <summary>
        /// Converts a compiler Location into a character offset in <paramref name="text"/>.
        ///
        /// Column is 1-based, except that 0 means "the start of this line" - which is the shape a
        /// boundary error takes when the trailing statement begins on a new line. Location packs the
        /// column into 8 bits, so a line longer than 255 characters reports a wrapped column; the
        /// caller's "the prefix must compile" check is what makes that harmless.
        /// </summary>
        private static int OffsetOf(Location location, char[] text)
        {
            if (location.IsNull) return -1;

            int offset = 0;
            for (int line = 1; line < location.Row; line++)
            {
                while (offset < text.Length && text[offset] != '\n') offset++;
                if (offset >= text.Length) return -1;
                offset++;
            }

            if (location.Column > 0) offset += location.Column - 1;
            return offset <= text.Length ? offset : -1;
        }

        /// <summary>Replaces consumed text with spaces, keeping every line break in place.</summary>
        private static void Blank(char[] text, int from, int to)
        {
            for (int i = from; i < to; i++)
            {
                if (text[i] != '\n' && text[i] != '\r') text[i] = ' ';
            }
        }

        /// <summary>
        /// Reports a snippet that could not be run either as a whole or as a sequence of submissions.
        /// The most specific diagnostics available win: the nested failure once the split reached the
        /// remainder, otherwise the original whole-input error.
        /// </summary>
        private static ScriptResult GiveUp(Submission failure, Submission wholeInputFailure)
        {
            var detail = string.IsNullOrWhiteSpace(failure.Diagnostics)
                ? wholeInputFailure.Diagnostics
                : failure.Diagnostics;

            if (failure.Status == SubmissionStatus.PartialInput)
            {
                detail = string.IsNullOrWhiteSpace(detail)
                    ? "Incomplete input"
                    : "Incomplete input:\n" + detail;
            }

            return ScriptResult.CompileError(string.IsNullOrWhiteSpace(detail)
                ? "The snippet could not be compiled."
                : detail);
        }

        /// <summary>How one compiled submission ended.</summary>
        private enum SubmissionStatus
        {
            Ok,
            PartialInput,    // incomplete text: the compiler wants more of it
            CompileFailed,   // syntax or semantic errors; nothing ran
            RuntimeFailed,   // it compiled, but executing it threw
        }

        /// <summary>Outcome of compiling (and possibly running) one submission.</summary>
        private sealed class Submission
        {
            public SubmissionStatus Status;
            public object Value;
            public bool HasValue;
            public string Diagnostics;
            public Location ErrorLocation;
            public Exception Exception;

            public ScriptResult ToResult()
            {
                if (Status == SubmissionStatus.RuntimeFailed)
                {
                    var detail = string.IsNullOrWhiteSpace(Diagnostics)
                        ? Exception.Message
                        : Diagnostics + "\n" + Exception.Message;
                    return ScriptResult.RuntimeError(detail, Exception);
                }

                if (Status != SubmissionStatus.Ok)
                {
                    return ScriptResult.CompileError(Diagnostics);
                }

                return ScriptResult.Ok(
                    HasValue ? Format(Value) : null,
                    HasValue,
                    string.IsNullOrWhiteSpace(Diagnostics) ? null : Diagnostics);
            }
        }

        /// <summary>
        /// A ReportPrinter that writes diagnostics like StreamReportPrinter (which it replaces) and
        /// additionally remembers WHERE the first error was. That location is what lets Run split a
        /// multi-submission snippet at the exact point the parser stopped.
        ///
        /// ReportPrinter.Reset() is not virtual, so Clear() has to be called before every compile.
        /// ErrorsCount is deliberately not used as the failure signal - a null CompiledMethod is,
        /// because that covers semantic errors as well as syntax errors.
        /// </summary>
        private sealed class CapturingReportPrinter : ReportPrinter
        {
            private readonly TextWriter _writer;

            public CapturingReportPrinter(TextWriter writer)
            {
                _writer = writer;
            }

            /// <summary>Location of the first error reported since the last <see cref="Clear"/>.</summary>
            public Location FirstErrorLocation { get; private set; }

            /// <summary>
            /// Whether the last compile reported any error. This - not a null CompiledMethod - is the
            /// failure signal: the compiler returns NO delegate for a submission that only declares
            /// types, so compiled == null happens on success as well.
            /// </summary>
            public bool HasError => ErrorsCount > 0;

            public void Clear()
            {
                Reset();
                FirstErrorLocation = Location.Null;
            }

            public override void Print(AbstractMessage msg, bool showFullPath)
            {
                if (!msg.IsWarning && FirstErrorLocation.IsNull && !msg.Location.IsNull)
                {
                    FirstErrorLocation = msg.Location;
                }

                Print(msg, _writer, showFullPath);
                base.Print(msg, showFullPath);
            }
        }

        /// <summary>Discards all session state (variables, usings, defined types).</summary>
        public void Reset()
        {
            _disposed = false;
            AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
            _referencedAssemblies.Clear();
            _diagnostics.GetStringBuilder().Clear();
            _printer.Clear();
            Initialize();
        }

        /// <summary>
        /// Renders a value for text output. Enumerables are expanded (bounded) because the most
        /// common snippet result is a collection of game objects.
        /// </summary>
        public static string Format(object value)
        {
            if (value == null) return "null";
            if (value is string s) return s;
            if (value is bool b) return b ? "true" : "false";

            if (value is System.Collections.IEnumerable enumerable)
            {
                var parts = new List<string>();
                int count = 0;
                foreach (var item in enumerable)
                {
                    if (count++ >= 100)
                    {
                        parts.Add("... (" + count + "+ items, truncated)");
                        break;
                    }
                    parts.Add(item == null ? "null" : item.ToString());
                }
                return "[" + string.Join(", ", parts) + "]";
            }

            return value.ToString();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;

            // Mono.CSharp's Evaluator has no Dispose on this version; dropping the reference is
            // enough for it to be collected along with the compiler context it owns.
            _evaluator = null;
            _diagnostics.Dispose();
        }
    }

    /// <summary>Outcome of one snippet execution.</summary>
    public sealed class ScriptResult
    {
        public bool Success { get; private set; }
        public string Value { get; private set; }
        public bool HasValue { get; private set; }
        public string Output { get; private set; }
        public string Error { get; private set; }
        public Exception Exception { get; private set; }

        public static ScriptResult Ok(string value, bool hasValue, string output) => new ScriptResult
        {
            Success = true,
            Value = value,
            HasValue = hasValue,
            Output = output,
        };

        public static ScriptResult CompileError(string error) => new ScriptResult
        {
            Success = false,
            Error = error,
        };

        public static ScriptResult RuntimeError(string error, Exception ex) => new ScriptResult
        {
            Success = false,
            Error = error,
            Exception = ex,
        };

        /// <summary>Single-line summary suitable for returning straight to an MCP client.</summary>
        public string ToDisplayString()
        {
            if (!Success)
            {
                // "Compilation failed" and "Execution failed" must not share a message: the first
                // means nothing ran (the fix is a syntax or type error), the second means the
                // snippet ran and threw. CompileError carries no Exception, which separates them.
                return (Exception == null ? "Compilation failed:\n" : "Execution failed:\n") + Error;
            }

            var parts = new List<string>();

            if (HasValue)
            {
                parts.Add(Value);
            }

            if (!string.IsNullOrWhiteSpace(Output))
            {
                parts.Add(Output);
            }

            if (parts.Count == 0)
            {
                // "Ran fine, produced nothing" and "something went wrong" must never share a message.
                // A successful-but-empty result is a legitimate answer (a void call, a declaration with
                // no trailing expression) and the caller should move on; before this branch existed,
                // the wording was identical to a failure and cost real time chasing correct code.
                return "Executed successfully; no value returned. If you expected a result, end the "
                     + "snippet with a bare trailing expression (not 'return x') — declarations and "
                     + "void calls produce no value on their own.";
            }

            return string.Join("\n", parts);
        }
    }
}
