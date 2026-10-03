using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MelonMCP.Server;
using Newtonsoft.Json.Linq;

namespace MelonMCP.Tools
{
    /// <summary>
    /// List all loaded assemblies
    /// </summary>
    public class ListAssembliesToolDefinition : ToolDefinitionBase
    {
        public override string Name => "list_assemblies";
        public override string Description => @"List all assemblies loaded in the game process.
This includes Unity assemblies, game assemblies, and mod assemblies.
Use the filter parameter to search for specific assemblies.";

        protected override ToolInputSchema GetInputSchema()
        {
            return new ToolInputSchema
            {
                Properties = new Dictionary<string, ToolPropertySchema>
                {
                    ["filter"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Filter assemblies by name (case-insensitive)"
                    },
                    ["includeSystem"] = new ToolPropertySchema
                    {
                        Type = "boolean",
                        Description = "Include System.* and Microsoft.* assemblies (default: false)"
                    }
                }
            };
        }

        public override CallToolResult Execute(Dictionary<string, JToken> arguments)
        {
            var filter = GetStringArg(arguments, "filter");
            var includeSystem = GetBoolArg(arguments, "includeSystem", false);

            try
            {
                var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => !a.IsDynamic)
                    .Select(a => new
                    {
                        Name = a.GetName().Name,
                        Version = a.GetName().Version?.ToString(),
                        Location = TryGetLocation(a),
                        TypeCount = TryGetTypeCount(a)
                    })
                    .Where(a =>
                    {
                        if (!includeSystem)
                        {
                            if (a.Name.StartsWith("System") || a.Name.StartsWith("Microsoft") ||
                                a.Name.StartsWith("mscorlib") || a.Name.StartsWith("netstandard"))
                                return false;
                        }

                        if (!string.IsNullOrEmpty(filter))
                        {
                            return a.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);
                        }

                        return true;
                    })
                    .OrderBy(a => a.Name)
                    .ToList();

