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
    /// Tool for executing C# code at runtime using reflection-based evaluation
    /// </summary>
    public class ExecuteCSharpToolDefinition : ToolDefinitionBase
    {
        public override string Name => "execute_csharp";
        public override string Description => @"Execute C# code at runtime in the Unity game context.
The code has access to:
- All Unity namespaces (UnityEngine, UnityEngine.SceneManagement, etc.)
- All game assemblies and types
- MelonLoader APIs

The code should be a complete method body or expression. For complex operations, wrap in a function.
Results are returned as string output.

Examples:
- Get current scene: return UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
- Find player: var player = UnityEngine.GameObject.Find(""Player""); return player != null ? player.transform.position.ToString() : ""Not found"";
- List all cameras: return string.Join(""\n"", UnityEngine.Object.FindObjectsOfType<UnityEngine.Camera>().Select(c => c.name));";

        protected override ToolInputSchema GetInputSchema()
        {
            return new ToolInputSchema
            {
                Properties = new Dictionary<string, ToolPropertySchema>
                {
                    ["code"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "C# code to execute. Should return a value or perform an action."
                    },
                    ["timeout"] = new ToolPropertySchema
                    {
                        Type = "integer",
                        Description = "Execution timeout in milliseconds (default: 5000, max: 30000)",
                        Default = 5000,
                        Minimum = 100,
                        Maximum = 30000
                    }
                },
                Required = new List<string> { "code" }
            };
        }

        public override CallToolResult Execute(Dictionary<string, JToken> arguments)
        {
            var code = GetStringArg(arguments, "code");
            var timeout = GetIntArg(arguments, "timeout", 5000);

            if (string.IsNullOrWhiteSpace(code))
            {
                return ErrorResult("Code parameter is required");
            }

            try
            {
                // Use the CSharpEvaluator to execute code
                var result = CSharpEvaluator.Execute(code, timeout);
                return TextResult(result ?? "Execution completed (null result)");
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
        public override string Description => @"Evaluate a simple C# expression and return the result.
This is for quick evaluations like property access, method calls, or calculations.

Examples:
- ""UnityEngine.Time.time""
- ""UnityEngine.Application.productName""
- ""UnityEngine.QualitySettings.GetQualityLevel()""
- ""System.DateTime.Now.ToString()""";

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
                var result = CSharpEvaluator.EvaluateExpression(expression);
                return TextResult(result?.ToString() ?? "null");
            }
            catch (Exception ex)
            {
                return ErrorResult($"Evaluation failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Simple C# evaluator using reflection
    /// Does not require Mono.CSharp - works with any .NET runtime
    /// Supports multi-statement code, variable declarations, and generic methods
    /// </summary>
    public static class CSharpEvaluator
    {
        private static readonly Dictionary<string, Type> _typeCache = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        private static readonly StringBuilder _output = new StringBuilder();
        private static readonly object _lock = new object();

        // Common type aliases
        private static readonly Dictionary<string, string> TypeAliases = new Dictionary<string, string>
        {
            ["string"] = "System.String",
            ["int"] = "System.Int32",
            ["long"] = "System.Int64",
            ["float"] = "System.Single",
            ["double"] = "System.Double",
            ["bool"] = "System.Boolean",
            ["object"] = "System.Object",
            ["void"] = "System.Void",
            ["byte"] = "System.Byte",
            ["sbyte"] = "System.SByte",
            ["short"] = "System.Int16",
            ["ushort"] = "System.UInt16",
            ["uint"] = "System.UInt32",
            ["ulong"] = "System.UInt64",
            ["char"] = "System.Char",
            ["decimal"] = "System.Decimal"
        };

        // Regex patterns for parsing
        private static readonly Regex GenericMethodPattern = new Regex(@"^(\w+)<(.+)>\s*\((.*)\)$", RegexOptions.Compiled);
        private static readonly Regex VariableDeclarationPattern = new Regex(@"^\s*(var|(?:[\w\.]+(?:<[\w\.,\s]+>)?(?:\[\])?(?:\?)?))?\s+(\w+)\s*=\s*(.+)$", RegexOptions.Compiled);

        static CSharpEvaluator()
        {
            // Pre-cache common Unity types
            CacheAssemblyTypes();
        }

        private static void CacheAssemblyTypes()
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        if (asm.IsDynamic) continue;

                        foreach (var type in asm.GetExportedTypes())
                        {
                            _typeCache[type.FullName] = type;

                            // Also cache by simple name for common namespaces
                            if (type.Namespace != null && (
                                type.Namespace.StartsWith("UnityEngine") ||
                                type.Namespace.StartsWith("System") ||
                                type.Namespace.StartsWith("Il2Cpp")))
                            {
                                if (!_typeCache.ContainsKey(type.Name))
                                {
                                    _typeCache[type.Name] = type;
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                MelonMCPPlugin.Logger?.Warning($"Failed to cache assembly types: {ex.Message}");
            }
        }

        public static void RefreshTypeCache()
        {
            _typeCache.Clear();
            CacheAssemblyTypes();
        }

        public static Type ResolveType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;

            typeName = typeName.Trim();

            // Check aliases first
            if (TypeAliases.TryGetValue(typeName, out var aliasedType))
            {
                typeName = aliasedType;
            }

            // Check cache
            if (_typeCache.TryGetValue(typeName, out var cachedType))
            {
                return cachedType;
            }

            // Try to find in loaded assemblies
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (asm.IsDynamic) continue;
                    var type = asm.GetType(typeName, false);
                    if (type != null)
                    {
                        _typeCache[typeName] = type;
                        return type;
                    }
                }
                catch { }
            }

            // Try common namespaces
            string[] commonNamespaces = {
                "UnityEngine",
                "UnityEngine.SceneManagement",
                "UnityEngine.UI",
                "UnityEngine.Events",
                "System",
                "System.Linq",
                "System.Collections.Generic",
                "Il2Cpp",
                "Il2CppSystem"
            };
            foreach (var ns in commonNamespaces)
            {
                var fullName = $"{ns}.{typeName}";
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        if (asm.IsDynamic) continue;
                        var type = asm.GetType(fullName, false);
                        if (type != null)
                        {
                            _typeCache[typeName] = type;
                            return type;
                        }
                    }
                    catch { }
                }
            }

            return null;
        }

        public static object EvaluateExpression(string expression)
        {
            expression = expression.Trim();

            // Handle assignment expressions like "UnityEngine.Application.runInBackground = true"
            var assignmentIndex = FindAssignmentOperator(expression);
            if (assignmentIndex > 0)
            {
                var leftSide = expression.Substring(0, assignmentIndex).Trim();
                var rightSide = expression.Substring(assignmentIndex + 1).Trim();

                // Skip if left side looks like a comparison (== or !=)
                if (!expression.Substring(assignmentIndex - 1, 2).Contains("!") &&
                    !expression.Substring(assignmentIndex, 2).StartsWith("="))
                {
                    return EvaluateAssignment(leftSide, rightSide);
                }
            }

            // Handle simple property/method chains like "UnityEngine.Time.time"
            var parts = ParseExpression(expression);
            if (parts.Count == 0)
            {
                throw new ArgumentException("Empty expression");
            }

            return EvaluateExpressionParts(parts);
        }

        private static int FindAssignmentOperator(string expression)
        {
            int depth = 0;
            bool inString = false;
            char stringChar = '\0';

            for (int i = 0; i < expression.Length; i++)
            {
                char c = expression[i];

                if (!inString && (c == '"' || c == '\''))
                {
                    inString = true;
                    stringChar = c;
                }
                else if (inString && c == stringChar && (i == 0 || expression[i - 1] != '\\'))
                {
                    inString = false;
                }
                else if (!inString)
                {
                    if (c == '(' || c == '[' || c == '<' || c == '{') depth++;
                    else if (c == ')' || c == ']' || c == '>' || c == '}') depth--;
                    else if (depth == 0 && c == '=' && i > 0 && i < expression.Length - 1)
                    {
                        // Check it's not == or != or <= or >=
                        char prev = expression[i - 1];
                        char next = expression[i + 1];
                        if (prev != '=' && prev != '!' && prev != '<' && prev != '>' && next != '=')
                        {
                            return i;
                        }
                    }
                }
            }
            return -1;
        }

        private static object EvaluateAssignment(string target, string valueExpr)
        {
            // Parse the value
            var value = ParseLiteralOrExpression(valueExpr);

            // Parse target to get the object/type and property name
            var parts = ParseExpression(target);
            if (parts.Count < 2)
            {
                throw new ArgumentException("Invalid assignment target");
            }

            // The last part is the property to set
            var propertyName = parts[parts.Count - 1];
            var targetParts = parts.Take(parts.Count - 1).ToList();

            // Resolve the target type/object
            object targetObj = null;
            Type targetType = null;

            for (int typeEndIndex = targetParts.Count - 1; typeEndIndex >= 0; typeEndIndex--)
            {
                var typeName = string.Join(".", targetParts.Take(typeEndIndex + 1));
                targetType = ResolveType(typeName);

                if (targetType != null)
                {
                    // If there are remaining parts, evaluate them to get the target object
                    for (int i = typeEndIndex + 1; i < targetParts.Count; i++)
                    {
                        var part = targetParts[i];
                        var result = EvaluateMember(targetObj, targetType, part);
                        targetObj = result.Value;
                        targetType = result.Type ?? targetObj?.GetType();
                    }
                    break;
                }
            }

            if (targetType == null)
            {
                throw new ArgumentException($"Could not resolve target type: {string.Join(".", targetParts)}");
            }

            // Set the property
            var flags = BindingFlags.Public | BindingFlags.NonPublic | (targetObj == null ? BindingFlags.Static : BindingFlags.Instance);

            var prop = targetType.GetProperty(propertyName, flags);
            if (prop != null && prop.CanWrite)
            {
                var convertedValue = ConvertArgument(value, prop.PropertyType);
                prop.SetValue(targetObj, convertedValue);
                return $"Set {target} = {convertedValue}";
            }

            var field = targetType.GetField(propertyName, flags);
            if (field != null && !field.IsInitOnly)
            {
                var convertedValue = ConvertArgument(value, field.FieldType);
                field.SetValue(targetObj, convertedValue);
                return $"Set {target} = {convertedValue}";
            }

            throw new MemberAccessException($"Property '{propertyName}' not found or not writable on type '{targetType.FullName}'");
        }

        private static List<string> ParseExpression(string expression)
        {
            var parts = new List<string>();
            var current = new StringBuilder();
            int parenDepth = 0;
            int angleDepth = 0;
            int bracketDepth = 0;
            bool inString = false;
            char stringChar = '\0';

            for (int i = 0; i < expression.Length; i++)
            {
                char c = expression[i];

                if (!inString && (c == '"' || c == '\''))
                {
                    inString = true;
                    stringChar = c;
                    current.Append(c);
                }
                else if (inString && c == stringChar && (i == 0 || expression[i - 1] != '\\'))
                {
                    inString = false;
                    current.Append(c);
                }
                else if (inString)
                {
                    current.Append(c);
                }
                else
                {
                    if (c == '(') parenDepth++;
                    else if (c == ')') parenDepth--;
                    else if (c == '<') angleDepth++;
                    else if (c == '>') angleDepth--;
                    else if (c == '[') bracketDepth++;
                    else if (c == ']') bracketDepth--;

                    if (c == '.' && parenDepth == 0 && angleDepth == 0 && bracketDepth == 0)
                    {
                        if (current.Length > 0)
                        {
                            parts.Add(current.ToString());
                            current.Clear();
                        }
                    }
                    else
                    {
                        current.Append(c);
                    }
                }
            }

            if (current.Length > 0)
            {
                parts.Add(current.ToString());
            }

            return parts;
        }

        private static object EvaluateExpressionParts(List<string> parts)
        {
            object currentObj = null;
            Type currentType = null;

            // Try to resolve the first part(s) as a type
            for (int typeEndIndex = parts.Count - 1; typeEndIndex >= 0; typeEndIndex--)
            {
                var typeName = string.Join(".", parts.Take(typeEndIndex + 1));
                currentType = ResolveType(typeName);

                if (currentType != null)
                {
                    // Found a type, now evaluate remaining parts
                    for (int i = typeEndIndex + 1; i < parts.Count; i++)
                    {
                        var part = parts[i];
                        var result = EvaluateMember(currentObj, currentType, part);
                        currentObj = result.Value;
                        currentType = result.Type ?? currentObj?.GetType();
                    }
                    return currentObj;
                }
            }

            throw new ArgumentException($"Could not resolve type from expression: {string.Join(".", parts)}");
        }

        private static (object Value, Type Type) EvaluateMember(object obj, Type type, string memberExpression)
        {
            // Check for generic method call like GetComponent<MeshFilter>()
            var genericMatch = GenericMethodPattern.Match(memberExpression);
            if (genericMatch.Success)
            {
                var methodName = genericMatch.Groups[1].Value;
                var typeArgs = genericMatch.Groups[2].Value;
                var argsStr = genericMatch.Groups[3].Value;
                return InvokeGenericMethod(obj, type, methodName, typeArgs, argsStr);
            }

            // Check if it's a method call
            int parenIndex = memberExpression.IndexOf('(');
            if (parenIndex > 0)
            {
                var methodName = memberExpression.Substring(0, parenIndex);
                var argsStr = memberExpression.Substring(parenIndex + 1, memberExpression.Length - parenIndex - 2);
                var args = ParseArguments(argsStr);

                return InvokeMethod(obj, type, methodName, args);
            }

            // Check if it's an indexer
            int bracketIndex = memberExpression.IndexOf('[');
            if (bracketIndex > 0)
            {
                var propName = memberExpression.Substring(0, bracketIndex);
                var indexStr = memberExpression.Substring(bracketIndex + 1, memberExpression.Length - bracketIndex - 2);

                // Get the property first, then apply indexer
                var propResult = GetProperty(obj, type, propName);
                return ApplyIndexer(propResult.Value, propResult.Type, indexStr);
            }

            // It's a property or field
            return GetProperty(obj, type, memberExpression);
        }

        private static (object Value, Type Type) InvokeGenericMethod(object obj, Type type, string methodName, string typeArgsStr, string argsStr)
        {
            // Parse generic type arguments
            var typeArgNames = ParseTypeArguments(typeArgsStr);
            var typeArgs = new List<Type>();

            foreach (var typeArgName in typeArgNames)
            {
                var typeArg = ResolveType(typeArgName.Trim());
                if (typeArg == null)
                {
                    throw new ArgumentException($"Could not resolve generic type argument: {typeArgName}");
                }
                typeArgs.Add(typeArg);
            }

            // Parse method arguments
            var args = ParseArguments(argsStr);

            var flags = BindingFlags.Public | BindingFlags.NonPublic | (obj == null ? BindingFlags.Static : BindingFlags.Instance);

            // Find matching generic method
            var methods = type.GetMethods(flags)
                .Where(m => m.Name == methodName && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == typeArgs.Count)
                .ToList();

            foreach (var genericMethod in methods)
            {
                try
                {
                    var method = genericMethod.MakeGenericMethod(typeArgs.ToArray());
                    var parameters = method.GetParameters();

                    if (parameters.Length == args.Length || parameters.All(p => p.HasDefaultValue || args.Length >= Array.IndexOf(parameters, p) + 1))
                    {
                        var convertedArgs = new object[parameters.Length];
                        bool match = true;

                        for (int i = 0; i < parameters.Length && match; i++)
                        {
                            if (i < args.Length)
                            {
                                try
                                {
                                    convertedArgs[i] = ConvertArgument(args[i], parameters[i].ParameterType);
                                }
                                catch
                                {
                                    match = false;
                                }
                            }
                            else if (parameters[i].HasDefaultValue)
                            {
                                convertedArgs[i] = parameters[i].DefaultValue;
                            }
                            else
                            {
                                match = false;
                            }
                        }

                        if (match)
                        {
                            var result = method.Invoke(obj, convertedArgs);
                            return (result, method.ReturnType);
                        }
                    }
                }
                catch { }
            }

            throw new MemberAccessException($"Generic method '{methodName}<{typeArgsStr}>' not found or arguments don't match on type '{type.FullName}'");
        }

        private static List<string> ParseTypeArguments(string typeArgsStr)
        {
            var result = new List<string>();
            var current = new StringBuilder();
            int depth = 0;

            foreach (char c in typeArgsStr)
            {
                if (c == '<') depth++;
                else if (c == '>') depth--;

                if (c == ',' && depth == 0)
                {
                    result.Add(current.ToString().Trim());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }

            if (current.Length > 0)
            {
                result.Add(current.ToString().Trim());
            }

            return result;
        }

        private static (object Value, Type Type) GetProperty(object obj, Type type, string name)
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic | (obj == null ? BindingFlags.Static : BindingFlags.Instance);

            // Try property
            var prop = type.GetProperty(name, flags);
            if (prop != null)
            {
                var value = prop.GetValue(obj);
                return (value, prop.PropertyType);
            }

            // Try field
            var field = type.GetField(name, flags);
            if (field != null)
            {
                var value = field.GetValue(obj);
                return (value, field.FieldType);
            }

            throw new MemberAccessException($"Member '{name}' not found on type '{type.FullName}'");
        }

        private static (object Value, Type Type) InvokeMethod(object obj, Type type, string methodName, object[] args)
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic | (obj == null ? BindingFlags.Static : BindingFlags.Instance);

            // Find matching method
            var methods = type.GetMethods(flags).Where(m => m.Name == methodName && !m.IsGenericMethodDefinition).ToList();

            foreach (var method in methods)
            {
                var parameters = method.GetParameters();
                if (parameters.Length == args.Length || (parameters.Length > 0 && parameters.Last().IsDefined(typeof(ParamArrayAttribute))))
                {
                    try
                    {
                        // Try to convert arguments
                        var convertedArgs = new object[parameters.Length];
                        bool match = true;

                        for (int i = 0; i < parameters.Length && match; i++)
                        {
                            if (i < args.Length)
                            {
                                convertedArgs[i] = ConvertArgument(args[i], parameters[i].ParameterType);
                            }
                            else if (parameters[i].HasDefaultValue)
                            {
                                convertedArgs[i] = parameters[i].DefaultValue;
                            }
                            else
                            {
                                match = false;
                            }
                        }

                        if (match)
                        {
                            var result = method.Invoke(obj, convertedArgs);
                            return (result, method.ReturnType);
                        }
                    }
                    catch { }
                }
            }

            // Try with no arguments if we couldn't match
            if (args.Length == 0)
            {
                var method = methods.FirstOrDefault(m => m.GetParameters().Length == 0);
                if (method != null)
                {
                    var result = method.Invoke(obj, null);
                    return (result, method.ReturnType);
                }
            }

            throw new MemberAccessException($"Method '{methodName}' not found or arguments don't match on type '{type.FullName}'");
        }

        private static (object Value, Type Type) ApplyIndexer(object obj, Type type, string indexStr)
        {
            if (obj == null) throw new NullReferenceException("Cannot apply indexer to null");

            var index = ParseLiteralOrExpression(indexStr.Trim());

            // Try array indexer
            if (obj is Array array)
            {
                var idx = Convert.ToInt32(index);
                var value = array.GetValue(idx);
                return (value, type.GetElementType());
            }

            // Try IL2CPP array
            var il2cppArrayType = obj.GetType();
            if (il2cppArrayType.FullName?.Contains("Il2CppInterop") == true ||
                il2cppArrayType.FullName?.Contains("Il2CppReferenceArray") == true ||
                il2cppArrayType.FullName?.Contains("Il2CppStructArray") == true)
            {
                var getMethod = il2cppArrayType.GetMethod("get_Item") ?? il2cppArrayType.GetProperty("Item")?.GetGetMethod();
                if (getMethod != null)
                {
                    var idx = Convert.ToInt32(index);
                    var value = getMethod.Invoke(obj, new object[] { idx });
                    return (value, getMethod.ReturnType);
                }
            }

            // Try indexer property
            var indexer = type.GetProperty("Item");
            if (indexer != null)
            {
                var indexParams = indexer.GetIndexParameters();
                if (indexParams.Length > 0)
                {
                    var convertedIndex = ConvertArgument(index, indexParams[0].ParameterType);
                    var value = indexer.GetValue(obj, new[] { convertedIndex });
                    return (value, indexer.PropertyType);
                }
            }

            // Try direct indexer method
            var itemGetter = type.GetMethod("get_Item");
            if (itemGetter != null)
            {
                var parameters = itemGetter.GetParameters();
                if (parameters.Length > 0)
                {
                    var convertedIndex = ConvertArgument(index, parameters[0].ParameterType);
                    var value = itemGetter.Invoke(obj, new[] { convertedIndex });
                    return (value, itemGetter.ReturnType);
                }
            }

            throw new InvalidOperationException($"Type '{type.FullName}' does not have an indexer");
        }

        private static object[] ParseArguments(string argsStr)
        {
            if (string.IsNullOrWhiteSpace(argsStr)) return Array.Empty<object>();

            var args = new List<object>();
            var current = new StringBuilder();
            int depth = 0;
            bool inString = false;
            char stringChar = '\0';

            for (int i = 0; i < argsStr.Length; i++)
            {
                char c = argsStr[i];

                if (!inString && (c == '"' || c == '\''))
                {
                    inString = true;
                    stringChar = c;
                    current.Append(c);
                }
                else if (inString && c == stringChar && (i == 0 || argsStr[i - 1] != '\\'))
                {
                    inString = false;
                    current.Append(c);
                }
                else if (!inString && (c == '(' || c == '[' || c == '<' || c == '{'))
                {
                    depth++;
                    current.Append(c);
                }
                else if (!inString && (c == ')' || c == ']' || c == '>' || c == '}'))
                {
                    depth--;
                    current.Append(c);
                }
                else if (!inString && c == ',' && depth == 0)
                {
                    args.Add(ParseLiteralOrExpression(current.ToString().Trim()));
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }

            if (current.Length > 0)
            {
                args.Add(ParseLiteralOrExpression(current.ToString().Trim()));
            }

            return args.ToArray();
        }

        private static object ParseLiteralOrExpression(string str)
        {
            if (string.IsNullOrEmpty(str)) return null;

            str = str.Trim();

            // String literals
            if ((str.StartsWith("\"") && str.EndsWith("\"")) || (str.StartsWith("'") && str.EndsWith("'")))
            {
                return UnescapeString(str.Substring(1, str.Length - 2));
            }

            // Boolean
            if (str.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (str.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;

            // Null
            if (str.Equals("null", StringComparison.OrdinalIgnoreCase)) return null;

            // Numbers
            if (int.TryParse(str, out int intVal)) return intVal;
            if (long.TryParse(str, out long longVal)) return longVal;
            if (str.EndsWith("f", StringComparison.OrdinalIgnoreCase))
            {
                if (float.TryParse(str.Substring(0, str.Length - 1), out float floatVal)) return floatVal;
            }
            if (str.EndsWith("d", StringComparison.OrdinalIgnoreCase))
            {
                if (double.TryParse(str.Substring(0, str.Length - 1), out double dVal)) return dVal;
            }
            if (str.EndsWith("m", StringComparison.OrdinalIgnoreCase))
            {
                if (decimal.TryParse(str.Substring(0, str.Length - 1), out decimal decVal)) return decVal;
            }
            if (double.TryParse(str, out double doubleVal)) return doubleVal;

            // typeof expression
            if (str.StartsWith("typeof(") && str.EndsWith(")"))
            {
                var typeName = str.Substring(7, str.Length - 8);
                var type = ResolveType(typeName);
                if (type != null) return type;
            }

            // Could be an expression - try to evaluate it
            try
            {
                return EvaluateExpression(str);
            }
            catch
            {
                return str; // Return as string if we can't parse it
            }
        }

        private static string UnescapeString(string str)
        {
            return str
                .Replace("\\n", "\n")
                .Replace("\\r", "\r")
                .Replace("\\t", "\t")
                .Replace("\\\\", "\\")
                .Replace("\\\"", "\"")
                .Replace("\\'", "'");
        }

        private static object ConvertArgument(object arg, Type targetType)
        {
            if (arg == null)
            {
                return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;
            }

            var argType = arg.GetType();
            if (targetType.IsAssignableFrom(argType)) return arg;

            // Handle Type arguments
            if (targetType == typeof(Type) && arg is Type) return arg;

            // Handle common conversions
            if (targetType == typeof(string)) return arg.ToString();
            if (targetType == typeof(int)) return Convert.ToInt32(arg);
            if (targetType == typeof(long)) return Convert.ToInt64(arg);
            if (targetType == typeof(float)) return Convert.ToSingle(arg);
            if (targetType == typeof(double)) return Convert.ToDouble(arg);
            if (targetType == typeof(bool)) return Convert.ToBoolean(arg);
            if (targetType == typeof(byte)) return Convert.ToByte(arg);
            if (targetType == typeof(short)) return Convert.ToInt16(arg);
            if (targetType == typeof(char) && arg is string s && s.Length == 1) return s[0];

            // Handle enums
            if (targetType.IsEnum)
            {
                if (arg is string enumStr)
                {
                    return Enum.Parse(targetType, enumStr, true);
                }
                return Enum.ToObject(targetType, arg);
            }

            // Handle IL2CPP types
            if (targetType.FullName?.StartsWith("Il2Cpp") == true)
            {
                // Check if there's an implicit conversion
                var implicitOp = targetType.GetMethod("op_Implicit", new[] { argType });
                if (implicitOp != null)
                {
                    return implicitOp.Invoke(null, new[] { arg });
                }
            }

            return Convert.ChangeType(arg, targetType);
        }

        public static string Execute(string code, int timeoutMs = 5000)
        {
            lock (_lock)
            {
                _output.Clear();

                try
                {
                    code = code.Trim();

                    // Handle multi-statement code
                    if (code.Contains(";") && (code.Contains("\n") || code.Contains("var ") || code.Contains("return ")))
                    {
                        return ExecuteMultiStatement(code);
                    }

                    // For simple return statements, extract and evaluate
                    if (code.StartsWith("return ") && code.EndsWith(";"))
                    {
                        var expression = code.Substring(7, code.Length - 8).Trim();
                        var result = EvaluateExpression(expression);
                        return FormatResult(result);
                    }

                    // Try to evaluate as expression
                    var evalResult = EvaluateExpression(code.TrimEnd(';'));
                    return FormatResult(evalResult);
                }
                catch (Exception ex)
                {
                    throw new Exception($"Execution error: {ex.Message}", ex);
                }
            }
        }

        private static string ExecuteMultiStatement(string code)
        {
            // Simple multi-statement executor with variable support
            var variables = new Dictionary<string, object>();
            var statements = SplitStatements(code);
            object lastResult = null;

            foreach (var statement in statements)
            {
                var stmt = statement.Trim();
                if (string.IsNullOrEmpty(stmt)) continue;

                // Handle return statements
                if (stmt.StartsWith("return "))
                {
                    var expr = stmt.Substring(7).TrimEnd(';').Trim();
                    lastResult = EvaluateWithVariables(expr, variables);
                    return FormatResult(lastResult);
                }

                // Handle variable declarations
                var varMatch = VariableDeclarationPattern.Match(stmt.TrimEnd(';'));
                if (varMatch.Success)
                {
                    var varName = varMatch.Groups[2].Value;
                    var valueExpr = varMatch.Groups[3].Value;
                    var value = EvaluateWithVariables(valueExpr, variables);
                    variables[varName] = value;
                    lastResult = value;
                    continue;
                }

                // Handle simple expression/assignment
                lastResult = EvaluateWithVariables(stmt.TrimEnd(';'), variables);
            }

            return FormatResult(lastResult);
        }

        private static List<string> SplitStatements(string code)
        {
            var statements = new List<string>();
            var current = new StringBuilder();
            int depth = 0;
            bool inString = false;
            char stringChar = '\0';

            foreach (char c in code)
            {
                if (!inString && (c == '"' || c == '\''))
                {
                    inString = true;
                    stringChar = c;
                    current.Append(c);
                }
                else if (inString && c == stringChar)
                {
                    inString = false;
                    current.Append(c);
                }
                else if (inString)
                {
                    current.Append(c);
                }
                else if (c == '{' || c == '(' || c == '[')
                {
                    depth++;
                    current.Append(c);
                }
                else if (c == '}' || c == ')' || c == ']')
                {
                    depth--;
                    current.Append(c);
                }
                else if (c == ';' && depth == 0)
                {
                    statements.Add(current.ToString().Trim());
                    current.Clear();
                }
                else if (c == '\n' || c == '\r')
                {
                    current.Append(' ');
                }
                else
                {
                    current.Append(c);
                }
            }

            if (current.Length > 0)
            {
                statements.Add(current.ToString().Trim());
            }

            return statements;
        }

        private static object EvaluateWithVariables(string expression, Dictionary<string, object> variables)
        {
            expression = expression.Trim();

            // Check if expression starts with a variable name
            foreach (var kvp in variables)
            {
                if (expression == kvp.Key)
                {
                    return kvp.Value;
                }

                // Variable followed by member access
                if (expression.StartsWith(kvp.Key + ".") || expression.StartsWith(kvp.Key + "["))
                {
                    var remainder = expression.Substring(kvp.Key.Length);
                    var obj = kvp.Value;
                    if (obj == null) return null;

                    var parts = ParseExpression(remainder.TrimStart('.'));
                    var currentType = obj.GetType();

                    foreach (var part in parts)
                    {
                        if (string.IsNullOrEmpty(part)) continue;
                        var result = EvaluateMember(obj, currentType, part);
                        obj = result.Value;
                        currentType = result.Type ?? obj?.GetType();
                    }
                    return obj;
                }
            }

            // Regular expression evaluation
            return EvaluateExpression(expression);
        }

        private static string FormatResult(object result)
        {
            if (result == null) return "null";

            var type = result.GetType();

            // Handle collections
            if (result is System.Collections.IEnumerable enumerable && !(result is string))
            {
                var items = new List<string>();
                int count = 0;
                foreach (var item in enumerable)
                {
                    items.Add(FormatResult(item));
                    count++;
                    if (count >= 100)
                    {
                        items.Add($"... (truncated, showing first 100 of more items)");
                        break;
                    }
                }
                return $"[{string.Join(", ", items)}]";
            }

            return result.ToString();
        }
    }
}
