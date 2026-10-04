namespace WuMingPerformance.Patches;

internal static class BackgroundWorldTasks
{
	private sealed class WorkItem
	{
		internal string Name = "";

		internal System.Action Work = () =>
		{
		};

		internal bool WaitDaySignal;

		internal long SignalVersionAtSubmit;

		internal bool SelfLock;

		internal bool Quiet;
	}

	private static readonly System.Collections.Generic.Queue<WuMingPerformance.Patches.BackgroundWorldTasks.WorkItem> _queue = new System.Collections.Generic.Queue<WuMingPerformance.Patches.BackgroundWorldTasks.WorkItem>();

	private static readonly object _queueLock = new object();

	private static readonly System.Threading.AutoResetEvent _wake = new System.Threading.AutoResetEvent(initialState: false);

	private static System.Threading.Thread? _worker;

	private static volatile bool _workerAttached;

	private static int _running;

        private static long _daySignalVersion;

        /// <summary>
        /// Set to tell the worker loop to leave. Previously the loop was `while (true)` with no exit
        /// path at all, so the thread could only ever stop by the process being torn down under it -
        /// which is precisely the situation that leaves an IL2CPP attachment undetached (measured:
        /// the worker was still `attachedToIl2Cpp=True` at shutdown while the thread later vanished).
        /// </summary>
        private static volatile bool _shutdown;

        internal static bool HasRunning => System.Threading.Volatile.Read(ref _running) > 0;

        /// <summary>
        /// Stop the worker and wait for it to leave, so its IL2CPP attachment is released through
        /// Il2CppThreadScope.Dispose() instead of being abandoned when the process tears the thread
        /// down. Safe to call more than once.
        ///
        /// Bounded on purpose: if the worker is wedged inside a task, the wait gives up rather than
        /// hanging the shutdown itself. The timeout is logged so a failure to stop is never silent.
        /// </summary>
        internal static void ShutdownWorker(int timeoutMs = 3000)
        {
            System.Threading.Thread? worker = _worker;
            if (worker == null)
            {
                return;
            }

            _shutdown = true;
            try
            {
                _wake.Set();
            }
            catch (System.Exception ex)
            {
                MelonLoader.MelonLogger.Warning("[WorldAsync] 唤醒工作线程失败: " + ex.Message);
            }

            bool joined;
            try
            {
                joined = worker.Join(timeoutMs);
            }
            catch (System.Exception ex)
            {
                MelonLoader.MelonLogger.Warning("[WorldAsync] 等待工作线程退出异常: " + ex.Message);
                joined = false;
            }

            if (!joined)
            {
                MelonLoader.MelonLogger.Error($"[WorldAsync] 工作线程在 {timeoutMs}ms 内未退出（可能正卡在某个任务里）；"
                    + "此时 IL2CPP 附件无法释放，进程可能卡在退出阶段。");
            }
        }

        /// <summary>
        /// Reports the worker's live state. Added so the shutdown path has something to say even when
        /// no work is in flight - previously the quit handler logged NOTHING in that case, which made
        /// "nothing to wait for" and "this code never ran" indistinguishable in the log.
        /// </summary>
        internal static string DescribeWorker()
        {
            System.Threading.Thread? worker = _worker;
            if (worker == null)
            {
                return "worker=<never created>";
            }

            int queued;
            lock (_queueLock)
            {
                queued = _queue.Count;
            }

        // Every Thread property below can throw ThreadStateException once the thread has died -
        // IsBackground and ThreadState are NOT safe to read after termination, unlike IsAlive.
        // This used to guard only IsAlive, so the "worker stopped" log (emitted right after
        // ShutdownWorker) failed with "Thread is dead; state cannot be accessed" and reported an
        // ERROR while shutting down cleanly. A diagnostic must never be able to raise.
        string threadInfo;
        try
        {
            threadInfo = $"managedThreadId={worker.ManagedThreadId} "
                       + $"isBackground={worker.IsBackground} state={worker.ThreadState}";
        }
        catch (System.Exception ex)
        {
            // Fall back to the one field that stays readable on a dead thread.
            string aliveState;
            try
            {
                aliveState = worker.IsAlive ? "alive" : "dead";
            }
            catch (System.Exception inner)
            {
                aliveState = "unknown(" + inner.GetType().Name + ")";
            }

            threadInfo = $"managedThreadId=<unavailable: {ex.GetType().Name}> threadState={aliveState}";
        }

        return $"worker={(IsWorkerAlive(worker) ? "alive" : "dead")} {threadInfo} "
             + $"attachedToIl2Cpp={_workerAttached} queued={queued} running={System.Threading.Volatile.Read(ref _running)}";
    }

