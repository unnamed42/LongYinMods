using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using MelonMCP.Server;
using Newtonsoft.Json.Linq;

namespace MelonMCP.Tools
{
    /// <summary>
    /// Tool for executing C# code at runtime in the game process.
    ///
    /// Backed by Mono.CSharp (embedded into MelonMCP.dll), so full C# is available: operators,
    /// string concatenation, foreach, object construction, generics, lambdas and LINQ.
    public class ExecuteCSharpToolDefinition : ToolDefinitionBase
    {
        public override string Name => "execute_csharp";
        public override string Description => @"Execute C# in-process; all Unity, game and MelonLoader types are available.

C# 7.0/7.1 in full (tuples, out-var, pattern matching, numeric separators). Of 7.2 only
'private protected' works - local functions and in parameters are not parsed. Nothing newer: switch
expressions and using declarations (C# 8) fail, as do when clauses and LINQ query syntax (use
x.Where(...).Select(...)). switch type patterns (case int i:) hit an internal compiler error, use
if (x is int i).

State persists across calls; a trailing expression is returned. Runs on the Unity main thread with a
small stack and cannot be interrupted: avoid MakeGenericMethod, deep reflection, infinite loops.";

        /// <summary>
        /// Session shared by all script tools. State (variables, usings, defined types) persists
        /// between calls, which is what an interactive console is expected to do.
        /// </summary>
        internal static readonly ScriptSession Session = new ScriptSession();

        protected override ToolInputSchema GetInputSchema()
        {
            return new ToolInputSchema
            {
                Properties = new Dictionary<string, ToolPropertySchema>
                {
                    ["code"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "C# code to execute. A trailing expression is returned as the result."
                    },
                    ["timeout"] = new ToolPropertySchema
                    {
                        Type = "integer",
                        Description = "Execution timeout in milliseconds (default: 5000, max: 30000)",
                        Default = 5000,
                        Minimum = 100,
                        Maximum = 30000
                    },
                    ["reset"] = new ToolPropertySchema
                    {
                        Type = "boolean",
                        Description = "Clear all session state (variables, usings, defined types) before running.",
                        Default = false
                    }
                },
                Required = new List<string> { "code" }
            };
        }

        public override CallToolResult Execute(Dictionary<string, JToken> arguments)
        {
            var code = GetStringArg(arguments, "code");
            var timeout = GetIntArg(arguments, "timeout", 5000);
            var reset = GetBoolArg(arguments, "reset", false);

            if (string.IsNullOrWhiteSpace(code))
            {
                return ErrorResult("Code parameter is required");
            }

            if (reset)
            {
                Session.Reset();
            }

            try
            {
                var result = Session.Run(code);
                return result.Success
                    ? TextResult(result.ToDisplayString())
                    : ErrorResult(result.ToDisplayString());
            }
            catch (Exception ex)
            {
                return ErrorResult($"Execution failed: {ex.Message}\n{ex.StackTrace}");
            }
        }
    }

    /// <summary>
    /// Tool for evaluating simple C# expressions
    /// </summary>
    public class EvaluateExpressionToolDefinition : ToolDefinitionBase
    {
        public override string Name => "evaluate_expression";
        public override string Description => @"Evaluate one C# expression and return its value. Same engine
and limits as execute_csharp, but for a single stateless read - use execute_csharp when you need
variables or several statements.";

        protected override ToolInputSchema GetInputSchema()
        {
            return new ToolInputSchema
            {
                Properties = new Dictionary<string, ToolPropertySchema>
                {
                    ["expression"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "C# expression to evaluate"
                    }
                },
                Required = new List<string> { "expression" }
            };
        }

        public override CallToolResult Execute(Dictionary<string, JToken> arguments)
        {
            var expression = GetStringArg(arguments, "expression");

            if (string.IsNullOrWhiteSpace(expression))
            {
                return ErrorResult("Expression parameter is required");
            }

            try
            {
                var result = ExecuteCSharpToolDefinition.Session.Run(expression);
                return result.Success
                    ? TextResult(result.ToDisplayString())
                    : ErrorResult(result.ToDisplayString());
            }
            catch (Exception ex)
            {
                return ErrorResult($"Evaluation failed: {ex.Message}");
            }
        }
    }
}
