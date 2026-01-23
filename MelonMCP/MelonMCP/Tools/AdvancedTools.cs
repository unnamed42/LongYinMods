using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MelonMCP.Server;
using Newtonsoft.Json.Linq;

namespace MelonMCP.Tools
{
    /// <summary>
    /// Find all objects of a specific type in the scene
    /// </summary>
    public class FindObjectsOfTypeToolDefinition : ToolDefinitionBase
    {
        public override string Name => "find_objects_of_type";
        public override string Description => @"Find all Unity objects of a specific type in the scene.
Useful for finding all instances of a component type like 'Camera', 'Light', 'AudioSource', etc.
Can also find custom game types like 'Enemy', 'PlayerController', etc.";

        protected override ToolInputSchema GetInputSchema()
        {
            return new ToolInputSchema
            {
                Properties = new Dictionary<string, ToolPropertySchema>
                {
                    ["typeName"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "The type name to search for (e.g., 'Camera', 'Light', 'MonoBehaviour')"
                    },
                    ["includeInactive"] = new ToolPropertySchema
                    {
                        Type = "boolean",
                        Description = "Include inactive objects (default: false)"
                    },
                    ["limit"] = new ToolPropertySchema
                    {
                        Type = "integer",
                        Description = "Maximum number of results (default: 50)",
                        Default = 50,
                        Minimum = 1,
                        Maximum = 500
                    }
                },
                Required = new List<string> { "typeName" }
            };
        }

        public override CallToolResult Execute(Dictionary<string, JToken> arguments)
        {
            var typeName = GetStringArg(arguments, "typeName");
            var includeInactive = GetBoolArg(arguments, "includeInactive", false);
            var limit = GetIntArg(arguments, "limit", 50);

            if (string.IsNullOrEmpty(typeName))
            {
                return ErrorResult("typeName is required");
            }

            try
            {
                // Resolve the type
                var type = CSharpEvaluator.ResolveType(typeName);
                if (type == null)
                {
                    // Try with UnityEngine prefix
                    type = CSharpEvaluator.ResolveType("UnityEngine." + typeName);
                }

                if (type == null)
                {
                    return ErrorResult($"Type '{typeName}' not found. Try the full type name.");
                }

                var objects = UnityHelper.FindObjectsOfType(type);
                var results = new List<Dictionary<string, object>>();

                foreach (var obj in objects.Take(limit))
                {
                    if (obj == null) continue;

                    var info = new Dictionary<string, object>
                    {
                        ["type"] = obj.GetType().Name,
                        ["instanceId"] = UnityHelper.InvokeMethod(obj, "GetInstanceID"),
                        ["name"] = UnityHelper.GetProperty(obj, "name")
                    };

                    // If it's a component, get the GameObject info
                    var gameObject = UnityHelper.GetProperty(obj, "gameObject");
                    if (gameObject != null)
                    {
                        info["gameObjectName"] = UnityHelper.GetProperty(gameObject, "name");
                        info["gameObjectActive"] = UnityHelper.GetProperty(gameObject, "activeInHierarchy");

                        // Get the path
                        var transform = UnityHelper.GetProperty(gameObject, "transform");
                        if (transform != null)
                        {
                            info["path"] = GetTransformPath(transform);
                        }
                    }

                    // Check enabled state if it's a Behaviour
                    var enabled = UnityHelper.GetProperty(obj, "enabled");
                    if (enabled != null)
                    {
                        info["enabled"] = enabled;
                    }

                    results.Add(info);
                }

                return JsonResult(new
                {
                    typeName = type.FullName,
                    count = results.Count,
                    totalFound = objects.Length,
                    objects = results
                });
            }
            catch (Exception ex)
            {
                return ErrorResult($"Failed to find objects: {ex.Message}");
            }
        }

        private static string GetTransformPath(object transform)
        {
            var path = new List<string>();
            var current = transform;

            while (current != null)
            {
                var name = UnityHelper.GetProperty(current, "name")?.ToString() ?? "";
                path.Insert(0, name);
                current = UnityHelper.GetProperty(current, "parent");
            }

            return string.Join("/", path);
        }
    }

    /// <summary>
    /// Set the Unity time scale
    /// </summary>
    public class SetTimeScaleToolDefinition : ToolDefinitionBase
    {
        public override string Name => "set_time_scale";
        public override string Description => @"Set the Unity time scale.
0 = paused, 1 = normal speed, 0.5 = half speed, 2 = double speed.
This affects physics and time-based game logic.";

