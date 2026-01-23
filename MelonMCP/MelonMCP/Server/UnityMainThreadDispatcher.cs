using System;
using System.Collections.Concurrent;

namespace MelonMCP.Server
{
    /// <summary>
    /// Dispatches actions to the Unity main thread
    /// Unity objects can only be accessed from the main thread
    /// </summary>
    public static class UnityMainThreadDispatcher
    {
        private static readonly ConcurrentQueue<Action> _executionQueue = new ConcurrentQueue<Action>();
        private static bool _initialized = false;

        /// <summary>
        /// Initialize the dispatcher
        /// </summary>
        public static void Initialize()
        {
            _initialized = true;
        }

        /// <summary>
        /// Queue an action to be executed on the Unity main thread
        /// </summary>
        public static void Enqueue(Action action)
        {
            if (action == null) return;
            _executionQueue.Enqueue(action);
        }

        /// <summary>
        /// Process the queue - should be called from MelonMod.OnUpdate()
        /// </summary>
        public static void ProcessQueue()
        {
            // Process all queued actions
            int processed = 0;
            while (_executionQueue.TryDequeue(out var action) && processed < 100)
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    MelonMCPPlugin.Logger?.Error($"Error executing queued action: {ex}");
                }
                processed++;
            }
        }
    }
}
