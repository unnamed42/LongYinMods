using System;
using System.Collections.Generic;
using MelonMCP.Server;
using Newtonsoft.Json.Linq;

namespace MelonMCP.Tools
{
    /// <summary>
    /// Get information about the current scene
    /// </summary>
    public class GetSceneInfoToolDefinition : ToolDefinitionBase
    {
        public override string Name => "get_scene_info";
        public override string Description => "Get information about the currently loaded Unity scene including name, build index, and root object count.";

        protected override ToolInputSchema GetInputSchema()
        {
            return new ToolInputSchema
            {
                Properties = new Dictionary<string, ToolPropertySchema>()
            };
        }

        public override CallToolResult Execute(Dictionary<string, JToken> arguments)
        {
            try
            {
                var json = UnityHelper.GetGameInfoJson();
                return TextResult(json);
            }
            catch (Exception ex)
            {
                return ErrorResult($"Failed to get scene info: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// List GameObjects in the scene
    /// </summary>
    public class ListGameObjectsToolDefinition : ToolDefinitionBase
    {
        public override string Name => "list_game_objects";
        public override string Description => @"List GameObjects in the current scene hierarchy.
Returns a tree structure of GameObjects with their components, active state, and children.
Use depth parameter to control how deep to traverse (default: 2).";

        protected override ToolInputSchema GetInputSchema()
        {
            return new ToolInputSchema
            {
                Properties = new Dictionary<string, ToolPropertySchema>
                {
                    ["depth"] = new ToolPropertySchema
                    {
                        Type = "integer",
                        Description = "Maximum depth to traverse (default: 2, max: 5)",
                        Default = 2,
                        Minimum = 1,
                        Maximum = 5
                    },
                    ["filter"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Optional name filter - only returns objects containing this text"
                    },
                    ["activeOnly"] = new ToolPropertySchema
                    {
                        Type = "boolean",
                        Description = "If true, only return active GameObjects (default: false)"
                    }
                }
            };
        }

        public override CallToolResult Execute(Dictionary<string, JToken> arguments)
        {
            var depth = GetIntArg(arguments, "depth", 2);
            var filter = GetStringArg(arguments, "filter");
            var activeOnly = GetBoolArg(arguments, "activeOnly", false);

            depth = Math.Max(1, Math.Min(5, depth));

            try
            {
                var hierarchy = new List<object>();
                var rootObjects = UnityHelper.GetRootGameObjects();

                foreach (var go in rootObjects)
                {
                    var name = UnityHelper.GetProperty(go, "name")?.ToString() ?? "";
                    var isActive = (bool)(UnityHelper.GetProperty(go, "activeInHierarchy") ?? true);

                    // Apply filters
                    if (activeOnly && !isActive) continue;
                    if (!string.IsNullOrEmpty(filter) && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;

                    hierarchy.Add(UnityHelper.GetGameObjectHierarchy(go, 0, depth));
                }

                return JsonResult(hierarchy);
            }
            catch (Exception ex)
            {
                return ErrorResult($"Failed to list game objects: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Find a specific GameObject
    /// </summary>
    public class FindGameObjectToolDefinition : ToolDefinitionBase
    {
        public override string Name => "find_game_object";
        public override string Description => @"Find a GameObject by path, name, or instance ID.
Paths use '/' as separator (e.g., 'Canvas/Panel/Button').
Returns detailed information about the found object including all components.";

        protected override ToolInputSchema GetInputSchema()
        {
            return new ToolInputSchema
            {
                Properties = new Dictionary<string, ToolPropertySchema>
                {
                    ["path"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Path to the GameObject (e.g., 'Canvas/Panel/Button')"
                    },
                    ["instanceId"] = new ToolPropertySchema
                    {
                        Type = "integer",
                        Description = "Instance ID of the GameObject (alternative to path)"
                    },
                    ["includeChildren"] = new ToolPropertySchema
                    {
                        Type = "boolean",
                        Description = "Include child objects in result (default: true)"
                    }
                }
            };
        }

        public override CallToolResult Execute(Dictionary<string, JToken> arguments)
        {
            var path = GetStringArg(arguments, "path");
            var instanceId = GetIntArg(arguments, "instanceId", 0);
            var includeChildren = GetBoolArg(arguments, "includeChildren", true);

            try
            {
                object gameObject = null;

                if (!string.IsNullOrEmpty(path))
                {
                    gameObject = UnityHelper.FindGameObject(path);
                }
                else if (instanceId != 0)
                {
                    gameObject = UnityHelper.FindObjectByInstanceId(instanceId);
                }
                else
                {
                    return ErrorResult("Either 'path' or 'instanceId' must be provided");
                }

                if (gameObject == null)
                {
                    return TextResult($"GameObject not found: {path ?? instanceId.ToString()}");
                }

                var depth = includeChildren ? 3 : 0;
                var result = UnityHelper.GetGameObjectHierarchy(gameObject, 0, depth);

                return JsonResult(result);
            }
            catch (Exception ex)
            {
                return ErrorResult($"Failed to find GameObject: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// List components on a GameObject
    /// </summary>
    public class ListComponentsToolDefinition : ToolDefinitionBase
    {
        public override string Name => "list_components";
        public override string Description => @"List all components attached to a GameObject.
Returns component type names, enabled state (for Behaviours), and instance IDs.";

        protected override ToolInputSchema GetInputSchema()
        {
            return new ToolInputSchema
            {
                Properties = new Dictionary<string, ToolPropertySchema>
                {
                    ["path"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Path to the GameObject"
                    },
                    ["instanceId"] = new ToolPropertySchema
                    {
                        Type = "integer",
                        Description = "Instance ID of the GameObject (alternative to path)"
                    }
                }
            };
        }

        public override CallToolResult Execute(Dictionary<string, JToken> arguments)
        {
            var path = GetStringArg(arguments, "path");
            var instanceId = GetIntArg(arguments, "instanceId", 0);

            try
            {
                object gameObject = null;

                if (!string.IsNullOrEmpty(path))
                {
                    gameObject = UnityHelper.FindGameObject(path);
                }
                else if (instanceId != 0)
                {
                    gameObject = UnityHelper.FindObjectByInstanceId(instanceId);
                }
                else
                {
                    return ErrorResult("Either 'path' or 'instanceId' must be provided");
                }

                if (gameObject == null)
                {
                    return TextResult($"GameObject not found");
                }

                var components = UnityHelper.GetComponents(gameObject);
                var result = new List<Dictionary<string, object>>();

                foreach (var comp in components)
                {
                    var compInfo = new Dictionary<string, object>
                    {
                        ["type"] = comp.GetType().FullName,
                        ["typeName"] = comp.GetType().Name,
                        ["instanceId"] = UnityHelper.InvokeMethod(comp, "GetInstanceID")
                    };

                    // Check if it's a Behaviour and get enabled state
                    var enabled = UnityHelper.GetProperty(comp, "enabled");
                    if (enabled != null)
                    {
                        compInfo["enabled"] = enabled;
                    }

                    result.Add(compInfo);
                }

                return JsonResult(result);
            }
            catch (Exception ex)
            {
                return ErrorResult($"Failed to list components: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Inspect a component in detail
    /// </summary>
    public class InspectComponentToolDefinition : ToolDefinitionBase
    {
        public override string Name => "inspect_component";
        public override string Description => @"Inspect a component's properties, fields, and methods.
Returns all public members and their values. Use includePrivate to also show private members.";

        protected override ToolInputSchema GetInputSchema()
        {
            return new ToolInputSchema
            {
                Properties = new Dictionary<string, ToolPropertySchema>
                {
                    ["gameObjectPath"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Path to the GameObject containing the component"
                    },
                    ["gameObjectId"] = new ToolPropertySchema
                    {
                        Type = "integer",
                        Description = "Instance ID of the GameObject (alternative to path)"
                    },
                    ["componentType"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Name or full name of the component type to inspect"
                    },
                    ["componentIndex"] = new ToolPropertySchema
                    {
                        Type = "integer",
                        Description = "Index of the component if multiple of same type exist (default: 0)"
                    },
                    ["includePrivate"] = new ToolPropertySchema
                    {
                        Type = "boolean",
                        Description = "Include private members (default: false)"
                    }
                },
                Required = new List<string> { "componentType" }
            };
        }

        public override CallToolResult Execute(Dictionary<string, JToken> arguments)
        {
            var path = GetStringArg(arguments, "gameObjectPath");
            var instanceId = GetIntArg(arguments, "gameObjectId", 0);
            var componentType = GetStringArg(arguments, "componentType");
            var componentIndex = GetIntArg(arguments, "componentIndex", 0);
            var includePrivate = GetBoolArg(arguments, "includePrivate", false);

            if (string.IsNullOrEmpty(componentType))
            {
                return ErrorResult("componentType is required");
            }

            try
            {
                object gameObject = null;

                if (!string.IsNullOrEmpty(path))
                {
                    gameObject = UnityHelper.FindGameObject(path);
                }
                else if (instanceId != 0)
                {
                    gameObject = UnityHelper.FindObjectByInstanceId(instanceId);
                }
                else
                {
                    return ErrorResult("Either 'gameObjectPath' or 'gameObjectId' must be provided");
                }

                if (gameObject == null)
                {
                    return TextResult("GameObject not found");
                }

                var components = UnityHelper.GetComponents(gameObject);
                object targetComponent = null;
                int matchIndex = 0;

                foreach (var comp in components)
                {
                    var typeName = comp.GetType().Name;
                    var fullName = comp.GetType().FullName;

                    if (typeName.Equals(componentType, StringComparison.OrdinalIgnoreCase) ||
                        fullName.Equals(componentType, StringComparison.OrdinalIgnoreCase))
                    {
                        if (matchIndex == componentIndex)
                        {
                            targetComponent = comp;
                            break;
                        }
                        matchIndex++;
                    }
                }

                if (targetComponent == null)
                {
                    return TextResult($"Component '{componentType}' not found on GameObject");
                }

                var result = UnityHelper.InspectObject(targetComponent, includePrivate);
                return JsonResult(result);
            }
            catch (Exception ex)
            {
                return ErrorResult($"Failed to inspect component: {ex.Message}");
            }
        }
    }
}
