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
        private readonly MemoryStream _reportStream = new MemoryStream();
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

            var context = new CompilerContext(settings, new StreamReportPrinter(_diagnostics));
            _evaluator = new Evaluator(context);

            ImportLoadedAssemblies();
            ApplyDefaultUsings();
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

        /// <summary>
        /// Compiles and executes a snippet, returning the value of the final expression when there
        /// is one, otherwise any captured output.
        ///
        /// Two modes are tried in order:
        ///   * Expression mode (Evaluate) - a trailing expression yields its value, so bare
        ///     "1 + 1" and "SomeProperty" return a result.
        ///   * Statement mode (Run) - full statements, declarations, foreach and control flow.
        ///
        /// Expression mode is tried first because it is the only one that produces a value, but it
        /// rejects statements outright, so a genuine statement block falls through to Run. The
        /// previous implementation called Compile/Run/Evaluate unconditionally, which forced every
        /// submission into statement mode and made `return expr` fail with CS0127.
        /// </summary>
        public ScriptResult Run(string code)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ScriptSession));
            if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("Code is required");

            _diagnostics.GetStringBuilder().Clear();
            _reportStream.SetLength(0);

            object value = null;
            bool hasValue = false;
            string statementOutput = null;

            // 1. Expression mode.
            //
            // hasValue tracks whether we actually have a RESULT TO RETURN, not merely whether
            // Evaluate() returned without throwing. Those are different: Evaluate() happily accepts
            // a void-returning call such as `System.Console.WriteLine("x")` and yields null. Treating
            // that as "has a value" set hasValue = true with value == null, which then SKIPPED
            // statement mode below - so the snippet ran but its output was thrown away, and the caller
            // got the same bare "Execution completed (no result)" that a genuinely value-less snippet
            // produces. The two are impossible to tell apart, which is exactly what made this bug
            // expensive: it looks like "my query was wrong" and sends you editing correct code.
            //
            // Falling through on null is also what makes void statements work at all: statement mode
            // re-runs the code under _evaluator.Run(), which is where a void call's diagnostics
            // (captured output) come from.
            _diagnostics.GetStringBuilder().Clear();
            string expressionError;
            try
            {
                value = _evaluator.Evaluate(code);
                hasValue = value != null;
                expressionError = null;
            }
            catch (Exception ex)
            {
                expressionError = ex.Message;
                value = null;
            }

            // 2. Statement mode, used both when expression mode rejected the input and when it
            //    succeeded but produced no value.
            if (!hasValue)
            {
                _diagnostics.GetStringBuilder().Clear();
                try
                {
                    _evaluator.Run(code);
                    statementOutput = _diagnostics.ToString().Trim();
                }
                catch (Exception ex)
                {
                    var errors = _diagnostics.ToString().Trim();
                    var detail = string.IsNullOrWhiteSpace(errors) ? ex.Message : errors + "\n" + ex.Message;

                    if (!string.IsNullOrWhiteSpace(expressionError))
                    {
                        detail = expressionError + "\n" + detail;
                    }

                    return ScriptResult.RuntimeError(detail, ex);
                }
            }

            var text = string.IsNullOrWhiteSpace(statementOutput) ? null : statementOutput;
            return ScriptResult.Ok(hasValue ? Format(value) : null, hasValue, text);
        }

        /// <summary>Discards all session state (variables, usings, defined types).</summary>
        public void Reset()
        {
            _disposed = false;
            AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
            _referencedAssemblies.Clear();
            _diagnostics.GetStringBuilder().Clear();
            _reportStream.SetLength(0);
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
            _reportStream.Dispose();
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
                return "Execution failed:\n" + Error;
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
