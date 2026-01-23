using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using MelonLoader;
using Newtonsoft.Json;

namespace MelonMCP.Server
{
    /// <summary>
    /// Helper methods for accessing Unity objects via reflection
    /// Works with both Mono and IL2CPP games
    /// </summary>
    public static class UnityHelper
    {
        private static Assembly _unityEngine;
        private static Assembly _unityCoreModule;
        private static bool _isIl2Cpp;

        // Cached types - IL2CPP wrapped versions
        private static Type _gameObjectType;
        private static Type _componentType;
        private static Type _behaviourType;
        private static Type _transformType;
        private static Type _sceneManagerType;
        private static Type _sceneType;
        private static Type _objectType;
        private static Type _timeType;
        private static Type _screenType;
        private static Type _applicationtype;
        private static Type _screenCaptureType;
        private static Type _texture2DType;
        private static Type _renderTextureType;
        private static Type _rectType;
        private static Type _textureFormatType;

        // Cached methods
        private static MethodInfo _findObjectsOfTypeMethod;
        private static MethodInfo _findObjectsOfTypeGenericMethod;
        private static MethodInfo _getActiveSceneMethod;
        private static MethodInfo _getRootGameObjectsMethod;
        private static MethodInfo _gameObjectFindMethod;

        static UnityHelper()
        {
            CacheUnityTypes();
        }

        private static void CacheUnityTypes()
        {
            try
            {
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();

                // Check if we're in IL2CPP environment
                _isIl2Cpp = assemblies.Any(a => a.GetName().Name.Contains("Il2Cpp"));

                if (_isIl2Cpp)
                {
                    CacheIl2CppTypes(assemblies);
                }
                else
                {
                    CacheMonoTypes(assemblies);
                }
            }
            catch (Exception ex)
            {
                MelonMCPPlugin.Logger?.Warning($"Failed to cache Unity types: {ex.Message}");
            }
        }

        private static void CacheIl2CppTypes(Assembly[] assemblies)
        {
            // For IL2CPP, look for the Il2Cpp wrapped types
            foreach (var asm in assemblies)
            {
                try
                {
                    if (asm.IsDynamic) continue;
                    var asmName = asm.GetName().Name;

                    // Skip non-Unity assemblies
                    if (!asmName.Contains("Unity") && !asmName.Contains("Il2Cpp")) continue;

                    foreach (var type in asm.GetExportedTypes())
                    {
                        var fullName = type.FullName;
                        if (fullName == null) continue;

                        // Cache Unity types (wrapped by Il2CppInterop)
                        if (fullName == "UnityEngine.Object" && _objectType == null) _objectType = type;
                        else if (fullName == "UnityEngine.GameObject" && _gameObjectType == null) _gameObjectType = type;
                        else if (fullName == "UnityEngine.Component" && _componentType == null) _componentType = type;
                        else if (fullName == "UnityEngine.Behaviour" && _behaviourType == null) _behaviourType = type;
                        else if (fullName == "UnityEngine.Transform" && _transformType == null) _transformType = type;
                        else if (fullName == "UnityEngine.Time" && _timeType == null) _timeType = type;
                        else if (fullName == "UnityEngine.Screen" && _screenType == null) _screenType = type;
                        else if (fullName == "UnityEngine.Application" && _applicationtype == null) _applicationtype = type;
                        else if (fullName == "UnityEngine.Texture2D" && _texture2DType == null) _texture2DType = type;
                        else if (fullName == "UnityEngine.RenderTexture" && _renderTextureType == null) _renderTextureType = type;
                        else if (fullName == "UnityEngine.Rect" && _rectType == null) _rectType = type;
                        else if (fullName == "UnityEngine.TextureFormat" && _textureFormatType == null) _textureFormatType = type;
                        else if (fullName == "UnityEngine.ScreenCapture" && _screenCaptureType == null) _screenCaptureType = type;
                        else if (fullName == "UnityEngine.SceneManagement.SceneManager" && _sceneManagerType == null) _sceneManagerType = type;
                        else if (fullName == "UnityEngine.SceneManagement.Scene" && _sceneType == null) _sceneType = type;
                    }
                }
                catch { }
            }

            CacheCommonMethods();
        }

