using System;
using System.Diagnostics;
using System.Threading;

namespace MelonMCP.Server
{
    /// <summary>
    /// Detects whether the Unity main thread is still making progress, WITHOUT running any code on it.
    ///
    /// Why this exists: the MCP server runs on a thread-pool thread, but every tool that touches a
    /// Unity object is queued onto the main thread and waited on. When the main thread is wedged -
    /// a native infinite loop inside GameAssembly.dll, a deadlock, or a modal pump that never
    /// returns - those tools all sit on the queue and time out, and the caller cannot tell the
    /// difference between "the game is busy" and "the game will never answer again".
    ///
    /// The heartbeat is a counter incremented from MelonMod.OnUpdate(). A reader on any other thread
    /// can sample it twice and compare: if it has not moved, no frame has completed, which means the
    /// main thread is stuck somewhere inside the previous frame. That single bit is what separates
    /// "wait and retry" from "the process is gone, go look at it from the OS".
    ///
    /// This type deliberately holds no reference to Unity, and never calls into it.
    /// </summary>
    internal static class MainThreadWatchdog
    {
        /// <summary>Incremented once per completed main-thread frame. Read with Volatile.</summary>
        private static long _frameCounter;

        /// <summary>Environment.TickCount64 at the last observed heartbeat, for cross-thread reporting.</summary>
        private static long _lastBeatTicks;

        /// <summary>Monotonic marker used for stall durations. Stopwatch ticks, not wall clock.</summary>
        private static long _lastBeatTimestamp;

        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        /// <summary>Total frames observed since load. Useful as a coarse liveness signal.</summary>
        internal static long FrameCount => Volatile.Read(ref _frameCounter);

        /// <summary>
        /// Called from the Unity main thread once per frame. Must stay allocation-free and trivial:
        /// it runs inside the game's own update loop, and this mod is a debugger, not a feature.
        /// </summary>
        internal static void Beat()
        {
            Volatile.Write(ref _lastBeatTimestamp, Clock.ElapsedTicks);
            Volatile.Write(ref _lastBeatTicks, Environment.TickCount64);
            Interlocked.Increment(ref _frameCounter);
        }

        /// <summary>
        /// How long the main thread has been silent. Returns 0 while frames are still completing.
        /// Safe to call from any thread.
        /// </summary>
        internal static TimeSpan StallDuration()
        {
            long last = Volatile.Read(ref _lastBeatTimestamp);
            if (last == 0)
            {
                // No beat has ever been observed. That is not necessarily a stall: the game may still
                // be in early startup, before the first OnUpdate. Report zero and let the caller
                // combine this with FrameCount, which distinguishes "no frames yet" from "stopped".
                return TimeSpan.Zero;
            }

            long delta = Clock.ElapsedTicks - last;
            if (delta < 0) return TimeSpan.Zero;
            return TimeSpan.FromSeconds((double)delta / Stopwatch.Frequency);
        }

        /// <summary>Wall-clock time of the last observed frame, or null if none yet.</summary>
        internal static DateTime? LastBeatUtc()
        {
            long ticks = Volatile.Read(ref _lastBeatTicks);
            if (ticks == 0) return null;
            return DateTime.UtcNow.AddMilliseconds(-(Environment.TickCount64 - ticks));
        }

        /// <summary>
        /// Sample the counter across a window and decide whether the main thread is progressing.
        /// This is the authoritative check: it does not rely on the heartbeat timestamp being fresh,
        /// only on whether the counter advances while we watch.
        /// </summary>
        internal static MainThreadStatus Probe(int windowMs = 750)
        {
            long before = Volatile.Read(ref _frameCounter);
            long t0 = Clock.ElapsedTicks;
            Thread.Sleep(Math.Max(1, windowMs));
            long elapsed = Clock.ElapsedTicks - t0;
            long after = Volatile.Read(ref _frameCounter);

            var status = new MainThreadStatus
            {
                FramesBefore = before,
                FramesAfter = after,
                FramesDuringWindow = after - before,
                ProbedForMs = (long)((double)elapsed / Stopwatch.Frequency * 1000.0),
                StallSeconds = StallDuration().TotalSeconds,
                LastBeatUtc = LastBeatUtc()
            };

            status.IsProgressing = status.FramesDuringWindow > 0;

            if (before == 0 && after == 0)
            {
                status.Verdict = "no-frames-yet";
                status.Explanation = "No main-thread frame has been observed since load. The game is "
                    + "probably still starting up, or OnUpdate has not been reached.";
            }
            else if (status.IsProgressing)
            {
                status.Verdict = "running";
                status.Explanation = "The main thread is completing frames normally.";
            }
            else
            {
                status.Verdict = "stuck";
                status.Explanation = "The main thread completed ZERO frames during the probe window. "
                    + "Anything queued for the main thread (find_objects_of_type, evaluate_expression, "
                    + "watch_field, execute_csharp, ...) will time out until it recovers. Use the "
                    + "main-thread-free tools - read_logs, disasm, read_mem, resolve_jump, list_patches, "
                    + "hook_patch_info, list_assemblies, config tools - and pair them with the OS-side "
                    + "stack check described below.";
            }

            status.NativeHint = NativeHint();
            return status;
        }

        /// <summary>
        /// Best-effort pointer to the OS-level check, which works even when this process cannot answer
        /// at all. Linux-only strings are included as text rather than executed.
        /// </summary>
        private static string NativeHint()
        {
            try
            {
                var self = Process.GetCurrentProcess();
                return $"pid={self.Id}. If this reports stuck, dump the native stack from outside: "
                     + $"'gdb -p {self.Id} -batch -ex \"thread 1\" -ex \"info registers rip\"' "
                     + "or 'eu-stack -p " + self.Id + "'. A main thread inside GameAssembly.dll with a "
                     + "repeating RIP is a native infinite loop in game code.";
            }
            catch (Exception ex)
            {
                return "pid unavailable: " + ex.Message;
            }
        }

        /// <summary>Reset between loads so a hot reload does not inherit the previous counter.</summary>
        internal static void Reset()
        {
            Volatile.Write(ref _frameCounter, 0);
            Volatile.Write(ref _lastBeatTicks, 0);
            Volatile.Write(ref _lastBeatTimestamp, 0);
        }
    }

    /// <summary>Snapshot of main-thread liveness, shaped for direct serialization into a tool result.</summary>
    internal sealed class MainThreadStatus
    {
        public bool IsProgressing { get; set; }
        public long FramesBefore { get; set; }
        public long FramesAfter { get; set; }
        public long FramesDuringWindow { get; set; }
        public long ProbedForMs { get; set; }
        public double StallSeconds { get; set; }
        public DateTime? LastBeatUtc { get; set; }
        public string Verdict { get; set; }
        public string Explanation { get; set; }
        public string NativeHint { get; set; }
    }
}