    /// <summary>
    /// IsAlive is the only Thread liveness member that is safe to touch unconditionally, but even it
    /// is wrapped here so that one failed read cannot propagate out of a logging call.
    /// </summary>
    private static bool IsWorkerAlive(System.Threading.Thread worker)
    {
        try
        {
            return worker.IsAlive;
        }
        catch
        {
            return false;
        }
    }
	internal static void Track()
	{
		System.Threading.Interlocked.Increment(ref _running);
	}

	internal static void Complete()
	{
		int num = System.Threading.Interlocked.Decrement(ref _running);
		if (num < 0)
		{
			System.Threading.Interlocked.CompareExchange(ref _running, 0, num);
			MelonLoader.MelonLogger.Error("[WorldAsync] 任务计数出现负值（Track/Complete 不配对），已钳回 0");
		}
	}

	internal static void NotifyMainThreadPassedForceStuff()
	{
		System.Threading.Interlocked.Increment(ref _daySignalVersion);
		_wake.Set();
	}

        internal static void Submit(string name, System.Action work, bool waitForDaySignal = false, bool quiet = false)
        {
            Enqueue(new WuMingPerformance.Patches.BackgroundWorldTasks.WorkItem
            {
                Name = name,
                Work = work,
                WaitDaySignal = waitForDaySignal,
                SignalVersionAtSubmit = (waitForDaySignal ? System.Threading.Interlocked.Read(ref _daySignalVersion) : 0),
                SelfLock = false,
                Quiet = quiet
            });
        }

        internal static void SubmitSelfLocked(string name, System.Action work)
        {
            Enqueue(new WuMingPerformance.Patches.BackgroundWorldTasks.WorkItem
            {
                Name = name,
                Work = work,
                WaitDaySignal = false,
                SignalVersionAtSubmit = 0L,
                SelfLock = true
            });
        }

        private static void Enqueue(WuMingPerformance.Patches.BackgroundWorldTasks.WorkItem item)
        {
            Track();
            lock (_queueLock)
            {
                // Once shutdown has begun, do not start a new worker: it would see _shutdown already
                // set and exit immediately, silently dropping the work instead of running it. Run the
                // item inline instead, so behaviour stays correct during teardown.
                if (_shutdown)
                {
                    Execute(item);
                    return;
                }

                _queue.Enqueue(item);
                if (_worker == null)
                {
                    _worker = new System.Threading.Thread(WorkerLoop)
                    {
                        IsBackground = true,
                        Name = "WuMingPerf-WorldWorker"
                    };
                    _worker.Start();
                }
            }
            _wake.Set();
        }
    private static void WorkerLoop()
    {
        // Added for diagnosing the exit hang: record the OS thread id so the managed worker can be
        // matched against the native thread list when the process is analysed from outside.
        MelonLoader.MelonLogger.Msg("[ShutdownTag] worker ENTER | managedThreadId="
            + System.Threading.Thread.CurrentThread.ManagedThreadId
            + $" name={System.Threading.Thread.CurrentThread.Name} "
            + $"isBackground={System.Threading.Thread.CurrentThread.IsBackground}");

        if (WuMingPerformance.Il2CppThreadScope.TryAttach(out var scope))
        {
            _workerAttached = true;
            // Recorded for the exit-diagnosis path; the attachment is now released on the way out
            // (see the finally below) instead of being abandoned when the process tears the thread
            // down underneath us.
            MelonLoader.MelonLogger.Msg("[ShutdownTag] worker attached to il2cpp");
        }
        else
        {
            _workerAttached = false;
            MelonLoader.MelonLogger.Error("[WorldAsync] 工作线程 attach il2cpp 失败，所有任务将回退主线程执行（退化）");
        }

        // The loop now has a way out. WaitOne previously blocked forever, so the thread could only
        // ever stop by being killed with the process, leaving the IL2CPP attachment (owns:true, and
        // never disposed on this path) behind.
        //
        // The timeout is what makes this robust: _shutdown is checked on every wake, so a Set() that
        // races with the loop's own dequeue can never be missed for longer than one interval.
        try
        {
            while (!_shutdown)
            {
                WuMingPerformance.Patches.BackgroundWorldTasks.WorkItem workItem;
                lock (_queueLock)
                {
                    workItem = ((_queue.Count > 0) ? _queue.Dequeue() : null);
                }
                if (workItem == null)
                {
                    _wake.WaitOne(200);
                }
                else
                {
                    Execute(workItem);
                }
            }
        }
        finally
        {
            // Releasing the attachment is the whole point of giving this loop an exit path: the
            // thread must leave the IL2CPP domain cleanly before the runtime starts tearing down.
            try
            {
                scope.Dispose();
                MelonLoader.MelonLogger.Msg("[ShutdownTag] worker detached from il2cpp and exiting");
            }
            catch (System.Exception ex)
            {
                MelonLoader.MelonLogger.Error("[WorldAsync] 工作线程 detach 失败: " + ex.Message);
            }
        }
    }