        private static void CacheMonoTypes(Assembly[] assemblies)
        {
            _unityEngine = assemblies.FirstOrDefault(a => a.GetName().Name == "UnityEngine");
            _unityCoreModule = assemblies.FirstOrDefault(a => a.GetName().Name == "UnityEngine.CoreModule");

            var primaryAsm = _unityCoreModule ?? _unityEngine;

            if (primaryAsm != null)
            {
                _objectType = primaryAsm.GetType("UnityEngine.Object");
                _gameObjectType = primaryAsm.GetType("UnityEngine.GameObject");
                _componentType = primaryAsm.GetType("UnityEngine.Component");
                _behaviourType = primaryAsm.GetType("UnityEngine.Behaviour");
                _transformType = primaryAsm.GetType("UnityEngine.Transform");
                _timeType = primaryAsm.GetType("UnityEngine.Time");
                _screenType = primaryAsm.GetType("UnityEngine.Screen");
                _applicationtype = primaryAsm.GetType("UnityEngine.Application");
                _texture2DType = primaryAsm.GetType("UnityEngine.Texture2D");
                _renderTextureType = primaryAsm.GetType("UnityEngine.RenderTexture");
                _rectType = primaryAsm.GetType("UnityEngine.Rect");
                _textureFormatType = primaryAsm.GetType("UnityEngine.TextureFormat");
            }

            var sceneManagementAsm = assemblies.FirstOrDefault(a => a.GetName().Name == "UnityEngine.SceneManagementModule");
            _sceneManagerType = sceneManagementAsm?.GetType("UnityEngine.SceneManagement.SceneManager");
            _sceneType = sceneManagementAsm?.GetType("UnityEngine.SceneManagement.Scene");

            if (_sceneManagerType == null && primaryAsm != null)
            {
                _sceneManagerType = primaryAsm.GetType("UnityEngine.SceneManagement.SceneManager");
                _sceneType = primaryAsm.GetType("UnityEngine.SceneManagement.Scene");
            }

            var screenshotAsm = assemblies.FirstOrDefault(a => a.GetName().Name == "UnityEngine.ScreenCaptureModule");
            _screenCaptureType = screenshotAsm?.GetType("UnityEngine.ScreenCapture");

            CacheCommonMethods();
        }

        private static void CacheCommonMethods()
        {
            // FindObjectsOfType
            if (_objectType != null)
            {
                // Try generic version first (works better in IL2CPP)
                _findObjectsOfTypeGenericMethod = _objectType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "FindObjectsOfType" && m.IsGenericMethodDefinition);

                // Also cache non-generic version
                _findObjectsOfTypeMethod = _objectType.GetMethod("FindObjectsOfType",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(Type) },
                    null);

                // For IL2CPP, there might be a method that takes Il2CppType
                if (_findObjectsOfTypeMethod == null)
                {
                    _findObjectsOfTypeMethod = _objectType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .FirstOrDefault(m => m.Name == "FindObjectsOfType" && !m.IsGenericMethodDefinition && m.GetParameters().Length == 1);
                }
            }

            // GetActiveScene
            if (_sceneManagerType != null)
            {
                _getActiveSceneMethod = _sceneManagerType.GetMethod("GetActiveScene",
                    BindingFlags.Public | BindingFlags.Static);
            }

