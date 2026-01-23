using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using MelonMCP.Server;
using MelonMCP.Tools;

[assembly: MelonInfo(typeof(MelonMCP.MelonMCPPlugin), MelonMCP.BuildInfo.Name, MelonMCP.BuildInfo.Version, MelonMCP.BuildInfo.Author, MelonMCP.BuildInfo.DownloadLink)]
[assembly: MelonGame(null, null)] // Universal - works with any game
[assembly: MelonColor(255, 0, 255, 255)] // Magenta

namespace MelonMCP
{
    public class MelonMCPPlugin : MelonMod
    {
        public static MelonMCPPlugin Instance { get; private set; }
        public static MelonLogger.Instance Logger => Instance?.LoggerInstance;

        private MCPServer _server;
        private readonly List<string> _logBuffer = new List<string>();
        private readonly object _logLock = new object();
        private const int MaxLogBufferSize = 1000;
        private bool _hasEnabledRunInBackground = false;
        private int _runInBackgroundCheckCounter = 0;
        private Type _applicationTypeCache = null;
        private PropertyInfo _runInBackgroundPropCache = null;

        // GameManager pause bypass
        private Type _gameManagerType = null;
        private FieldInfo _isPausedField = null;
        private bool _gameManagerCacheAttempted = false;

        public MelonMCPPlugin()
        {
            Instance = this;
        }

        public override void OnApplicationStart()
        {
            // Subscribe to log events
            SubscribeToAllLogEvents();

            LoggerInstance.Msg("MelonMCP Initializing...");

            try
            {
                // Apply Harmony patches to prevent games from pausing when unfocused
                RunInBackgroundPatch.ApplyPatch();
                GameManagerFocusPatch.ApplyPatch();

                // Initialize Unity main thread dispatcher
                UnityMainThreadDispatcher.Initialize();

                // Initialize MCP server
                int port = GetConfiguredPort();
                _server = new MCPServer(port);

                // Register all tools
                RegisterTools();

                // Start the server
                _server.Start();

                LoggerInstance.Msg($"MelonMCP Server started on port {port}");
                LoggerInstance.Msg($"Connect your MCP client to: tcp://localhost:{port}");
            }
            catch (Exception ex)
            {
                LoggerInstance.Error($"Failed to start MelonMCP server: {ex}");
            }
        }

        private int GetConfiguredPort()
        {
            // Could be made configurable via MelonPreferences
            return 27015; // Default port
        }

        private void RegisterTools()
        {
            // Log tools
            _server.RegisterTool(new ReadLogsToolDefinition());
            _server.RegisterTool(new ClearLogsToolDefinition());

            // Code execution tools
            _server.RegisterTool(new ExecuteCSharpToolDefinition());
            _server.RegisterTool(new EvaluateExpressionToolDefinition());

            // Unity inspection tools
            _server.RegisterTool(new GetSceneInfoToolDefinition());
            _server.RegisterTool(new ListGameObjectsToolDefinition());
            _server.RegisterTool(new FindGameObjectToolDefinition());
            _server.RegisterTool(new ListComponentsToolDefinition());
            _server.RegisterTool(new InspectComponentToolDefinition());

            // MonoBehaviour control tools
            _server.RegisterTool(new ToggleBehaviourToolDefinition());
            _server.RegisterTool(new SetPropertyToolDefinition());
            _server.RegisterTool(new InvokeMethodToolDefinition());

            // Game state tools
            _server.RegisterTool(new GetGameInfoToolDefinition());
            _server.RegisterTool(new TakeScreenshotToolDefinition());
            _server.RegisterTool(new GetTimeInfoToolDefinition());

            // Resource tools
            _server.RegisterTool(new ListAssembliesToolDefinition());
            _server.RegisterTool(new ListTypesToolDefinition());
            _server.RegisterTool(new GetTypeInfoToolDefinition());

            // Advanced tools
            _server.RegisterTool(new FindObjectsOfTypeToolDefinition());
            _server.RegisterTool(new SetTimeScaleToolDefinition());
            _server.RegisterTool(new CursorControlToolDefinition());
            _server.RegisterTool(new LoadSceneToolDefinition());
            _server.RegisterTool(new InstantiateObjectToolDefinition());
            _server.RegisterTool(new CreatePrimitiveToolDefinition());
            _server.RegisterTool(new SetTransformToolDefinition());
            _server.RegisterTool(new InspectMaterialToolDefinition());
            _server.RegisterTool(new DestroyObjectToolDefinition());

            // Game knowledge tools
            _server.RegisterTool(new AddGameKnowledgeToolDefinition());
            _server.RegisterTool(new GetGameKnowledgeToolDefinition());
            _server.RegisterTool(new GetGameSummaryToolDefinition());
            _server.RegisterTool(new SetPseudocodePathToolDefinition());
            _server.RegisterTool(new SearchPseudocodeToolDefinition());
            _server.RegisterTool(new ReadPseudocodeFileToolDefinition());

            LoggerInstance.Msg($"Registered {_server.ToolCount} MCP tools");
        }

