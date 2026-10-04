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

        /// <summary>Set by OnDeinitializeMelon so per-frame work stops once the melon is unloaded.</summary>
        private bool _deinitialized = false;

        /// <summary>
        /// The Msg handler actually subscribed in SubscribeToAllLogEvents, kept so it can be removed
        /// again during teardown. Which one is used depends on the MelonLoader build, so it is
        /// recorded at subscribe time rather than guessed at unsubscribe time.
        /// </summary>
        private EventInfo _msgEvent = null;
        private Delegate _msgHandler = null;

        public MelonMCPPlugin()
        {
            Instance = this;
        }

        /// <summary>
        /// MelonLoader 0.7.x init callback. Replaces the obsolete OnApplicationStart(), which is
        /// marked [Obsolete(..., error: true)] in 0.7.3 and is scheduled for removal.
        ///
        /// OnInitializeMelon runs after MelonLoader has fully initialized, so Unity and game types
        /// are safe to touch here.
        /// </summary>
        public override void OnInitializeMelon()
        {
            _deinitialized = false;

            // Subscribe to log events
            SubscribeToAllLogEvents();

            // Learn the difference between "this assembly is being swapped out" and "the process is
            // ending". Set well before OnDeinitializeMelon (see _definiteQuit for the verified
            // ordering), so the teardown can choose to keep serving instead of shutting down.
            SubscribeToDefiniteQuit();

            LoggerInstance.Msg("MelonMCP Initializing...");

            try
            {
                // Apply Harmony patches to prevent games from pausing when unfocused
                // RunInBackgroundPatch.ApplyPatch();
                // GameManagerFocusPatch.ApplyPatch();

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

                // Do not leave a half-built server behind: if the port was already bound (e.g. a
                // previous instance was not torn down), the listener may exist but never accept.
                try { _server?.Stop(); } catch { }
                _server = null;
            }
        }

        /// <summary>
        /// Set when MelonLoader announces that the process is genuinely going away, as opposed to a
        /// hot-reload swapping this assembly out.
        ///
        /// This distinction is the whole point of the shutdown-survival behaviour. UnregisterInstance
        /// drives OnDeinitializeMelon in BOTH cases, and the mod historically treated them
        /// identically: it closed the listening socket, dropped the log buffer and stopped the
        /// watchdog. That is right for a reload (the port must be free before the new load binds it)
        /// and exactly wrong for an exit, because the reason to keep a debugger attached is the exit
        /// path itself - an IL2CPP teardown that never finishes because some mod left a thread
        /// attached to the domain.
        ///
        /// Verified ordering in MelonLoader 0.7.3 (decompiled):
        ///   SupportModule_From.DefiniteQuit() -> MelonEvents.OnApplicationDefiniteQuit.Invoke(),
        ///   and MelonAssembly subscribes OnApplicationQuit to that event, which calls
        ///   UnregisterMelons -> UnregisterInstance -> OnDeinitializeMelon.
        /// So this flag is always set BEFORE OnDeinitializeMelon runs; it never races.
        /// </summary>
        private static volatile bool _definiteQuit;

        /// <summary>
        /// MelonLoader teardown callback, invoked when the melon is unregistered - by shutdown, or by
        /// a hot-reload plugin swapping the assembly out.
        ///
        /// Everything this mod holds that outlives the assembly must be released here, or the reload
        /// leaks the port and pins the old assembly in memory. The exception is the real-exit path:
        /// see _definiteQuit. On a genuine quit the server and its buffers are deliberately kept
        /// alive, because the hang we are here to debug happens after this callback returns.
        ///
        /// Note: Harmony patches on the plugin's own HarmonyInstance are removed by MelonLoader
        /// itself (MelonBase.UnregisterInstance calls HarmonyInstance.UnpatchSelf() right after this
        /// callback), so they deliberately are not unpatch-ed here.
        /// </summary>
        public override void OnDeinitializeMelon()
        {
            _deinitialized = true;

            // On a genuine quit, deliberately leave the server listening. MelonLoader is about to
            // call Core.Quit(), and the process may then hang inside IL2CPP teardown - at which
            // point this server is the only remaining way to see why. Closing it here, as this
            // code used to do unconditionally, threw away the log buffer and the watchdog at
            // exactly the moment they became interesting.
            //
            // The cost is that the port stays bound until the process actually dies. That is
            // acceptable: nothing is going to load another copy of this mod into a dying process.
            if (_definiteQuit)
            {
                LoggerInstance?.Msg("MelonMCP is staying up during shutdown so a hung exit can still be "
                    + "diagnosed. Main-thread tools will stop responding once Unity tears down; "
                    + "read_logs, main_thread_status, disasm, read_mem, list_patches and the config "
                    + "tools keep working.");
                return;
            }

            // Release the listening socket first: this is what unblocks a hot reload.
            try
            {
                _server?.Stop();
            }
            catch (Exception ex)
            {
                LoggerInstance?.Warning($"Error while stopping MCP server: {ex.Message}");
            }
            _server = null;

            // Drop the static MelonLogger subscriptions. These are static events, so leaving them
            // attached keeps a delegate pointing at this (now unloaded) assembly and prevents the
            // AssemblyLoadContext from being collected.
            try
            {
                MelonLogger.WarningCallbackHandler -= OnLogWarning;
                MelonLogger.ErrorCallbackHandler -= OnLogError;
                UnsubscribeMsgHandler();
            }
            catch (Exception ex)
            {
                LoggerInstance?.Warning($"Error while unsubscribing log handlers: {ex.Message}");
            }

            UnityMainThreadDispatcher.Shutdown();

            // Active field watches hold references to game objects from this load; drop them so the
            // outgoing assembly is not kept alive and the new load does not inherit stale watches.
            FieldWatcher.Reset();

            lock (_logLock)
            {
                _logBuffer.Clear();
            }

            // Clear caches that hold reflected Types/PropertyInfos from this load.
            _applicationTypeCache = null;
            _runInBackgroundPropCache = null;
            _gameManagerType = null;
            _isPausedField = null;
            _gameManagerCacheAttempted = false;
            _hasEnabledRunInBackground = false;
            _runInBackgroundCheckCounter = 0;

            // Stop the heartbeat so a probe made after this load is torn down cannot mistake a stale
            // counter for a live main thread.
            MainThreadWatchdog.Reset();

            // Only clear the singleton if it still points at this instance; a newer load may have
            // already installed its own plugin object.
            if (ReferenceEquals(Instance, this))
            {
                Instance = null;
            }

            LoggerInstance?.Msg("MelonMCP Server stopped");
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
            // Main-thread liveness. Deliberately grouped with the log tools: these are the operations
            // that must keep working when the game itself has stopped responding.
            _server.RegisterTool(new MainThreadStatusToolDefinition());

            // Code execution tools
            _server.RegisterTool(new ExecuteCSharpToolDefinition());
            _server.RegisterTool(new EvaluateExpressionToolDefinition());

            // Unity inspection tools.
            //
            // find_game_object / list_components / inspect_component are DISABLED: under IL2CPP the
            // component list collapses to UnityEngine.Component proxies, so list_components reports
            // every component as "Component", inspect_component can never match a type name, and
            // find_game_object's instanceId path returns nothing. Exposing them only produces
            // misleading output, so they are not registered until the proxy collapse is fixed.
            // The classes remain in UnityInspectionTools.cs for that repair.
            _server.RegisterTool(new GetSceneInfoToolDefinition());
            _server.RegisterTool(new ListGameObjectsToolDefinition());
#if MELONMCP_ENABLE_BROKEN_INSPECTION_TOOLS
            _server.RegisterTool(new FindGameObjectToolDefinition());
            _server.RegisterTool(new ListComponentsToolDefinition());
            _server.RegisterTool(new InspectComponentToolDefinition());
#endif

            // MonoBehaviour control tools - DISABLED for the same IL2CPP proxy-collapse reason as
            // the inspection tools above: all three locate their target by matching
            // component.GetType().Name, which never matches because every component reports as
            // 'Component'. They therefore always answer "Component 'X' not found on GameObject".
            // SetBehaviourEnabled would work if the component could be found, so these come back
            // together with the inspection tools. Implementations are in MonoBehaviourControlTools.cs.
#if MELONMCP_ENABLE_BROKEN_INSPECTION_TOOLS
            _server.RegisterTool(new ToggleBehaviourToolDefinition());
            _server.RegisterTool(new SetPropertyToolDefinition());
            _server.RegisterTool(new InvokeMethodToolDefinition());
#endif

            // Game state tools
            _server.RegisterTool(new GetGameInfoToolDefinition());
            // take_screenshot is DISABLED: CaptureScreenshot fails on this IL2CPP/CoreCLR runtime
            // (Texture2D + ReadPixels path returns null before PNG encoding), so the tool could only
            // ever answer 'screenshot API may not be available'. Implementation stays in
            // GameStateTools.cs. Use execute_csharp for pixel/graphics inspection in the meantime.
#if MELONMCP_ENABLE_BROKEN_SCREENSHOT
            _server.RegisterTool(new TakeScreenshotToolDefinition());
#endif
            _server.RegisterTool(new GetTimeInfoToolDefinition());

            // Resource tools
            _server.RegisterTool(new ListAssembliesToolDefinition());
            _server.RegisterTool(new ListTypesToolDefinition());
            _server.RegisterTool(new GetTypeInfoToolDefinition());

            // Advanced tools.
            //
            // NOTE on the instanceId branch: instantiate_object / set_transform / destroy_object all
            // work when addressed by 'path', but their 'instanceId' argument routes through
            // UnityHelper.FindObjectByInstanceId, which depends on a non-generic FindObjectsOfType
            // reflection lookup that resolves to null under IL2CPP. Passing instanceId therefore
            // fails with 'GameObject not found'. The path form is unaffected.
            _server.RegisterTool(new FindObjectsOfTypeToolDefinition());
            _server.RegisterTool(new SetTimeScaleToolDefinition());
            _server.RegisterTool(new CursorControlToolDefinition());
            _server.RegisterTool(new LoadSceneToolDefinition());
            _server.RegisterTool(new InstantiateObjectToolDefinition());
            _server.RegisterTool(new CreatePrimitiveToolDefinition());
            _server.RegisterTool(new SetTransformToolDefinition());
            // inspect_material is DISABLED: it resolves the renderer via
            // UnityHelper.GetComponent(gameObject, "Renderer"), which is subject to the same IL2CPP
            // proxy collapse and always answers 'No renderer found on GameObject'. The rest of this
            // group works when addressed by path; only their instanceId branch is dead (see below).
#if MELONMCP_ENABLE_BROKEN_INSPECTION_TOOLS
            _server.RegisterTool(new InspectMaterialToolDefinition());
#endif
            _server.RegisterTool(new DestroyObjectToolDefinition());

            // Game knowledge tools
            _server.RegisterTool(new AddGameKnowledgeToolDefinition());
            _server.RegisterTool(new GetGameKnowledgeToolDefinition());
            _server.RegisterTool(new GetGameSummaryToolDefinition());
            _server.RegisterTool(new SetPseudocodePathToolDefinition());
            _server.RegisterTool(new SearchPseudocodeToolDefinition());
            _server.RegisterTool(new ReadPseudocodeFileToolDefinition());

            // Patch / hook debugging tools. These exist because "Harmony reported the patch applied"
            // does not mean it will fire, and because on IL2CPP the runtime bytes differ from disk.
            _server.RegisterTool(new HookPatchInfoToolDefinition());
            _server.RegisterTool(new ListPatchesToolDefinition());
            _server.RegisterTool(new DisasmToolDefinition());
            _server.RegisterTool(new ReadMemToolDefinition());
            _server.RegisterTool(new ResolveJumpToolDefinition());
            _server.RegisterTool(new WatchFieldToolDefinition());
            _server.RegisterTool(new UnwatchFieldToolDefinition());

            // Reads named fields across instances of a type. Distinct from the DISABLED inspection
            // tools: it never discovers members by reflection, it only reads the exact field paths
            // the caller names. That inversion is what keeps it working under IL2CPP.
            _server.RegisterTool(new InspectUnityObjectToolDefinition());

            // Configuration tools. Built on MelonPreferences (part of MelonLoader itself) rather
            // than on MelonPreferencesManager, so they work on any MelonLoader install and do not
            // require a human-facing in-game UI that an agent could not open anyway.
            _server.RegisterTool(new ListConfigsToolDefinition());
            _server.RegisterTool(new GetConfigToolDefinition());
            _server.RegisterTool(new SetConfigToolDefinition());
            _server.RegisterTool(new ResetConfigToolDefinition());

            LoggerInstance.Msg($"Registered {_server.ToolCount} MCP tools");
        }

        public override void OnUpdate()
        {
            // Once the melon is unloaded (hot reload, or shutdown) there is nothing left to pump.
            // OnUpdate is driven by MelonLoader's own update loop, which may still tick this object
            // briefly after Unregister; touching Unity here would resurrect state we just released.
            //
            // The heartbeat is the exception: it touches nothing but plain statics, and on a real
            // exit it is precisely what tells a reader that the main thread has stopped completing
            // frames. Skipping it during shutdown would make main_thread_status report a permanently
            // stale counter exactly when the answer matters.
            if (_deinitialized && !_definiteQuit) return;

            // Prove to any other thread that the main thread is still alive. This is the only
            // main-thread-side cost the watchdog adds, and it is three volatile writes.
            MainThreadWatchdog.Beat();

            // Everything below reaches into Unity or the dispatcher, so it must stay behind the
            // unload check: on a real exit the game is already tearing down.
            if (_deinitialized) return;

            // Prove to any other thread that the main thread is still alive. This is the only
            // main-thread-side cost the watchdog adds, and it is three volatile writes.
            MainThreadWatchdog.Beat();

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

            // Sample any active field watches. This has to happen on the main thread: the watches
            // read game state, and the game is mutating it in this same loop.
            FieldWatcher.Tick();

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

        /// <summary>
        /// Normal shutdown. MelonLoader runs OnDeinitializeMelon first, so by the time this fires the
        /// server is usually already stopped; MCPServer.Stop() is idempotent, and the null-guard
        /// below keeps this harmless either way.
        ///
        /// On a definite quit the server was deliberately left running (see _definiteQuit), and this
        /// callback fires before the engine finishes tearing down - so stopping here would undo that
        /// and close the port right before the hang we want to observe.
        /// </summary>
        public override void OnApplicationQuit()
        {
            if (_definiteQuit)
            {
                LoggerInstance?.Msg("MelonMCP: keeping the server up through application quit.");
                return;
            }

            _server?.Stop();
            LoggerInstance?.Msg("MelonMCP Server stopped");
        }

        #region Shutdown detection

        /// <summary>
        /// Subscribe to MelonLoader's quit events so the teardown can tell a real exit from a hot
        /// reload, and keep the server alive for the former.
        ///
        /// ORDERING IS LOAD-BEARING HERE, and getting it wrong fails silently.
        ///
        /// MelonAssembly subscribes its own OnApplicationQuit to OnApplicationDefiniteQuit at load
        /// time, and that callback runs UnregisterMelons -> UnregisterInstance -> OnDeinitializeMelon
        /// - i.e. it is the thing that tears this mod down. Our handler must therefore run BEFORE
        /// it, or the flag it sets arrives too late to be read.
        ///
        /// MelonEventBase orders callbacks by PRIORITY (verified against the shipped
        /// MelonLoader.dll, not inferred): Subscribe inserts an action before the first entry with a
        /// strictly greater priority, and otherwise appends. MelonAssembly subscribes with the
        /// default priority 0, so subscribing with priority 0 puts us AFTER it - which is exactly
        /// what happened, and why the keep-alive never engaged. A negative priority is inserted
        /// ahead of it.
        ///
        /// Both quit events are covered because they fire at different moments and either alone is
        /// enough to know the process is going away:
        ///   OnApplicationQuit         - the quit REQUEST (earliest; cancellable, so merely a hint)
        ///   OnApplicationDefiniteQuit - the COMMITMENT (fires just before Core.Quit())
        ///
        /// Failures are non-fatal on purpose: if neither event can be reached, the mod keeps the old
        /// (reload-safe) behaviour of releasing the port, which is the conservative choice.
        /// </summary>
        private void SubscribeToDefiniteQuit()
        {
            try
            {
                // priority:-1 places this ahead of MelonAssembly's default-priority callback.
                MelonEvents.OnApplicationQuit.Subscribe(OnDefiniteQuit, -1);
                MelonEvents.OnApplicationDefiniteQuit.Subscribe(OnDefiniteQuit, -1);
            }
            catch (Exception ex)
            {
                LoggerInstance?.Warning($"Could not subscribe to the quit events ({ex.Message}); "
                    + "the MCP server will shut down as usual on exit and will not be available to "
                    + "diagnose a hung shutdown.");
                return;
            }

            VerifyQuitSubscriptionOrder();
        }

        /// <summary>
        /// Confirm at startup that our quit handler really is ahead of MelonAssembly's.
        ///
        /// This exists because the failure mode it guards against is invisible: with equal
        /// priorities the mod still starts, still logs, and still shuts down - it just never gets
        /// the keep-alive, and the only symptom is a missing log line during an exit that is
        /// already going wrong. Reading the subscriber list back turns a silent ordering
        /// regression into a loud warning while the game is still healthy.
        private void VerifyQuitSubscriptionOrder()
        {
            try
            {
                // GetSubscribers returns the list in invocation order, which is exactly the order
                // Invoke() will walk. MelonAssembly's entries are created from within that assembly,
                // so identifying them by declaring assembly is enough and does not depend on the
                // shape of MelonAssembly's internals.
                var subscribers = MelonEvents.OnApplicationDefiniteQuit.GetSubscribers();
                if (subscribers == null || subscribers.Length == 0)
                {
                    LoggerInstance?.Warning("Quit-subscription self-check: subscriber list was empty; "
                        + "cannot confirm the keep-alive-on-exit path will run.");
                    return;
                }

                int ourIndex = -1;
                int assemblyIndex = -1;

                for (int i = 0; i < subscribers.Length; i++)
                {
                    var del = subscribers[i]?.del;
                    if (del == null) continue;

                    if (del.Method.DeclaringType == typeof(MelonMCPPlugin))
                    {
                        if (ourIndex < 0) ourIndex = i;
                    }
                    else if (del.Method.DeclaringType?.Assembly.GetName().Name == "MelonLoader")
                    {
                        if (assemblyIndex < 0) assemblyIndex = i;
                    }
                }

                if (ourIndex < 0)
                {
                    LoggerInstance?.Warning("Quit-subscription self-check: our quit handler is not in "
                        + "the subscriber list; the MCP server will not survive a hung shutdown.");
                    return;
                }

                if (assemblyIndex >= 0 && ourIndex > assemblyIndex)
                {
                    LoggerInstance?.Error("Quit-subscription self-check FAILED: our quit handler runs "
                        + $"after MelonLoader's teardown (index {ourIndex} vs {assemblyIndex}). The "
                        + "keep-alive-on-exit path will NOT trigger, so MCP will be gone by the time a "
                        + "hung shutdown happens. Expected priority to place us first.");
                    return;
                }

                // Success is logged deliberately: the whole reason this check exists is that the
                // failure it guards against used to be invisible, so a positive confirmation is
                // worth one line at startup.
                LoggerInstance?.Msg("Quit-subscription self-check passed: MCP will keep serving "
                    + "during a real quit so a hung shutdown can still be diagnosed.");
            }
            catch (Exception ex)
            {
                LoggerInstance?.Warning($"Quit-subscription self-check failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Runs before teardown. Must not touch Unity: at this point the engine is already tearing
        /// down, and the only job here is to record intent in a plain static field.
        /// </summary>
        private void OnDefiniteQuit()
        {
            _definiteQuit = true;
            try
            {
                // Goes through the normal log path so it is also visible in the MCP log buffer that
                // we are about to preserve - this is the line that explains why the port is still up.
                LoggerInstance?.Msg("MelonMCP: definite quit detected; server will remain available "
                    + "until the process exits.");
            }
            catch
            {
                // Logging during shutdown must never be able to break the shutdown path.
            }
        }

        #endregion

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
                        _msgEvent = msgDrawingEvent;
                        _msgHandler = handler;
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
                            _msgEvent = msgEvent;
                            _msgHandler = handler;
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

        /// <summary>
        /// Removes the reflectively-subscribed Msg handler. The event and delegate were recorded by
        /// SubscribeToAllLogEvents because the ConcreteDrawingCallbackHandler pair differs across
        /// MelonLoader builds, so the correct one cannot be inferred at teardown time.
        /// </summary>
        private void UnsubscribeMsgHandler()
        {
            if (_msgEvent == null || _msgHandler == null) return;

            try
            {
                _msgEvent.RemoveEventHandler(null, _msgHandler);
            }
            catch (Exception ex)
            {
                LoggerInstance?.Warning($"Could not unsubscribe from Msg logs: {ex.Message}");
            }
            finally
            {
                _msgEvent = null;
                _msgHandler = null;
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
    /// Harmony patch that blocks the game from turning runInBackground off.
    ///
    /// NOTE: this class deliberately carries NO [HarmonyPatch] attribute. The target
    /// (UnityEngine.Application.set_runInBackground) is resolved by reflection at runtime in
    /// ApplyPatch(), because the method is not statically referenceable in the IL2CPP proxy set.
    ///
    /// Adding a bare [HarmonyPatch] here makes MelonLoader's automatic HarmonyInit → PatchAll scan
    /// pick this class up and try to patch a method it cannot identify, which produces:
    ///   HarmonyException: Patching exception in method null
    ///    ---> ArgumentException: Undefined target method for patch method ...RunInBackgroundPatch::Prefix
    /// The failure is harmless (nothing was patched either way) but it logs an ERROR at every start.
    /// If this class ever needs to join automatic PatchAll, it must declare a concrete target via
    /// [HarmonyPatch(typeof(X), "Method")] and mark the methods [HarmonyPrefix].
    /// </summary>
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