	private static void Execute(WuMingPerformance.Patches.BackgroundWorldTasks.WorkItem item)
	{
        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
        // Added for diagnosing the exit hang: shows which work item is in flight at any moment.
        // Bounded output - only items that are still running when the process is on its way out
        // are interesting, and this fires for every submission.
        MelonLoader.MelonLogger.Msg($"[ShutdownTag] worker BEGIN {item.Name} | "
            + $"waitDaySignal={item.WaitDaySignal} selfLock={item.SelfLock} quiet={item.Quiet}");
        long num = 0L;
		long num2 = 0L;
		long num3 = 0L;
		try
		{
			if (!_workerAttached)
			{
				PostMainThreadFallback(item.Name, item.Work);
				return;
			}
			if (item.WaitDaySignal)
			{
				while (System.Threading.Interlocked.Read(ref _daySignalVersion) <= item.SignalVersionAtSubmit)
				{
					if (stopwatch.ElapsedMilliseconds > 15000)
					{
						MelonLoader.MelonLogger.Warning("[WorldAsync] " + item.Name + " 等待过日信号超时 15s，直接执行（ForceStuff 被禁用或 ChangeDay 被打断）");
						break;
					}
					System.Threading.Thread.Sleep(10);
				}
				num = stopwatch.ElapsedMilliseconds;
			}
			long elapsedMilliseconds = stopwatch.ElapsedMilliseconds;
			while (WuMingPerformance.Patches.GameDataControllerSaveAsyncPatch.IsSaving)
			{
				System.Threading.Thread.Sleep(50);
			}
			num3 = stopwatch.ElapsedMilliseconds - elapsedMilliseconds;
			if (item.SelfLock)
			{
				WuMingPerformance.Patches.WorldAsyncContext.IsWorkerThread = true;
				try
				{
					item.Work();
				}
				finally
				{
					WuMingPerformance.Patches.WorldAsyncContext.IsWorkerThread = false;
				}
			}
			else
			{
				long elapsedMilliseconds2 = stopwatch.ElapsedMilliseconds;
				WuMingPerformance.NativeLockScope nativeLockScope = WuMingPerformance.NativeLockScope.TryEnter(Il2Cpp.GameController.lockObj, item.Name);
				num2 = stopwatch.ElapsedMilliseconds - elapsedMilliseconds2;
				if (nativeLockScope == null)
				{
					MelonLoader.MelonLogger.Error("[WorldAsync] " + item.Name + ": 获取原生 lockObj 失败，回退主线程执行（无锁，退化）");
					PostMainThreadFallback(item.Name, item.Work);
					return;
				}
				using (nativeLockScope)
				{
					WuMingPerformance.Patches.WorldAsyncContext.IsWorkerThread = true;
					try
					{
						item.Work();
					}
					finally
					{
						WuMingPerformance.Patches.WorldAsyncContext.IsWorkerThread = false;
					}
				}
			}
			if (WuMingPerformance.PerformanceConfig.DiagnosticsEnabled && !item.Quiet && stopwatch.ElapsedMilliseconds >= 5)
			{
				MelonLoader.MelonLogger.Msg($"[诊断] 后台任务 {item.Name}: 等信号 {num}ms + 等存档 {num3}ms + 等锁 {num2}ms + 执行 {stopwatch.ElapsedMilliseconds - num - num3 - num2}ms");
			}
		}
		catch (System.Exception ex)
		{
			MelonLoader.MelonLogger.Error("[WorldAsync] " + item.Name + " 后台执行失败: " + ex.Message + "\n" + ex.StackTrace);
		}
        finally
        {
            // Added: the counterpart to "worker BEGIN", so a task that starts but never finishes is
            // visible as a missing END line. During a hung exit that asymmetry is the whole signal.
            MelonLoader.MelonLogger.Msg($"[ShutdownTag] worker END {item.Name} | "
                + $"totalMs={stopwatch.ElapsedMilliseconds} waitSignalMs={num} waitSaveMs={num3} waitLockMs={num2}");
            Complete();
        }
	}

	private static void PostMainThreadFallback(string name, System.Action work)
	{
		Track();
		WuMingPerformance.MainThreadDispatcher.Post(() =>
		{
			try
			{
				work();
			}
			catch (System.Exception ex)
			{
				MelonLoader.MelonLogger.Error("[WorldAsync] " + name + " 主线程兜底执行失败: " + ex.Message + "\n" + ex.StackTrace);
			}
			finally
			{
				Complete();
			}
		});
	}
}
