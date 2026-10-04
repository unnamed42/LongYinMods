namespace WuMingPerformance;

public sealed class WuMingPerformanceMod : MelonLoader.MelonMod
{
	private float _hitchWindowMax;

	private float _hitchWindowStart;

	public override void OnInitializeMelon()
	{
		WuMingPerformance.PerformanceConfig.Initialize();
		MelonLoader.MelonLogger.Msg("[性能优化] 已加载 | " + WuMingPerformance.PerformanceConfig.Describe());
	}

	public override void OnUpdate()
	{
		WuMingPerformance.MainThreadDispatcher.Drain();
		TrackFrameHitches();
	}

	private void TrackFrameHitches()
	{
		if (!WuMingPerformance.PerformanceConfig.DiagnosticsEnabled)
		{
			return;
		}
		float unscaledDeltaTime = UnityEngine.Time.unscaledDeltaTime;
		float realtimeSinceStartup = UnityEngine.Time.realtimeSinceStartup;
		if (unscaledDeltaTime > _hitchWindowMax)
		{
			_hitchWindowMax = unscaledDeltaTime;
		}
		if (realtimeSinceStartup - _hitchWindowStart >= 1f)
		{
			if (_hitchWindowMax > 0.08f)
			{
				MelonLoader.MelonLogger.Msg($"[诊断] 近1秒最差帧 {_hitchWindowMax * 1000f:F0}ms | 暂存tabs={WuMingPerformance.Patches.InfoFloodControlPatch.HeldTabsCount}");
			}
			_hitchWindowMax = 0f;
			_hitchWindowStart = realtimeSinceStartup;
		}
	}

        public override void OnApplicationQuit()
        {
            // ---- Added for diagnosing the exit hang. Always logs, NOT gated on DiagnosticsEnabled. ----
            // The original code emitted nothing at all when no task was in flight, so "the hang is
            // later" and "this code never ran" were indistinguishable in the log. Every line below
            // is unconditional so the shutdown timeline is complete even with diagnostics off.
            //
            // Kind=ShutdownTag lets these be pulled out of a noisy log.
            System.Diagnostics.Stopwatch quitWatch = System.Diagnostics.Stopwatch.StartNew();
            MelonLoader.MelonLogger.Msg("[ShutdownTag] OnApplicationQuit ENTER | "
                + WuMingPerformance.Patches.BackgroundWorldTasks.DescribeWorker()
                + $" | IsSaving={WuMingPerformance.Patches.GameDataControllerSaveAsyncPatch.IsSaving}");

            try
		{
			System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
			while (WuMingPerformance.Patches.BackgroundWorldTasks.HasRunning && stopwatch.ElapsedMilliseconds < 15000)
			{
				WuMingPerformance.MainThreadDispatcher.Drain();
				System.Threading.Thread.Sleep(10);
			}
            MelonLoader.MelonLogger.Msg($"[ShutdownTag] waited for HasRunning to clear | "
                + $"elapsedMs={quitWatch.ElapsedMilliseconds} "
                + $"HasRunning={WuMingPerformance.Patches.BackgroundWorldTasks.HasRunning} | "
                + WuMingPerformance.Patches.BackgroundWorldTasks.DescribeWorker());
            if (WuMingPerformance.Patches.BackgroundWorldTasks.HasRunning)
            {
                MelonLoader.MelonLogger.Error("[性能优化] 退出时世界结算任务未能在 15 秒内完成，可能存在未结算数据");
            }
        }
        catch (System.Exception ex)
        {
            MelonLoader.MelonLogger.Error("[性能优化] 退出等待世界任务异常: " + ex.Message);
        }
        MelonLoader.MelonLogger.Msg($"[ShutdownTag] entering WaitForPendingSave | elapsedMs={quitWatch.ElapsedMilliseconds}");
        WuMingPerformance.Patches.GameDataControllerSaveAsyncPatch.WaitForPendingSave();
        MelonLoader.MelonLogger.Msg($"[ShutdownTag] WaitForPendingSave returned | elapsedMs={quitWatch.ElapsedMilliseconds}");

        // THE FIX. Stop the worker and wait for it to detach from the IL2CPP domain before the
        // runtime begins tearing down. Without this the thread was abandoned mid-attach, which is
        // the state that was measured at shutdown (attachedToIl2Cpp=True while the thread later
        // vanished). Done after the task waits above so in-flight work still gets its chance to
        // finish, and before base.OnApplicationQuit() so the detach happens as early as possible.
        MelonLoader.MelonLogger.Msg($"[ShutdownTag] stopping worker | elapsedMs={quitWatch.ElapsedMilliseconds} | "
            + WuMingPerformance.Patches.BackgroundWorldTasks.DescribeWorker());
        WuMingPerformance.Patches.BackgroundWorldTasks.ShutdownWorker();
        MelonLoader.MelonLogger.Msg($"[ShutdownTag] worker stopped | elapsedMs={quitWatch.ElapsedMilliseconds} | "
            + WuMingPerformance.Patches.BackgroundWorldTasks.DescribeWorker());

        base.OnApplicationQuit();
        MelonLoader.MelonLogger.Msg($"[ShutdownTag] base.OnApplicationQuit returned | elapsedMs={quitWatch.ElapsedMilliseconds} - "
            + "this is the last line this mod emits during shutdown; anything hung after this point is outside it");
    }

	public override void OnSceneWasLoaded(int buildIndex, string sceneName)
	{
		WuMingPerformance.Patches.ManageAllAIScanPatch.Reset();
		WuMingPerformance.Patches.InfoFloodControlPatch.ClearHeld();
		if (WuMingPerformance.PerformanceConfig.DiagnosticsEnabled)
		{
			MelonLoader.MelonLogger.Msg("[性能优化] 场景切换: " + sceneName + "，已重置 AI 扫描节流时间戳");
		}
		base.OnSceneWasLoaded(buildIndex, sceneName);
	}
}