                return JsonResult(assemblies);
            }
            catch (Exception ex)
            {
                return ErrorResult($"Failed to list assemblies: {ex.Message}");
            }
        }

        private static string TryGetLocation(Assembly asm)
        {
            try
            {
                return asm.Location;
            }
            catch
            {
                return "<in-memory>";
            }
        }

        private static int TryGetTypeCount(Assembly asm)
        {
            try
            {
                return asm.GetTypes().Length;
            }
            catch
            {
                return -1;
            }
        }
    }

    /// <summary>
    /// List types in an assembly
    /// </summary>
    public class ListTypesToolDefinition : ToolDefinitionBase
    {
        public override string Name => "list_types";
        public override string Description => @"List types (classes, structs, enums, interfaces) in an assembly.
Use filters to narrow down the results.";

        protected override ToolInputSchema GetInputSchema()
        {
            return new ToolInputSchema
            {
                Properties = new Dictionary<string, ToolPropertySchema>
                {
                    ["assemblyName"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Name of the assembly to inspect"
                    },
                    ["namespace"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Filter by namespace (partial match)"
                    },
                    ["nameFilter"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Filter by type name (partial match)"
                    },
                    ["typeKind"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Filter by type kind",
                        Enum = new List<string> { "all", "class", "struct", "enum", "interface" }
                    },
                    ["baseType"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Filter by base type (e.g., 'MonoBehaviour')"
                    },
                    ["limit"] = new ToolPropertySchema
                    {
                        Type = "integer",
                        Description = "Maximum number of types to return (default: 100)",
                        Default = 100,
                        Minimum = 1,
                        Maximum = 1000
                    }
                },
                Required = new List<string> { "assemblyName" }
            };
        }

        public override CallToolResult Execute(Dictionary<string, JToken> arguments)
        {
            var assemblyName = GetStringArg(arguments, "assemblyName");
            var namespaceFil = GetStringArg(arguments, "namespace");
            var nameFilter = GetStringArg(arguments, "nameFilter");
            var typeKind = GetStringArg(arguments, "typeKind", "all");
            var baseTypeName = GetStringArg(arguments, "baseType");
            var limit = GetIntArg(arguments, "limit", 100);

            if (string.IsNullOrEmpty(assemblyName))
            {
                return ErrorResult("assemblyName is required");
            }

            try
            {
                var assembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name.Equals(assemblyName, StringComparison.OrdinalIgnoreCase));

                if (assembly == null)
                {
                    return ErrorResult($"Assembly '{assemblyName}' not found");
                }

                Type baseType = null;
                if (!string.IsNullOrEmpty(baseTypeName))
                {
                    baseType = TypeResolver.ResolveType(baseTypeName);
                }

                var types = assembly.GetTypes()
                    .Where(t =>
                    {
                        // Namespace filter
                        if (!string.IsNullOrEmpty(namespaceFil))
                        {
                            if (t.Namespace == null || !t.Namespace.Contains(namespaceFil, StringComparison.OrdinalIgnoreCase))
                                return false;
                        }

                        // Name filter
                        if (!string.IsNullOrEmpty(nameFilter))
                        {
                            if (!t.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
                                return false;
                        }

                        // Type kind filter
                        switch (typeKind)
                        {
                            case "class":
                                if (!t.IsClass || t.IsEnum) return false;
                                break;
                            case "struct":
                                if (!t.IsValueType || t.IsEnum) return false;
                                break;
                            case "enum":
                                if (!t.IsEnum) return false;
                                break;
                            case "interface":
                                if (!t.IsInterface) return false;
                                break;
                        }

                        // Base type filter
                        if (baseType != null)
                        {
                            if (!baseType.IsAssignableFrom(t))
                                return false;
                        }

                        return true;
                    })
                    .Take(limit)
                    .Select(t => new
                    {
                        Name = t.Name,
                        FullName = t.FullName,
                        Namespace = t.Namespace,
                        Kind = GetTypeKind(t),
                        BaseType = t.BaseType?.Name,
                        IsPublic = t.IsPublic,
                        IsAbstract = t.IsAbstract,
                        IsSealed = t.IsSealed,
                        InterfaceCount = t.GetInterfaces().Length
                    })
                    .OrderBy(t => t.Namespace)
                    .ThenBy(t => t.Name)
                    .ToList();

                var result = new
                {
                    assembly = assemblyName,
                    totalMatching = types.Count,
                    types = types
                };

                return JsonResult(result);
            }
            catch (Exception ex)
            {
                return ErrorResult($"Failed to list types: {ex.Message}");
            }
        }

        private static string GetTypeKind(Type t)
        {
            if (t.IsInterface) return "interface";
            if (t.IsEnum) return "enum";
            if (t.IsValueType) return "struct";
            return "class";
        }
    }

    /// <summary>
    /// Get detailed info about a specific type
    /// </summary>
    public class GetTypeInfoToolDefinition : ToolDefinitionBase
    {
        public override string Name => "get_type_info";
        public override string Description => @"Get detailed information about a specific type including:
- Properties, fields, methods, constructors
- Base type and interfaces
- Attributes
- Static members";

        protected override ToolInputSchema GetInputSchema()
        {
            return new ToolInputSchema
            {
                Properties = new Dictionary<string, ToolPropertySchema>
                {
                    ["typeName"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Full name or simple name of the type"
                    },
                    ["includePrivate"] = new ToolPropertySchema
                    {
                        Type = "boolean",
                        Description = "Include non-public members (default: false)"
                    },
                    ["includeInherited"] = new ToolPropertySchema
                    {
                        Type = "boolean",
                        Description = "Include inherited members (default: false)"
                    }
                },
                Required = new List<string> { "typeName" }
            };
        }

        public override CallToolResult Execute(Dictionary<string, JToken> arguments)
        {
            var typeName = GetStringArg(arguments, "typeName");
            var includePrivate = GetBoolArg(arguments, "includePrivate", false);
            var includeInherited = GetBoolArg(arguments, "includeInherited", false);

            if (string.IsNullOrEmpty(typeName))
            {
                return ErrorResult("typeName is required");
            }

            try
            {
                var type = TypeResolver.ResolveType(typeName);
                if (type == null)
                {
                    return ErrorResult($"Type '{typeName}' not found");
                }

                var flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
                if (includePrivate)
                {
                    flags |= BindingFlags.NonPublic;
                }
                if (!includeInherited)
                {
                    flags |= BindingFlags.DeclaredOnly;
                }

                var result = new Dictionary<string, object>
                {
                    ["name"] = type.Name,
                    ["fullName"] = type.FullName,
                    ["namespace"] = type.Namespace,
                    ["assembly"] = type.Assembly.GetName().Name,
                    ["kind"] = GetTypeKind(type),
                    ["baseType"] = type.BaseType?.FullName,
                    ["isPublic"] = type.IsPublic,
                    ["isAbstract"] = type.IsAbstract,
                    ["isSealed"] = type.IsSealed,
                    ["isGeneric"] = type.IsGenericType,
                    ["interfaces"] = type.GetInterfaces().Select(i => i.Name).ToArray()
                };

                // Constructors
                var constructors = type.GetConstructors(flags)
                    .Select(c => new
                    {
                        Parameters = c.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}").ToArray(),
                        IsPublic = c.IsPublic
                    })
                    .ToList();
                result["constructors"] = constructors;

                // Properties
                var properties = type.GetProperties(flags)
                    .Select(p => new
                    {
                        Name = p.Name,
                        Type = p.PropertyType.Name,
                        CanRead = p.CanRead,
                        CanWrite = p.CanWrite,
                        IsStatic = p.GetMethod?.IsStatic ?? p.SetMethod?.IsStatic ?? false
                    })
                    .ToList();
                result["properties"] = properties;

                // Fields
                var fields = type.GetFields(flags)
                    .Select(f => new
                    {
                        Name = f.Name,
                        Type = f.FieldType.Name,
                        IsStatic = f.IsStatic,
                        IsReadOnly = f.IsInitOnly,
                        IsPublic = f.IsPublic
                    })
                    .ToList();
                result["fields"] = fields;

                // Methods (limit to avoid huge output)
                var methods = type.GetMethods(flags)
                    .Where(m => !m.IsSpecialName) // Skip property accessors
                    .Take(100)
                    .Select(m => new
                    {
                        Name = m.Name,
                        ReturnType = m.ReturnType.Name,
                        Parameters = m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}").ToArray(),
                        IsStatic = m.IsStatic,
                        IsPublic = m.IsPublic,
                        IsVirtual = m.IsVirtual
                    })
                    .ToList();
                result["methods"] = methods;

                // Enum values if applicable
                if (type.IsEnum)
                {
                    result["enumValues"] = Enum.GetNames(type).Zip(
                        Enum.GetValues(type).Cast<object>().Select(v => Convert.ToInt64(v)),
                        (name, value) => new { Name = name, Value = value }
                    ).ToList();
                }

                return JsonResult(result);
            }
            catch (Exception ex)
            {
                return ErrorResult($"Failed to get type info: {ex.Message}");
            }
        }

        private static string GetTypeKind(Type t)
        {
            if (t.IsInterface) return "interface";
            if (t.IsEnum) return "enum";
            if (t.IsValueType) return "struct";
            return "class";
        }
    }
}