        public override void OnUpdate()
        {
            // Process queued actions on the main thread
            UnityMainThreadDispatcher.ProcessQueue();

            // Keep checking and enforcing runInBackground for the first ~5 seconds
            // Games often reset this value during their own initialization
            _runInBackgroundCheckCounter++;
            if (_runInBackgroundCheckCounter <= 300) // ~5 seconds at 60fps
            {
                if (_runInBackgroundCheckCounter % 60 == 1) // Check once per second
                {
                    EnsureRunInBackground();
                }
            }

            // Continuously bypass GameManager pause (for games like TLD that pause on focus loss)
            EnsureNotPaused();
        }

        private void EnsureNotPaused()
        {
            try
            {
                // Cache GameManager type on first call
                if (!_gameManagerCacheAttempted)
                {
                    _gameManagerCacheAttempted = true;
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        try
                        {
                            if (asm.IsDynamic) continue;

                            // Look for GameManager (common in many Unity games)
                            var type = asm.GetType("GameManager", false);
                            if (type == null)
                            {
                                // Try Il2Cpp namespace
                                type = asm.GetType("Il2Cpp.GameManager", false);
                            }

                            if (type != null)
                            {
                                _gameManagerType = type;

                                // Find m_IsPaused field
                                _isPausedField = type.GetField("m_IsPaused",
                                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);

                                if (_isPausedField != null)
                                {
                                    LoggerInstance.Msg($"Found GameManager.m_IsPaused field (static: {_isPausedField.IsStatic})");
                                }
                                break;
                            }
                        }
                        catch { }
                    }
                }

                // Reset m_IsPaused to false if we found the field
                if (_isPausedField != null && _isPausedField.IsStatic)
                {
                    var currentValue = _isPausedField.GetValue(null);
                    if (currentValue is bool isPaused && isPaused)
                    {
                        _isPausedField.SetValue(null, false);
                    }
                }
            }
            catch { }
        }