        protected override ToolInputSchema GetInputSchema()
        {
            return new ToolInputSchema
            {
                Properties = new Dictionary<string, ToolPropertySchema>
                {
                    ["timeScale"] = new ToolPropertySchema
                    {
                        Type = "number",
                        Description = "Time scale value (0 = pause, 1 = normal)"
                    }
                },
                Required = new List<string> { "timeScale" }
            };
        }

        public override CallToolResult Execute(Dictionary<string, JToken> arguments)
        {
            var timeScale = (float)GetDoubleArg(arguments, "timeScale", 1.0);

            try
            {
                var timeType = CSharpEvaluator.ResolveType("UnityEngine.Time");
                if (timeType == null)
                {
                    return ErrorResult("Could not find UnityEngine.Time type");
                }

                var prop = timeType.GetProperty("timeScale", BindingFlags.Public | BindingFlags.Static);
                if (prop == null)
                {
                    return ErrorResult("Could not find Time.timeScale property");
                }

                prop.SetValue(null, timeScale);
                return TextResult($"Time scale set to {timeScale}");
            }
            catch (Exception ex)
            {
                return ErrorResult($"Failed to set time scale: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Get/set cursor state
    /// </summary>
    public class CursorControlToolDefinition : ToolDefinitionBase
    {
        public override string Name => "cursor_control";
        public override string Description => @"Control the mouse cursor visibility and lock state.
Useful for freeing the cursor to interact with debug UIs.";

        protected override ToolInputSchema GetInputSchema()
        {
            return new ToolInputSchema
            {
                Properties = new Dictionary<string, ToolPropertySchema>
                {
                    ["visible"] = new ToolPropertySchema
                    {
                        Type = "boolean",
                        Description = "Set cursor visibility"
                    },
                    ["lockState"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Set cursor lock state",
                        Enum = new List<string> { "None", "Locked", "Confined" }
                    }
                }
            };
        }

        public override CallToolResult Execute(Dictionary<string, JToken> arguments)
        {
            var hasVisible = arguments.ContainsKey("visible");
            var visible = GetBoolArg(arguments, "visible", true);
            var lockState = GetStringArg(arguments, "lockState");

            try
            {
                var cursorType = CSharpEvaluator.ResolveType("UnityEngine.Cursor");
                if (cursorType == null)
                {
                    return ErrorResult("Could not find UnityEngine.Cursor type");
                }

                var results = new List<string>();

                if (hasVisible)
                {
                    var visibleProp = cursorType.GetProperty("visible", BindingFlags.Public | BindingFlags.Static);
                    if (visibleProp != null)
                    {
                        visibleProp.SetValue(null, visible);
                        results.Add($"Cursor visible: {visible}");
                    }
                }

                if (!string.IsNullOrEmpty(lockState))
                {
                    var lockStateProp = cursorType.GetProperty("lockState", BindingFlags.Public | BindingFlags.Static);
                    var lockStateType = CSharpEvaluator.ResolveType("UnityEngine.CursorLockMode");

                    if (lockStateProp != null && lockStateType != null)
                    {
                        var enumValue = Enum.Parse(lockStateType, lockState);
                        lockStateProp.SetValue(null, enumValue);
                        results.Add($"Cursor lock state: {lockState}");
                    }
                }

                if (results.Count == 0)
                {
                    // Just return current state
                    var visibleProp = cursorType.GetProperty("visible", BindingFlags.Public | BindingFlags.Static);
                    var lockStateProp = cursorType.GetProperty("lockState", BindingFlags.Public | BindingFlags.Static);

                    return JsonResult(new
                    {
                        visible = visibleProp?.GetValue(null),
                        lockState = lockStateProp?.GetValue(null)?.ToString()
                    });
                }

                return TextResult(string.Join(", ", results));
            }
            catch (Exception ex)
            {
                return ErrorResult($"Failed to control cursor: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Load a scene
    /// </summary>
    public class LoadSceneToolDefinition : ToolDefinitionBase
    {
        public override string Name => "load_scene";
        public override string Description => @"Load a Unity scene by name or build index.
Can load scenes additively or replace the current scene.";

        protected override ToolInputSchema GetInputSchema()
        {
            return new ToolInputSchema
            {
                Properties = new Dictionary<string, ToolPropertySchema>
                {
                    ["sceneName"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Name of the scene to load"
                    },
                    ["buildIndex"] = new ToolPropertySchema
                    {
                        Type = "integer",
                        Description = "Build index of the scene (alternative to sceneName)"
                    },
                    ["additive"] = new ToolPropertySchema
                    {
                        Type = "boolean",
                        Description = "Load additively instead of replacing current scene (default: false)"
                    }
                }
            };
        }

        public override CallToolResult Execute(Dictionary<string, JToken> arguments)
        {
            var sceneName = GetStringArg(arguments, "sceneName");
            var buildIndex = GetIntArg(arguments, "buildIndex", -1);
            var additive = GetBoolArg(arguments, "additive", false);

            if (string.IsNullOrEmpty(sceneName) && buildIndex < 0)
            {
                return ErrorResult("Either 'sceneName' or 'buildIndex' must be provided");
            }

            try
            {
                var sceneManagerType = CSharpEvaluator.ResolveType("UnityEngine.SceneManagement.SceneManager");
                var loadSceneModeType = CSharpEvaluator.ResolveType("UnityEngine.SceneManagement.LoadSceneMode");

                if (sceneManagerType == null || loadSceneModeType == null)
                {
                    return ErrorResult("Could not find SceneManager types");
                }

                var loadMode = additive ? Enum.Parse(loadSceneModeType, "Additive") : Enum.Parse(loadSceneModeType, "Single");

                MethodInfo loadMethod;
                object[] args;

                if (!string.IsNullOrEmpty(sceneName))
                {
                    loadMethod = sceneManagerType.GetMethod("LoadScene", new[] { typeof(string), loadSceneModeType });
                    args = new object[] { sceneName, loadMode };
                }
                else
                {
                    loadMethod = sceneManagerType.GetMethod("LoadScene", new[] { typeof(int), loadSceneModeType });
                    args = new object[] { buildIndex, loadMode };
                }

                if (loadMethod == null)
                {
                    return ErrorResult("Could not find LoadScene method");
                }

                loadMethod.Invoke(null, args);
                return TextResult($"Loading scene: {sceneName ?? buildIndex.ToString()} (additive: {additive})");
            }
            catch (Exception ex)
            {
                return ErrorResult($"Failed to load scene: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Instantiate a prefab or clone a GameObject
    /// </summary>
    public class InstantiateObjectToolDefinition : ToolDefinitionBase
    {
        public override string Name => "instantiate_object";
        public override string Description => @"Clone an existing GameObject.
The new object will be created at the same position or at a specified position.";

        protected override ToolInputSchema GetInputSchema()
        {
            return new ToolInputSchema
            {
                Properties = new Dictionary<string, ToolPropertySchema>
                {
                    ["sourceGameObjectPath"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Path to the GameObject to clone"
                    },
                    ["sourceGameObjectId"] = new ToolPropertySchema
                    {
                        Type = "integer",
                        Description = "Instance ID of the GameObject to clone (alternative to path)"
                    },
                    ["position"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Position for the new object as 'x,y,z' (optional)"
                    },
                    ["newName"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Name for the new object (optional)"
                    }
                }
            };
        }

        public override CallToolResult Execute(Dictionary<string, JToken> arguments)
        {
            var path = GetStringArg(arguments, "sourceGameObjectPath");
            var instanceId = GetIntArg(arguments, "sourceGameObjectId", 0);
            var position = GetStringArg(arguments, "position");
            var newName = GetStringArg(arguments, "newName");

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
                    return ErrorResult("Either 'sourceGameObjectPath' or 'sourceGameObjectId' must be provided");
                }

                if (gameObject == null)
                {
                    return TextResult("Source GameObject not found");
                }

                var objectType = CSharpEvaluator.ResolveType("UnityEngine.Object");
                var instantiateMethod = objectType?.GetMethod("Instantiate", new[] { objectType });

                if (instantiateMethod == null)
                {
                    return ErrorResult("Could not find Instantiate method");
                }

                var newObject = instantiateMethod.Invoke(null, new[] { gameObject });

                if (newObject == null)
                {
                    return ErrorResult("Instantiate returned null");
                }

                // Set name if specified
                if (!string.IsNullOrEmpty(newName))
                {
                    UnityHelper.SetProperty(newObject, "name", newName);
                }

                // Set position if specified
                if (!string.IsNullOrEmpty(position))
                {
                    var transform = UnityHelper.GetProperty(newObject, "transform");
                    if (transform != null)
                    {
                        var parts = position.Split(',').Select(p => float.Parse(p.Trim())).ToArray();
                        if (parts.Length >= 3)
                        {
                            var vector3Type = CSharpEvaluator.ResolveType("UnityEngine.Vector3");
                            var ctor = vector3Type?.GetConstructor(new[] { typeof(float), typeof(float), typeof(float) });
                            var pos = ctor?.Invoke(new object[] { parts[0], parts[1], parts[2] });
                            if (pos != null)
                            {
                                UnityHelper.SetProperty(transform, "position", pos);
                            }
                        }
                    }
                }

                var resultName = UnityHelper.GetProperty(newObject, "name")?.ToString();
                var resultId = UnityHelper.InvokeMethod(newObject, "GetInstanceID");

                return JsonResult(new
                {
                    success = true,
                    name = resultName,
                    instanceId = resultId
                });
            }
            catch (Exception ex)
            {
                return ErrorResult($"Failed to instantiate object: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Create a new primitive GameObject
    /// </summary>
    public class CreatePrimitiveToolDefinition : ToolDefinitionBase
    {
        public override string Name => "create_primitive";
        public override string Description => @"Create a new primitive GameObject (Cube, Sphere, Capsule, Cylinder, Plane, Quad).
Returns the new object's instance ID and path.";

        protected override ToolInputSchema GetInputSchema()
        {
            return new ToolInputSchema
            {
                Properties = new Dictionary<string, ToolPropertySchema>
                {
                    ["type"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Type of primitive to create",
                        Enum = new List<string> { "Cube", "Sphere", "Capsule", "Cylinder", "Plane", "Quad" }
                    },
                    ["name"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Name for the new object (optional)"
                    },
                    ["position"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Position as 'x,y,z' (default: 0,0,0)"
                    },
                    ["scale"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Scale as 'x,y,z' (default: 1,1,1)"
                    }
                },
                Required = new List<string> { "type" }
            };
        }

        public override CallToolResult Execute(Dictionary<string, JToken> arguments)
        {
            var primitiveType = GetStringArg(arguments, "type");
            var name = GetStringArg(arguments, "name");
            var positionStr = GetStringArg(arguments, "position");
            var scaleStr = GetStringArg(arguments, "scale");

            if (string.IsNullOrEmpty(primitiveType))
            {
                return ErrorResult("type is required");
            }

            try
            {
                var gameObjectType = CSharpEvaluator.ResolveType("UnityEngine.GameObject");
                var primitiveTypeEnum = CSharpEvaluator.ResolveType("UnityEngine.PrimitiveType");
                var vector3Type = CSharpEvaluator.ResolveType("UnityEngine.Vector3");

                if (gameObjectType == null || primitiveTypeEnum == null)
                {
                    return ErrorResult("Could not resolve required Unity types");
                }

                // Find CreatePrimitive method
                var createMethod = gameObjectType.GetMethod("CreatePrimitive",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { primitiveTypeEnum },
                    null);

                if (createMethod == null)
                {
                    return ErrorResult("Could not find GameObject.CreatePrimitive method");
                }

                // Parse primitive type enum
                var primitiveEnum = Enum.Parse(primitiveTypeEnum, primitiveType);
                var newObject = createMethod.Invoke(null, new[] { primitiveEnum });

                if (newObject == null)
                {
                    return ErrorResult("CreatePrimitive returned null");
                }

                // Set name if provided
                if (!string.IsNullOrEmpty(name))
                {
                    UnityHelper.SetProperty(newObject, "name", name);
                }

                var transform = UnityHelper.GetProperty(newObject, "transform");
                if (transform != null && vector3Type != null)
                {
                    var vector3Ctor = vector3Type.GetConstructor(new[] { typeof(float), typeof(float), typeof(float) });

                    // Set position if specified
                    if (!string.IsNullOrEmpty(positionStr))
                    {
                        var parts = positionStr.Split(',').Select(p => float.Parse(p.Trim())).ToArray();
                        if (parts.Length >= 3 && vector3Ctor != null)
                        {
                            var pos = vector3Ctor.Invoke(new object[] { parts[0], parts[1], parts[2] });
                            UnityHelper.SetProperty(transform, "position", pos);
                        }
                    }

                    // Set scale if specified
                    if (!string.IsNullOrEmpty(scaleStr))
                    {
                        var parts = scaleStr.Split(',').Select(p => float.Parse(p.Trim())).ToArray();
                        if (parts.Length >= 3 && vector3Ctor != null)
                        {
                            var scale = vector3Ctor.Invoke(new object[] { parts[0], parts[1], parts[2] });
                            UnityHelper.SetProperty(transform, "localScale", scale);
                        }
                    }
                }

                var resultName = UnityHelper.GetProperty(newObject, "name")?.ToString();
                var resultId = UnityHelper.InvokeMethod(newObject, "GetInstanceID");

                return JsonResult(new
                {
                    success = true,
                    name = resultName,
                    instanceId = resultId,
                    type = primitiveType
                });
            }
            catch (Exception ex)
            {
                return ErrorResult($"Failed to create primitive: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Set transform properties (position, rotation, scale)
    /// </summary>
    public class SetTransformToolDefinition : ToolDefinitionBase
    {
        public override string Name => "set_transform";
        public override string Description => @"Set the position, rotation, or scale of a GameObject's transform.
Provide values as 'x,y,z' strings. Rotation is in euler angles (degrees).";

        protected override ToolInputSchema GetInputSchema()
        {
            return new ToolInputSchema
            {
                Properties = new Dictionary<string, ToolPropertySchema>
                {
                    ["gameObjectPath"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Path to the GameObject"
                    },
                    ["gameObjectId"] = new ToolPropertySchema
                    {
                        Type = "integer",
                        Description = "Instance ID of the GameObject (alternative to path)"
                    },
                    ["position"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "New world position as 'x,y,z'"
                    },
                    ["localPosition"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "New local position as 'x,y,z'"
                    },
                    ["rotation"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "New euler angles as 'x,y,z' (degrees)"
                    },
                    ["localRotation"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "New local euler angles as 'x,y,z' (degrees)"
                    },
                    ["scale"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "New local scale as 'x,y,z'"
                    }
                }
            };
        }

        public override CallToolResult Execute(Dictionary<string, JToken> arguments)
        {
            var path = GetStringArg(arguments, "gameObjectPath");
            var instanceId = GetIntArg(arguments, "gameObjectId", 0);
            var position = GetStringArg(arguments, "position");
            var localPosition = GetStringArg(arguments, "localPosition");
            var rotation = GetStringArg(arguments, "rotation");
            var localRotation = GetStringArg(arguments, "localRotation");
            var scale = GetStringArg(arguments, "scale");

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

                var transform = UnityHelper.GetProperty(gameObject, "transform");
                if (transform == null)
                {
                    return ErrorResult("Transform not found on GameObject");
                }

                var vector3Type = CSharpEvaluator.ResolveType("UnityEngine.Vector3");
                var quaternionType = CSharpEvaluator.ResolveType("UnityEngine.Quaternion");

                if (vector3Type == null)
                {
                    return ErrorResult("Could not resolve Vector3 type");
                }

                var vector3Ctor = vector3Type.GetConstructor(new[] { typeof(float), typeof(float), typeof(float) });
                var changes = new List<string>();

                // Helper to parse and set Vector3 property
                void SetVector3Property(string value, string propName)
                {
                    if (!string.IsNullOrEmpty(value) && vector3Ctor != null)
                    {
                        var parts = value.Split(',').Select(p => float.Parse(p.Trim())).ToArray();
                        if (parts.Length >= 3)
                        {
                            var vec = vector3Ctor.Invoke(new object[] { parts[0], parts[1], parts[2] });
                            UnityHelper.SetProperty(transform, propName, vec);
                            changes.Add($"{propName} = ({parts[0]}, {parts[1]}, {parts[2]})");
                        }
                    }
                }

                // Set position
                SetVector3Property(position, "position");
                SetVector3Property(localPosition, "localPosition");
                SetVector3Property(scale, "localScale");

                // Set rotation (convert euler to quaternion)
                void SetEulerRotation(string value, string propName)
                {
                    if (!string.IsNullOrEmpty(value) && quaternionType != null)
                    {
                        var parts = value.Split(',').Select(p => float.Parse(p.Trim())).ToArray();
                        if (parts.Length >= 3)
                        {
                            // Create euler angles Vector3
                            var euler = vector3Ctor?.Invoke(new object[] { parts[0], parts[1], parts[2] });

                            // Call Quaternion.Euler(Vector3)
                            var eulerMethod = quaternionType.GetMethod("Euler",
                                BindingFlags.Public | BindingFlags.Static,
                                null,
                                new[] { vector3Type },
                                null);

                            if (eulerMethod != null && euler != null)
                            {
                                var quat = eulerMethod.Invoke(null, new[] { euler });
                                UnityHelper.SetProperty(transform, propName, quat);
                                changes.Add($"{propName} = Euler({parts[0]}, {parts[1]}, {parts[2]})");
                            }
                        }
                    }
                }

                SetEulerRotation(rotation, "rotation");
                SetEulerRotation(localRotation, "localRotation");

                if (changes.Count == 0)
                {
                    // Return current transform info
                    var pos = UnityHelper.GetProperty(transform, "position");
                    var localPos = UnityHelper.GetProperty(transform, "localPosition");
                    var eulerAngles = UnityHelper.GetProperty(transform, "eulerAngles");
                    var localScale = UnityHelper.GetProperty(transform, "localScale");

                    return JsonResult(new
                    {
                        position = pos?.ToString(),
                        localPosition = localPos?.ToString(),
                        eulerAngles = eulerAngles?.ToString(),
                        localScale = localScale?.ToString()
                    });
                }

                return TextResult($"Transform updated: {string.Join(", ", changes)}");
            }
            catch (Exception ex)
            {
                return ErrorResult($"Failed to set transform: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Inspect materials on a renderer
    /// </summary>
    public class InspectMaterialToolDefinition : ToolDefinitionBase
    {
        public override string Name => "inspect_material";
        public override string Description => @"Inspect the material(s) on a GameObject's renderer.
Returns material properties including shader, color, and texture information.";

        protected override ToolInputSchema GetInputSchema()
        {
            return new ToolInputSchema
            {
                Properties = new Dictionary<string, ToolPropertySchema>
                {
                    ["gameObjectPath"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Path to the GameObject"
                    },
                    ["gameObjectId"] = new ToolPropertySchema
                    {
                        Type = "integer",
                        Description = "Instance ID of the GameObject (alternative to path)"
                    },
                    ["materialIndex"] = new ToolPropertySchema
                    {
                        Type = "integer",
                        Description = "Index of the material to inspect (default: all materials)"
                    }
                }
            };
        }

        public override CallToolResult Execute(Dictionary<string, JToken> arguments)
        {
            var path = GetStringArg(arguments, "gameObjectPath");
            var instanceId = GetIntArg(arguments, "gameObjectId", 0);
            var materialIndex = GetIntArg(arguments, "materialIndex", -1);

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

                // Try to get a Renderer component
                var renderer = UnityHelper.GetComponent(gameObject, "Renderer");
                if (renderer == null)
                {
                    renderer = UnityHelper.GetComponent(gameObject, "MeshRenderer");
                }
                if (renderer == null)
                {
                    renderer = UnityHelper.GetComponent(gameObject, "SkinnedMeshRenderer");
                }

                if (renderer == null)
                {
                    return TextResult("No renderer found on GameObject");
                }

                // Get materials array
                var materials = UnityHelper.GetProperty(renderer, "materials");
                var sharedMaterials = UnityHelper.GetProperty(renderer, "sharedMaterials");

                var materialList = new List<Dictionary<string, object>>();

                // Handle materials array
                var matsToProcess = (materials ?? sharedMaterials) as System.Collections.IEnumerable;
                if (matsToProcess != null)
                {
                    int index = 0;
                    foreach (var mat in matsToProcess)
                    {
                        if (materialIndex >= 0 && index != materialIndex)
                        {
                            index++;
                            continue;
                        }

                        if (mat != null)
                        {
                            var matInfo = InspectMaterial(mat, index);
                            materialList.Add(matInfo);
                        }
                        index++;
                    }
                }

                return JsonResult(new
                {
                    rendererType = renderer.GetType().Name,
                    materialCount = materialList.Count,
                    materials = materialList
                });
            }
            catch (Exception ex)
            {
                return ErrorResult($"Failed to inspect material: {ex.Message}");
            }
        }

        private Dictionary<string, object> InspectMaterial(object material, int index)
        {
            var info = new Dictionary<string, object>
            {
                ["index"] = index,
                ["name"] = UnityHelper.GetProperty(material, "name"),
                ["instanceId"] = UnityHelper.InvokeMethod(material, "GetInstanceID")
            };

            // Get shader info
            var shader = UnityHelper.GetProperty(material, "shader");
            if (shader != null)
            {
                info["shaderName"] = UnityHelper.GetProperty(shader, "name");
            }

            // Get render queue
            info["renderQueue"] = UnityHelper.GetProperty(material, "renderQueue");

            // Get common material properties
            var colorType = CSharpEvaluator.ResolveType("UnityEngine.Color");

            // Try to get main color
            try
            {
                var getColorMethod = material.GetType().GetMethod("GetColor",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(string) },
                    null);

                if (getColorMethod != null)
                {
                    var mainColor = getColorMethod.Invoke(material, new object[] { "_Color" });
                    if (mainColor != null)
                    {
                        info["mainColor"] = mainColor.ToString();
                    }
                }
            }
            catch { }

            // Get main texture info
            try
            {
                var mainTexture = UnityHelper.GetProperty(material, "mainTexture");
                if (mainTexture != null)
                {
                    info["mainTexture"] = new Dictionary<string, object>
                    {
                        ["name"] = UnityHelper.GetProperty(mainTexture, "name"),
                        ["width"] = UnityHelper.GetProperty(mainTexture, "width"),
                        ["height"] = UnityHelper.GetProperty(mainTexture, "height")
                    };
                }
            }
            catch { }

            // Get texture scale and offset
            var mainTextureScale = UnityHelper.GetProperty(material, "mainTextureScale");
            var mainTextureOffset = UnityHelper.GetProperty(material, "mainTextureOffset");

            if (mainTextureScale != null)
            {
                info["mainTextureScale"] = mainTextureScale.ToString();
            }
            if (mainTextureOffset != null)
            {
                info["mainTextureOffset"] = mainTextureOffset.ToString();
            }

            // Get common shader keywords
            try
            {
                var keywords = UnityHelper.GetProperty(material, "shaderKeywords");
                if (keywords is string[] keywordArray && keywordArray.Length > 0)
                {
                    info["shaderKeywords"] = keywordArray;
                }
            }
            catch { }

            return info;
        }
    }

    /// <summary>
    /// Destroy a GameObject
    /// </summary>
    public class DestroyObjectToolDefinition : ToolDefinitionBase
    {
        public override string Name => "destroy_object";
        public override string Description => @"Destroy a GameObject or component.
WARNING: This cannot be undone!";

        protected override ToolInputSchema GetInputSchema()
        {
            return new ToolInputSchema
            {
                Properties = new Dictionary<string, ToolPropertySchema>
                {
                    ["gameObjectPath"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Path to the GameObject to destroy"
                    },
                    ["gameObjectId"] = new ToolPropertySchema
                    {
                        Type = "integer",
                        Description = "Instance ID of the GameObject (alternative to path)"
                    },
                    ["componentType"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "If specified, only destroy this component instead of the whole GameObject"
                    }
                }
            };
        }

        public override CallToolResult Execute(Dictionary<string, JToken> arguments)
        {
            var path = GetStringArg(arguments, "gameObjectPath");
            var instanceId = GetIntArg(arguments, "gameObjectId", 0);
            var componentType = GetStringArg(arguments, "componentType");

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

                var objectType = CSharpEvaluator.ResolveType("UnityEngine.Object");
                var destroyMethod = objectType?.GetMethod("Destroy", new[] { objectType });

                if (destroyMethod == null)
                {
                    return ErrorResult("Could not find Destroy method");
                }

                object toDestroy = gameObject;

                // If component specified, find and destroy it instead
                if (!string.IsNullOrEmpty(componentType))
                {
                    var components = UnityHelper.GetComponents(gameObject);
                    toDestroy = components.FirstOrDefault(c =>
                        c.GetType().Name.Equals(componentType, StringComparison.OrdinalIgnoreCase) ||
                        c.GetType().FullName.Equals(componentType, StringComparison.OrdinalIgnoreCase));

                    if (toDestroy == null)
                    {
                        return TextResult($"Component '{componentType}' not found");
                    }
                }

                var targetName = UnityHelper.GetProperty(toDestroy, "name")?.ToString();
                destroyMethod.Invoke(null, new[] { toDestroy });

                return TextResult($"Destroyed: {targetName ?? componentType ?? "object"}");
            }
            catch (Exception ex)
            {
                return ErrorResult($"Failed to destroy object: {ex.Message}");
            }
        }
    }
}