            // GameObject.Find
            if (_gameObjectType != null)
            {
                _gameObjectFindMethod = _gameObjectType.GetMethod("Find",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(string) },
                    null);
            }
        }

        public static bool IsUnityAvailable => _gameObjectType != null;

        #region Scene Information

        public static string GetSceneHierarchyJson()
        {
            try
            {
                var hierarchy = new List<object>();
                var rootObjects = GetRootGameObjects();

                foreach (var go in rootObjects)
                {
                    hierarchy.Add(GetGameObjectHierarchy(go, 0, 3));
                }

                return JsonConvert.SerializeObject(hierarchy, MCPProtocol.JsonSettings);
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { error = ex.Message }, MCPProtocol.JsonSettings);
            }
        }

        public static object GetGameObjectHierarchy(object gameObject, int depth, int maxDepth)
        {
            if (gameObject == null || depth > maxDepth) return null;

            try
            {
                var name = GetProperty(gameObject, "name")?.ToString() ?? "Unknown";
                var activeSelf = (bool)(GetProperty(gameObject, "activeSelf") ?? true);
                var activeInHierarchy = (bool)(GetProperty(gameObject, "activeInHierarchy") ?? true);
                var tag = GetProperty(gameObject, "tag")?.ToString() ?? "Untagged";
                var layer = (int)(GetProperty(gameObject, "layer") ?? 0);

                var transform = GetProperty(gameObject, "transform");
                var childCount = transform != null ? (int)(GetProperty(transform, "childCount") ?? 0) : 0;

                var result = new Dictionary<string, object>
                {
                    ["name"] = name,
                    ["activeSelf"] = activeSelf,
                    ["activeInHierarchy"] = activeInHierarchy,
                    ["tag"] = tag,
                    ["layer"] = layer,
                    ["instanceId"] = InvokeMethod(gameObject, "GetInstanceID"),
                    ["childCount"] = childCount
                };

                // Get components
                var components = InvokeMethod(gameObject, "GetComponents", new object[] { _componentType }) as Array;
                if (components != null)
                {
                    var componentList = new List<string>();
                    foreach (var comp in components)
                    {
                        if (comp != null)
                        {
                            componentList.Add(comp.GetType().Name);
                        }
                    }
                    result["components"] = componentList;
                }

                // Get children (limited depth)
                if (depth < maxDepth && transform != null && childCount > 0)
                {
                    var children = new List<object>();
                    for (int i = 0; i < Math.Min(childCount, 50); i++)
                    {
                        var childTransform = InvokeMethod(transform, "GetChild", new object[] { i });
                        if (childTransform != null)
                        {
                            var childGo = GetProperty(childTransform, "gameObject");
                            var childHierarchy = GetGameObjectHierarchy(childGo, depth + 1, maxDepth);
                            if (childHierarchy != null)
                            {
                                children.Add(childHierarchy);
                            }
                        }
                    }
                    if (childCount > 50)
                    {
                        children.Add(new Dictionary<string, object> { ["note"] = $"... and {childCount - 50} more children" });
                    }
                    result["children"] = children;
                }

                return result;
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { ["error"] = ex.Message };
            }
        }

        public static List<object> GetRootGameObjects()
        {
            var result = new List<object>();

            try
            {
                if (_getActiveSceneMethod != null)
                {
                    var scene = _getActiveSceneMethod.Invoke(null, null);
                    if (scene != null)
                    {
                        var sceneType = scene.GetType();

                        // Try GetRootGameObjects() - returns array
                        var getRootMethod = sceneType.GetMethod("GetRootGameObjects",
                            BindingFlags.Public | BindingFlags.Instance,
                            null,
                            Type.EmptyTypes,
                            null);

                        if (getRootMethod != null)
                        {
                            var rootObjects = getRootMethod.Invoke(scene, null);
                            if (rootObjects != null)
                            {
                                // Handle both regular arrays and IL2CPP arrays
                                if (rootObjects is IEnumerable enumerable)
                                {
                                    foreach (var obj in enumerable)
                                    {
                                        if (obj != null && IsValidUnityObject(obj))
                                        {
                                            result.Add(obj);
                                        }
                                    }
                                }
                            }
                        }

                        // If no results, try alternative: get rootCount and iterate
                        if (result.Count == 0)
                        {
                            var rootCountProp = sceneType.GetProperty("rootCount");
                            if (rootCountProp != null)
                            {
                                var rootCount = (int)rootCountProp.GetValue(scene);
                                if (rootCount > 0)
                                {
                                    // Use FindObjectsOfType<GameObject> and filter by scene
                                    var allGameObjects = FindObjectsOfType(_gameObjectType);
                                    foreach (var go in allGameObjects)
                                    {
                                        if (go == null) continue;
                                        var transform = GetProperty(go, "transform");
                                        if (transform != null)
                                        {
                                            var parent = GetProperty(transform, "parent");
                                            if (parent == null || !IsValidUnityObject(parent))
                                            {
                                                result.Add(go);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                // Fallback: use FindObjectsOfType and filter for root objects
                if (result.Count == 0 && _gameObjectType != null)
                {
                    var allGameObjects = FindObjectsOfType(_gameObjectType);
                    foreach (var go in allGameObjects)
                    {
                        if (go == null) continue;
                        var transform = GetProperty(go, "transform");
                        if (transform != null)
                        {
                            var parent = GetProperty(transform, "parent");
                            if (parent == null || !IsValidUnityObject(parent))
                            {
                                result.Add(go);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MelonMCPPlugin.Logger?.Warning($"Failed to get root game objects: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Check if a Unity object is valid (not destroyed)
        /// </summary>
        private static bool IsValidUnityObject(object obj)
        {
            if (obj == null) return false;

            try
            {
                // In IL2CPP, destroyed objects have m_CachedPtr == 0
                // We can check by trying to access any property - it will throw or return null
                var type = obj.GetType();

                // Check for Unity Object's implicit bool operator or GetInstanceID
                var getInstanceId = type.GetMethod("GetInstanceID", BindingFlags.Public | BindingFlags.Instance);
                if (getInstanceId != null)
                {
                    var instanceId = getInstanceId.Invoke(obj, null);
                    return instanceId != null && (int)instanceId != 0;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Game Information

        public static string GetGameInfoJson()
        {
            try
            {
                // Get engine version safely via reflection to avoid assembly dependency
                string engineVersion = "unknown";
                try
                {
                    var engineVersionObj = typeof(MelonLoader.InternalUtils.UnityInformationHandler)
                        .GetProperty("EngineVersion")?.GetValue(null);
                    engineVersion = engineVersionObj?.ToString() ?? "unknown";
                }
                catch { }

                var info = new Dictionary<string, object>
                {
                    ["gameName"] = MelonLoader.InternalUtils.UnityInformationHandler.GameName,
                    ["gameDeveloper"] = MelonLoader.InternalUtils.UnityInformationHandler.GameDeveloper,
                    ["gameVersion"] = MelonLoader.InternalUtils.UnityInformationHandler.GameVersion,
                    ["engineVersion"] = engineVersion,
                    ["melonLoaderVersion"] = BuildInfo.Version,
                    ["platform"] = Environment.OSVersion.Platform.ToString(),
                    ["is64Bit"] = Environment.Is64BitProcess
                };

                // Add runtime info
                if (_applicationtype != null)
                {
                    info["productName"] = GetStaticProperty(_applicationtype, "productName");
                    info["companyName"] = GetStaticProperty(_applicationtype, "companyName");
                    info["unityVersion"] = GetStaticProperty(_applicationtype, "unityVersion");
                    info["targetFrameRate"] = GetStaticProperty(_applicationtype, "targetFrameRate");
                    info["runInBackground"] = GetStaticProperty(_applicationtype, "runInBackground");
                    info["isPlaying"] = GetStaticProperty(_applicationtype, "isPlaying");
                    info["isFocused"] = GetStaticProperty(_applicationtype, "isFocused");
                    info["dataPath"] = GetStaticProperty(_applicationtype, "dataPath");
                    info["persistentDataPath"] = GetStaticProperty(_applicationtype, "persistentDataPath");
                }

                if (_screenType != null)
                {
                    info["screenWidth"] = GetStaticProperty(_screenType, "width");
                    info["screenHeight"] = GetStaticProperty(_screenType, "height");
                    info["fullScreen"] = GetStaticProperty(_screenType, "fullScreen");
                }

                // Scene info
                if (_getActiveSceneMethod != null)
                {
                    var scene = _getActiveSceneMethod.Invoke(null, null);
                    if (scene != null)
                    {
                        info["currentScene"] = new Dictionary<string, object>
                        {
                            ["name"] = GetProperty(scene, "name"),
                            ["buildIndex"] = GetProperty(scene, "buildIndex"),
                            ["isLoaded"] = GetProperty(scene, "isLoaded"),
                            ["rootCount"] = GetProperty(scene, "rootCount")
                        };
                    }
                }

                return JsonConvert.SerializeObject(info, MCPProtocol.JsonSettings);
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { error = ex.Message }, MCPProtocol.JsonSettings);
            }
        }

        public static Dictionary<string, object> GetTimeInfo()
        {
            var info = new Dictionary<string, object>();

            try
            {
                if (_timeType != null)
                {
                    info["time"] = GetStaticProperty(_timeType, "time");
                    info["deltaTime"] = GetStaticProperty(_timeType, "deltaTime");
                    info["fixedDeltaTime"] = GetStaticProperty(_timeType, "fixedDeltaTime");
                    info["unscaledTime"] = GetStaticProperty(_timeType, "unscaledTime");
                    info["unscaledDeltaTime"] = GetStaticProperty(_timeType, "unscaledDeltaTime");
                    info["timeScale"] = GetStaticProperty(_timeType, "timeScale");
                    info["frameCount"] = GetStaticProperty(_timeType, "frameCount");
                    info["realtimeSinceStartup"] = GetStaticProperty(_timeType, "realtimeSinceStartup");
                    info["timeSinceLevelLoad"] = GetStaticProperty(_timeType, "timeSinceLevelLoad");
                }
            }
            catch (Exception ex)
            {
                info["error"] = ex.Message;
            }

            return info;
        }

        #endregion

        #region Object Finding

        public static object FindGameObject(string path)
        {
            try
            {
                // Try to find by exact path
                var findMethod = _gameObjectType?.GetMethod("Find",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(string) },
                    null);

                if (findMethod != null)
                {
                    return findMethod.Invoke(null, new object[] { path });
                }
            }
            catch (Exception ex)
            {
                MelonMCPPlugin.Logger?.Warning($"Failed to find GameObject: {ex.Message}");
            }

            return null;
        }

        public static object[] FindObjectsOfType(Type type)
        {
            var results = new List<object>();

            try
            {
                if (type == null) return Array.Empty<object>();

                // Try generic method first (works best in IL2CPP)
                if (_findObjectsOfTypeGenericMethod != null)
                {
                    try
                    {
                        var genericMethod = _findObjectsOfTypeGenericMethod.MakeGenericMethod(type);
                        var result = genericMethod.Invoke(null, null);
                        if (result != null && result is IEnumerable enumerable)
                        {
                            foreach (var obj in enumerable)
                            {
                                if (obj != null && IsValidUnityObject(obj))
                                {
                                    results.Add(obj);
                                }
                            }
                            if (results.Count > 0) return results.ToArray();
                        }
                    }
                    catch { }
                }

                // Try non-generic method with Type parameter
                if (_findObjectsOfTypeMethod != null)
                {
                    try
                    {
                        var result = _findObjectsOfTypeMethod.Invoke(null, new object[] { type });
                        if (result != null)
                        {
                            if (result is IEnumerable enumerable)
                            {
                                foreach (var obj in enumerable)
                                {
                                    if (obj != null && IsValidUnityObject(obj))
                                    {
                                        results.Add(obj);
                                    }
                                }
                            }
                            else if (result is Array array)
                            {
                                foreach (var obj in array)
                                {
                                    if (obj != null && IsValidUnityObject(obj))
                                    {
                                        results.Add(obj);
                                    }
                                }
                            }
                        }
                        if (results.Count > 0) return results.ToArray();
                    }
                    catch { }
                }

                // Try Il2Cpp-specific approach: look for FindObjectsOfType that takes Il2CppType
                if (_isIl2Cpp && _objectType != null)
                {
                    // Try to get Il2CppType from the managed type
                    var il2cppTypeProperty = type.GetProperty("Il2CppType", BindingFlags.Public | BindingFlags.Static);
                    if (il2cppTypeProperty != null)
                    {
                        var il2cppType = il2cppTypeProperty.GetValue(null);
                        if (il2cppType != null)
                        {
                            var findMethods = _objectType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                                .Where(m => m.Name == "FindObjectsOfType" && !m.IsGenericMethodDefinition)
                                .ToList();

                            foreach (var method in findMethods)
                            {
                                var parameters = method.GetParameters();
                                if (parameters.Length == 1 && parameters[0].ParameterType.IsAssignableFrom(il2cppType.GetType()))
                                {
                                    try
                                    {
                                        var result = method.Invoke(null, new[] { il2cppType });
                                        if (result is IEnumerable enumerable)
                                        {
                                            foreach (var obj in enumerable)
                                            {
                                                if (obj != null && IsValidUnityObject(obj))
                                                {
                                                    results.Add(obj);
                                                }
                                            }
                                        }
                                        if (results.Count > 0) return results.ToArray();
                                    }
                                    catch { }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MelonMCPPlugin.Logger?.Warning($"Failed to find objects of type {type?.Name}: {ex.Message}");
            }

            return results.ToArray();
        }

        /// <summary>
        /// Find objects of type with include inactive option
        /// </summary>
        public static object[] FindObjectsOfType(Type type, bool includeInactive)
        {
            if (!includeInactive)
            {
                return FindObjectsOfType(type);
            }

            var results = new List<object>();

            try
            {
                // Try FindObjectsOfType with includeInactive parameter
                if (_objectType != null)
                {
                    var methods = _objectType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .Where(m => m.Name == "FindObjectsOfType" && m.GetParameters().Length == 2)
                        .ToList();

                    foreach (var method in methods)
                    {
                        var parameters = method.GetParameters();
                        if (parameters.Length == 2 && parameters[1].ParameterType == typeof(bool))
                        {
                            try
                            {
                                if (method.IsGenericMethodDefinition)
                                {
                                    var genericMethod = method.MakeGenericMethod(type);
                                    var result = genericMethod.Invoke(null, new object[] { includeInactive });
                                    if (result is IEnumerable enumerable)
                                    {
                                        foreach (var obj in enumerable)
                                        {
                                            if (obj != null && IsValidUnityObject(obj))
                                            {
                                                results.Add(obj);
                                            }
                                        }
                                    }
                                    if (results.Count > 0) return results.ToArray();
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }

            // Fallback to regular FindObjectsOfType
            return FindObjectsOfType(type);
        }

        public static object FindObjectByInstanceId(int instanceId)
        {
            try
            {
                // Get all GameObjects and find by instance ID
                if (_findObjectsOfTypeMethod != null && _gameObjectType != null)
                {
                    var allObjects = _findObjectsOfTypeMethod.Invoke(null, new object[] { _gameObjectType }) as Array;
                    if (allObjects != null)
                    {
                        foreach (var obj in allObjects)
                        {
                            if (obj != null)
                            {
                                var id = InvokeMethod(obj, "GetInstanceID");
                                if (id != null && (int)id == instanceId)
                                {
                                    return obj;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MelonMCPPlugin.Logger?.Warning($"Failed to find object by instance ID: {ex.Message}");
            }

            return null;
        }

        #endregion

        #region Component Operations

        public static object[] GetComponents(object gameObject)
        {
            var result = new List<object>();

            try
            {
                if (gameObject == null) return Array.Empty<object>();

                // Try generic GetComponents<Component>() first (works better in IL2CPP)
                if (_componentType != null)
                {
                    var goType = gameObject.GetType();

                    // Try GetComponents<T>() generic method
                    var genericMethod = goType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name == "GetComponents" && m.IsGenericMethodDefinition &&
                                           m.GetParameters().Length == 0);

                    if (genericMethod != null)
                    {
                        try
                        {
                            var typedMethod = genericMethod.MakeGenericMethod(_componentType);
                            var genericResult = typedMethod.Invoke(gameObject, null);
                            if (genericResult is IEnumerable enumerable)
                            {
                                foreach (var comp in enumerable)
                                {
                                    if (comp != null && IsValidUnityObject(comp))
                                    {
                                        result.Add(comp);
                                    }
                                }
                                if (result.Count > 0) return result.ToArray();
                            }
                        }
                        catch { }
                    }

                    // Try GetComponents(Type) method
                    var components = InvokeMethod(gameObject, "GetComponents", new object[] { _componentType });
                    if (components != null)
                    {
                        if (components is IEnumerable enumerable)
                        {
                            foreach (var comp in enumerable)
                            {
                                if (comp != null && IsValidUnityObject(comp))
                                {
                                    result.Add(comp);
                                }
                            }
                            if (result.Count > 0) return result.ToArray();
                        }
                        else if (components is Array array)
                        {
                            foreach (var comp in array)
                            {
                                if (comp != null && IsValidUnityObject(comp))
                                {
                                    result.Add(comp);
                                }
                            }
                            if (result.Count > 0) return result.ToArray();
                        }
                    }

                    // Try using Il2CppType if available
                    if (_isIl2Cpp)
                    {
                        var il2cppTypeProperty = _componentType.GetProperty("Il2CppType", BindingFlags.Public | BindingFlags.Static);
                        if (il2cppTypeProperty != null)
                        {
                            var il2cppType = il2cppTypeProperty.GetValue(null);
                            if (il2cppType != null)
                            {
                                // Look for GetComponents that takes Il2CppType
                                var getComponentsMethods = goType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                                    .Where(m => m.Name == "GetComponents" && !m.IsGenericMethodDefinition)
                                    .ToList();

                                foreach (var method in getComponentsMethods)
                                {
                                    var parameters = method.GetParameters();
                                    if (parameters.Length == 1)
                                    {
                                        try
                                        {
                                            var compResult = method.Invoke(gameObject, new[] { il2cppType });
                                            if (compResult is IEnumerable compEnumerable)
                                            {
                                                foreach (var comp in compEnumerable)
                                                {
                                                    if (comp != null && IsValidUnityObject(comp))
                                                    {
                                                        result.Add(comp);
                                                    }
                                                }
                                                if (result.Count > 0) return result.ToArray();
                                            }
                                        }
                                        catch { }
                                    }
                                }
                            }
                        }
                    }
                }

                // Fallback: iterate through common component types
                // This is a last resort and may not get all components
                if (result.Count == 0)
                {
                    // At minimum, try to get the Transform component
                    var transform = GetProperty(gameObject, "transform");
                    if (transform != null && IsValidUnityObject(transform))
                    {
                        result.Add(transform);
                    }
                }
            }
            catch (Exception ex)
            {
                MelonMCPPlugin.Logger?.Warning($"Failed to get components: {ex.Message}");
            }

            return result.ToArray();
        }

        /// <summary>
        /// Get a specific component by type name
        /// </summary>
        public static object GetComponent(object gameObject, string typeName)
        {
            try
            {
                if (gameObject == null) return null;

                var goType = gameObject.GetType();

                // Try GetComponent<T> with the resolved type
                var componentType = ResolveUnityType(typeName);
                if (componentType != null)
                {
                    // Try generic GetComponent<T>
                    var genericMethod = goType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name == "GetComponent" && m.IsGenericMethodDefinition &&
                                           m.GetParameters().Length == 0);

                    if (genericMethod != null)
                    {
                        try
                        {
                            var typedMethod = genericMethod.MakeGenericMethod(componentType);
                            var component = typedMethod.Invoke(gameObject, null);
                            if (component != null && IsValidUnityObject(component))
                            {
                                return component;
                            }
                        }
                        catch { }
                    }
                }

                // Try GetComponent(Type) method
                if (componentType != null)
                {
                    var getCompMethod = goType.GetMethod("GetComponent",
                        BindingFlags.Public | BindingFlags.Instance,
                        null,
                        new[] { typeof(Type) },
                        null);

                    if (getCompMethod != null)
                    {
                        var component = getCompMethod.Invoke(gameObject, new object[] { componentType });
                        if (component != null && IsValidUnityObject(component))
                        {
                            return component;
                        }
                    }
                }

                // Try GetComponent(string) method
                var getCompByNameMethod = goType.GetMethod("GetComponent",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(string) },
                    null);

                if (getCompByNameMethod != null)
                {
                    var component = getCompByNameMethod.Invoke(gameObject, new object[] { typeName });
                    if (component != null && IsValidUnityObject(component))
                    {
                        return component;
                    }
                }
            }
            catch (Exception ex)
            {
                MelonMCPPlugin.Logger?.Warning($"Failed to get component {typeName}: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Resolve a type name to a Unity type
        /// </summary>
        private static Type ResolveUnityType(string typeName)
        {
            // Check cache or common types first
            if (typeName == "Transform" || typeName == "UnityEngine.Transform") return _transformType;
            if (typeName == "Component" || typeName == "UnityEngine.Component") return _componentType;
            if (typeName == "Behaviour" || typeName == "UnityEngine.Behaviour") return _behaviourType;
            if (typeName == "GameObject" || typeName == "UnityEngine.GameObject") return _gameObjectType;

            // Search all assemblies for the type
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (asm.IsDynamic) continue;

                    // Try exact name first
                    var type = asm.GetType(typeName, false);
                    if (type != null) return type;

                    // Try with UnityEngine prefix
                    if (!typeName.Contains("."))
                    {
                        type = asm.GetType("UnityEngine." + typeName, false);
                        if (type != null) return type;
                    }
                }
                catch { }
            }

            return null;
        }

        public static bool SetBehaviourEnabled(object behaviour, bool enabled)
        {
            try
            {
                if (behaviour == null) return false;

                // Check if it's a Behaviour
                if (_behaviourType != null && _behaviourType.IsInstanceOfType(behaviour))
                {
                    SetProperty(behaviour, "enabled", enabled);
                    return true;
                }
            }
            catch (Exception ex)
            {
                MelonMCPPlugin.Logger?.Warning($"Failed to set behaviour enabled: {ex.Message}");
            }

            return false;
        }

        public static Dictionary<string, object> InspectObject(object obj, bool includePrivate = false)
        {
            var result = new Dictionary<string, object>();

            if (obj == null)
            {
                result["error"] = "Object is null";
                return result;
            }

            try
            {
                var type = obj.GetType();
                result["type"] = type.FullName;
                result["assembly"] = type.Assembly.GetName().Name;

                // Get instance ID if Unity object
                if (_objectType != null && _objectType.IsInstanceOfType(obj))
                {
                    result["instanceId"] = InvokeMethod(obj, "GetInstanceID");
                    result["name"] = GetProperty(obj, "name");
                }

                var flags = BindingFlags.Public | BindingFlags.Instance;
                if (includePrivate)
                {
                    flags |= BindingFlags.NonPublic;
                }

                // Properties
                var properties = new Dictionary<string, object>();
                foreach (var prop in type.GetProperties(flags))
                {
                    try
                    {
                        if (prop.CanRead && prop.GetIndexParameters().Length == 0)
                        {
                            var value = prop.GetValue(obj);
                            properties[prop.Name] = FormatValue(value, prop.PropertyType);
                        }
                    }
                    catch (Exception ex)
                    {
                        properties[prop.Name] = $"<error: {ex.Message}>";
                    }
                }
                result["properties"] = properties;

                // Fields
                var fields = new Dictionary<string, object>();
                foreach (var field in type.GetFields(flags))
                {
                    try
                    {
                        var value = field.GetValue(obj);
                        fields[field.Name] = FormatValue(value, field.FieldType);
                    }
                    catch (Exception ex)
                    {
                        fields[field.Name] = $"<error: {ex.Message}>";
                    }
                }
                result["fields"] = fields;

                // Methods (signatures only)
                var methods = new List<string>();
                foreach (var method in type.GetMethods(flags))
                {
                    if (!method.IsSpecialName) // Skip property accessors
                    {
                        var paramStr = string.Join(", ", method.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                        methods.Add($"{method.ReturnType.Name} {method.Name}({paramStr})");
                    }
                }
                result["methods"] = methods;
            }
            catch (Exception ex)
            {
                result["error"] = ex.Message;
            }

            return result;
        }

        private static object FormatValue(object value, Type type)
        {
            if (value == null) return null;

            // Handle primitives directly
            if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal))
            {
                return value;
            }

            // Handle enums
            if (type.IsEnum)
            {
                return value.ToString();
            }

            // Handle Unity vectors, quaternions, colors
            var typeName = type.Name;
            if (typeName.StartsWith("Vector") || typeName == "Quaternion" || typeName == "Color" || typeName == "Color32")
            {
                return value.ToString();
            }

            // For complex types, just return type name and string representation
            return new Dictionary<string, object>
            {
                ["type"] = type.Name,
                ["value"] = value.ToString()
            };
        }

        #endregion

        #region Reflection Helpers

        public static object GetProperty(object obj, string propertyName)
        {
            try
            {
                var prop = obj?.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                return prop?.GetValue(obj);
            }
            catch
            {
                return null;
            }
        }

        public static void SetProperty(object obj, string propertyName, object value)
        {
            try
            {
                var prop = obj?.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                prop?.SetValue(obj, value);
            }
            catch (Exception ex)
            {
                MelonMCPPlugin.Logger?.Warning($"Failed to set property {propertyName}: {ex.Message}");
            }
        }

        public static object GetStaticProperty(Type type, string propertyName)
        {
            try
            {
                var prop = type?.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static);
                return prop?.GetValue(null);
            }
            catch
            {
                return null;
            }
        }

        public static void SetStaticProperty(Type type, string propertyName, object value)
        {
            try
            {
                var prop = type?.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static);
                prop?.SetValue(null, value);
            }
            catch (Exception ex)
            {
                MelonMCPPlugin.Logger?.Warning($"Failed to set static property {propertyName}: {ex.Message}");
            }
        }

        /// <summary>
        /// Sets Application.runInBackground to allow MCP tools to work when game is unfocused
        /// </summary>
        public static void SetRunInBackground(bool value)
        {
            try
            {
                // Try direct IL2CPP access first (works better in IL2CPP games)
                var prop = _applicationtype?.GetProperty("runInBackground", BindingFlags.Public | BindingFlags.Static);
                if (prop != null && prop.CanWrite)
                {
                    prop.SetValue(null, value);
                    MelonMCPPlugin.Logger?.Msg($"Set runInBackground via property setter: {value}");
                    return;
                }

                // Try finding the setter method directly (IL2CPP sometimes exposes set_PropertyName)
                var setterMethod = _applicationtype?.GetMethod("set_runInBackground", BindingFlags.Public | BindingFlags.Static);
                if (setterMethod != null)
                {
                    setterMethod.Invoke(null, new object[] { value });
                    MelonMCPPlugin.Logger?.Msg($"Set runInBackground via setter method: {value}");
                    return;
                }

                // Fallback: try using UnhollowerBaseLib/Il2CppInterop if available
                var il2cppAppType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                    .FirstOrDefault(t => t.FullName == "UnityEngine.Application" && t.Assembly.GetName().Name.Contains("Il2Cpp"));

                if (il2cppAppType != null)
                {
                    var il2cppProp = il2cppAppType.GetProperty("runInBackground", BindingFlags.Public | BindingFlags.Static);
                    if (il2cppProp != null && il2cppProp.CanWrite)
                    {
                        il2cppProp.SetValue(null, value);
                        MelonMCPPlugin.Logger?.Msg($"Set runInBackground via Il2Cpp type: {value}");
                        return;
                    }
                }

                MelonMCPPlugin.Logger?.Warning("Could not find a way to set runInBackground");
            }
            catch (Exception ex)
            {
                MelonMCPPlugin.Logger?.Warning($"Failed to set runInBackground: {ex.Message}");
            }
        }

        public static object InvokeMethod(object obj, string methodName, object[] parameters = null)
        {
            try
            {
                var method = obj?.GetType().GetMethod(methodName,
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    parameters?.Select(p => p?.GetType() ?? typeof(object)).ToArray() ?? Type.EmptyTypes,
                    null);
                return method?.Invoke(obj, parameters);
            }
            catch
            {
                return null;
            }
        }

        public static object InvokeStaticMethod(Type type, string methodName, object[] parameters = null)
        {
            try
            {
                var method = type?.GetMethod(methodName,
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    parameters?.Select(p => p?.GetType() ?? typeof(object)).ToArray() ?? Type.EmptyTypes,
                    null);
                return method?.Invoke(null, parameters);
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region Screenshot

        public static byte[] CaptureScreenshot()
        {
            try
            {
                if (_texture2DType == null || _screenType == null) return null;

                var width = (int)GetStaticProperty(_screenType, "width");
                var height = (int)GetStaticProperty(_screenType, "height");

                // For IL2CPP, we need to use the proper constructors
                object texture = null;

                if (_isIl2Cpp)
                {
                    // Try IL2CPP constructor with (int, int) - simplest
                    var ctors = _texture2DType.GetConstructors();
                    foreach (var ctor in ctors)
                    {
                        var parameters = ctor.GetParameters();

                        // Try (int width, int height)
                        if (parameters.Length == 2 &&
                            parameters[0].ParameterType == typeof(int) &&
                            parameters[1].ParameterType == typeof(int))
                        {
                            try
                            {
                                texture = ctor.Invoke(new object[] { width, height });
                                break;
                            }
                            catch { }
                        }
                    }

                    // Try with TextureFormat
                    if (texture == null && _textureFormatType != null)
                    {
                        foreach (var ctor in ctors)
                        {
                            var parameters = ctor.GetParameters();
                            if (parameters.Length >= 3 &&
                                parameters[0].ParameterType == typeof(int) &&
                                parameters[1].ParameterType == typeof(int) &&
                                parameters[2].ParameterType == _textureFormatType)
                            {
                                try
                                {
                                    var rgb24 = Enum.Parse(_textureFormatType, "RGB24");
                                    if (parameters.Length == 3)
                                    {
                                        texture = ctor.Invoke(new object[] { width, height, rgb24 });
                                    }
                                    else if (parameters.Length == 4)
                                    {
                                        texture = ctor.Invoke(new object[] { width, height, rgb24, false });
                                    }
                                    if (texture != null) break;
                                }
                                catch { }
                            }
                        }
                    }
                }
                else
                {
                    // Mono - use standard constructor
                    texture = Activator.CreateInstance(_texture2DType, new object[] { width, height });
                }

                if (texture == null)
                {
                    throw new Exception($"Could not create Texture2D. Available constructors: {string.Join(", ", _texture2DType.GetConstructors().Select(c => $"({string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name))})"))}");
                }

                // ReadPixels
                var readMethod = _texture2DType.GetMethod("ReadPixels",
                    new[] { _rectType ?? typeof(object), typeof(int), typeof(int) });

                if (readMethod == null)
                {
                    readMethod = _texture2DType.GetMethods()
                        .FirstOrDefault(m => m.Name == "ReadPixels" && m.GetParameters().Length >= 3);
                }

                if (readMethod != null && _rectType != null)
                {
                    // Create Rect
                    object rect = null;
                    var rectCtors = _rectType.GetConstructors();
                    foreach (var ctor in rectCtors)
                    {
                        var parameters = ctor.GetParameters();
                        if (parameters.Length == 4 &&
                            parameters.All(p => p.ParameterType == typeof(float) || p.ParameterType == typeof(int)))
                        {
                            try
                            {
                                rect = ctor.Invoke(new object[] { 0f, 0f, (float)width, (float)height });
                                break;
                            }
                            catch { }
                        }
                    }

                    if (rect != null)
                    {
                        readMethod.Invoke(texture, new object[] { rect, 0, 0 });
                    }
                }

                // Apply
                var applyMethod = _texture2DType.GetMethod("Apply", Type.EmptyTypes);
                if (applyMethod == null)
                {
                    applyMethod = _texture2DType.GetMethods()
                        .FirstOrDefault(m => m.Name == "Apply" && m.GetParameters().Length == 0);
                }
                applyMethod?.Invoke(texture, null);

                // EncodeToPNG
                var encodeMethod = _texture2DType.GetMethod("EncodeToPNG");
                if (encodeMethod == null)
                {
                    // Try ImageConversion class (Unity 2017.3+)
                    var imageConversionType = AppDomain.CurrentDomain.GetAssemblies()
                        .SelectMany(a => { try { return a.GetExportedTypes(); } catch { return Array.Empty<Type>(); } })
                        .FirstOrDefault(t => t.FullName == "UnityEngine.ImageConversion");

                    if (imageConversionType != null)
                    {
                        encodeMethod = imageConversionType.GetMethod("EncodeToPNG",
                            BindingFlags.Public | BindingFlags.Static);
                    }
                }

                if (encodeMethod != null)
                {
                    if (encodeMethod.IsStatic)
                    {
                        return encodeMethod.Invoke(null, new[] { texture }) as byte[];
                    }
                    else
                    {
                        return encodeMethod.Invoke(texture, null) as byte[];
                    }
                }
            }
            catch (Exception ex)
            {
                MelonMCPPlugin.Logger?.Warning($"Failed to capture screenshot: {ex.Message}");
            }

            return null;
        }

        #endregion
    }
}