        private void EnsureRunInBackground()
        {
            try
            {
                // Cache the Application type and property on first call
                if (_applicationTypeCache == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        try
                        {
                            if (asm.IsDynamic) continue;
                            var type = asm.GetType("UnityEngine.Application", false);
                            if (type != null)
                            {
                                _applicationTypeCache = type;
                                break;
                            }
                        }
                        catch { }
                    }
                }

                if (_applicationTypeCache == null) return;

                // Cache the property
                if (_runInBackgroundPropCache == null)
                {
                    _runInBackgroundPropCache = _applicationTypeCache.GetProperty("runInBackground", BindingFlags.Public | BindingFlags.Static);
                }

                if (_runInBackgroundPropCache == null || !_runInBackgroundPropCache.CanWrite) return;

                // Check current value and set if needed
                var currentValue = (bool)_runInBackgroundPropCache.GetValue(null);
                if (!currentValue)
                {
                    _runInBackgroundPropCache.SetValue(null, true);
                    LoggerInstance.Msg($"Set runInBackground = true (was {currentValue})");
                }
                else if (!_hasEnabledRunInBackground)
                {
                    _hasEnabledRunInBackground = true;
                    LoggerInstance.Msg("runInBackground is already enabled");
                }
            }
            catch (Exception ex)
            {
                LoggerInstance.Warning($"Failed to set runInBackground: {ex.Message}");
            }
        }

        public override void OnApplicationQuit()
        {
            _server?.Stop();
            LoggerInstance.Msg("MelonMCP Server stopped");
        }

        #region Log Capture

        private void SubscribeToAllLogEvents()
        {
            // Subscribe to Warning and Error (these are straightforward)
            MelonLogger.WarningCallbackHandler += OnLogWarning;
            MelonLogger.ErrorCallbackHandler += OnLogError;

            // For Msg logs, we need to use reflection because MsgDrawingCallbackHandler
            // uses ColorARGB which may not be accessible in some builds
            try
            {
                // Try to subscribe to MsgDrawingCallbackHandler via reflection
                var loggerType = typeof(MelonLogger);
                var msgDrawingEvent = loggerType.GetEvent("MsgDrawingCallbackHandler", BindingFlags.Public | BindingFlags.Static);

                if (msgDrawingEvent != null)
                {
                    // Get the event handler type
                    var handlerType = msgDrawingEvent.EventHandlerType;

                    // Create a delegate that matches the handler signature
                    // The signature is: (ColorARGB melonColor, ColorARGB txtColor, string name, string msg)
                    var method = typeof(MelonMCPPlugin).GetMethod(nameof(OnLogMessageReflection), BindingFlags.NonPublic | BindingFlags.Instance);
                    if (method != null)
                    {
                        var handler = Delegate.CreateDelegate(handlerType, this, method);
                        msgDrawingEvent.AddEventHandler(null, handler);
                        LoggerInstance.Msg("Successfully subscribed to MsgDrawingCallbackHandler");
                    }
                }
                else
                {
                    // Fall back to MsgCallbackHandler (deprecated but may work)
                    var msgEvent = loggerType.GetEvent("MsgCallbackHandler", BindingFlags.Public | BindingFlags.Static);
                    if (msgEvent != null)
                    {
                        var method = typeof(MelonMCPPlugin).GetMethod(nameof(OnLogMessageSimple), BindingFlags.NonPublic | BindingFlags.Instance);
                        if (method != null)
                        {
                            var handlerType = msgEvent.EventHandlerType;
                            var handler = Delegate.CreateDelegate(handlerType, this, method);
                            msgEvent.AddEventHandler(null, handler);
                            LoggerInstance.Msg("Successfully subscribed to MsgCallbackHandler (deprecated)");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LoggerInstance.Warning($"Could not subscribe to Msg logs via reflection: {ex.Message}");
            }
        }

        // Handler for MsgDrawingCallbackHandler - signature: (ColorARGB, ColorARGB, string, string)
        // We use object for ColorARGB since it may not be accessible
        private void OnLogMessageReflection(object melonColor, object txtColor, string name, string msg)
        {
            AddToLogBuffer($"[{DateTime.Now:HH:mm:ss}] [MSG] [{name}] {msg}");
        }

        // Handler for MsgCallbackHandler (deprecated) - signature: (string, string)
        private void OnLogMessageSimple(string name, string msg)
        {
            AddToLogBuffer($"[{DateTime.Now:HH:mm:ss}] [MSG] [{name}] {msg}");
        }

        private void OnLogWarning(string name, string msg)
        {
            AddToLogBuffer($"[{DateTime.Now:HH:mm:ss}] [WARNING] [{name}] {msg}");
        }

        private void OnLogError(string name, string msg)
        {
            AddToLogBuffer($"[{DateTime.Now:HH:mm:ss}] [ERROR] [{name}] {msg}");
        }

        private void AddToLogBuffer(string log)
        {
            lock (_logLock)
            {
                _logBuffer.Add(log);
                if (_logBuffer.Count > MaxLogBufferSize)
                {
                    _logBuffer.RemoveAt(0);
                }
            }
        }

        public List<string> GetLogs(int count = 100, string filter = null)
        {
            lock (_logLock)
            {
                var logs = new List<string>();
                int start = Math.Max(0, _logBuffer.Count - count);

                for (int i = start; i < _logBuffer.Count; i++)
                {
                    if (string.IsNullOrEmpty(filter) || _logBuffer[i].Contains(filter, StringComparison.OrdinalIgnoreCase))
                    {
                        logs.Add(_logBuffer[i]);
                    }
                }

                return logs;
            }
        }

        public void ClearLogs()
        {
            lock (_logLock)
            {
                _logBuffer.Clear();
            }
        }

        #endregion
    }

    /// <summary>
    /// Harmony patch to prevent games from disabling runInBackground
    /// This intercepts Application.set_runInBackground and blocks attempts to set it to false
    /// </summary>
    [HarmonyPatch]
    public static class RunInBackgroundPatch
    {
        private static bool _patchApplied = false;

        public static void ApplyPatch()
        {
            if (_patchApplied) return;

            try
            {
                // Find UnityEngine.Application.set_runInBackground
                Type appType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        if (asm.IsDynamic) continue;
                        var type = asm.GetType("UnityEngine.Application", false);
                        if (type != null)
                        {
                            appType = type;
                            break;
                        }
                    }
                    catch { }
                }

                if (appType == null)
                {
                    MelonMCPPlugin.Logger?.Warning("Could not find UnityEngine.Application for patching");
                    return;
                }

                var setter = appType.GetMethod("set_runInBackground", BindingFlags.Public | BindingFlags.Static);
                if (setter == null)
                {
                    MelonMCPPlugin.Logger?.Warning("Could not find set_runInBackground method");
                    return;
                }

                var prefix = typeof(RunInBackgroundPatch).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic);
                MelonMCPPlugin.Instance.HarmonyInstance.Patch(setter, new HarmonyMethod(prefix));

                _patchApplied = true;
                MelonMCPPlugin.Logger?.Msg("Patched Application.set_runInBackground to prevent game from disabling it");

                // Now set it to true (this will bypass our patch since we allow setting to true)
                var prop = appType.GetProperty("runInBackground", BindingFlags.Public | BindingFlags.Static);
                if (prop != null && prop.CanWrite)
                {
                    prop.SetValue(null, true);
                    MelonMCPPlugin.Logger?.Msg("Set runInBackground = true");
                }
            }
            catch (Exception ex)
            {
                MelonMCPPlugin.Logger?.Warning($"Failed to patch set_runInBackground: {ex.Message}");
            }
        }

        private static bool Prefix(bool value)
        {
            // Block any attempt to set runInBackground to false
            if (!value)
            {
                MelonMCPPlugin.Logger?.Msg("Blocked attempt to disable runInBackground");
                return false; // Skip original method
            }
            return true; // Allow setting to true
        }
    }

    /// <summary>
    /// Harmony patch to prevent GameManager from pausing when focus is lost
    /// This is needed for games like The Long Dark that have multiple pause-on-focus systems
    /// </summary>
    public static class GameManagerFocusPatch
    {
        private static bool _patchApplied = false;

        public static void ApplyPatch()
        {
            if (_patchApplied) return;

            try
            {
                // Find GameManager type
                Type gameManagerType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        if (asm.IsDynamic) continue;
                        var type = asm.GetType("GameManager", false);
                        if (type == null)
                        {
                            type = asm.GetType("Il2Cpp.GameManager", false);
                        }
                        if (type != null)
                        {
                            gameManagerType = type;
                            break;
                        }
                    }
                    catch { }
                }

                if (gameManagerType == null)
                {
                    MelonMCPPlugin.Logger?.Msg("GameManager not found - focus patch not needed");
                    return;
                }

                // Patch OnApplicationFocus
                var onFocusMethod = gameManagerType.GetMethod("OnApplicationFocus",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (onFocusMethod != null)
                {
                    var prefix = typeof(GameManagerFocusPatch).GetMethod(nameof(OnApplicationFocusPrefix),
                        BindingFlags.Static | BindingFlags.NonPublic);
                    MelonMCPPlugin.Instance.HarmonyInstance.Patch(onFocusMethod, new HarmonyMethod(prefix));
                    MelonMCPPlugin.Logger?.Msg("Patched GameManager.OnApplicationFocus");
                }

                // Patch PauseWhenFocusLost
                var pauseFocusMethod = gameManagerType.GetMethod("PauseWhenFocusLost",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);

                if (pauseFocusMethod != null)
                {
                    var prefix = typeof(GameManagerFocusPatch).GetMethod(nameof(PauseWhenFocusLostPrefix),
                        BindingFlags.Static | BindingFlags.NonPublic);
                    MelonMCPPlugin.Instance.HarmonyInstance.Patch(pauseFocusMethod, new HarmonyMethod(prefix));
                    MelonMCPPlugin.Logger?.Msg("Patched GameManager.PauseWhenFocusLost");
                }

                _patchApplied = true;
            }
            catch (Exception ex)
            {
                MelonMCPPlugin.Logger?.Warning($"Failed to patch GameManager focus methods: {ex.Message}");
            }
        }

        // Skip OnApplicationFocus when losing focus (hasFocus = false)
        private static bool OnApplicationFocusPrefix(bool hasFocus)
        {
            if (!hasFocus)
            {
                // Skip the original method when losing focus - this prevents pause
                return false;
            }
            return true; // Allow regaining focus to proceed normally
        }

        // Skip PauseWhenFocusLost entirely
        private static bool PauseWhenFocusLostPrefix()
        {
            return false; // Never pause when focus is lost
        }
    }
}
