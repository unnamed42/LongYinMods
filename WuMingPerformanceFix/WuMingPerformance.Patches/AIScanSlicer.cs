namespace WuMingPerformance.Patches;

internal static class AIScanSlicer
{
	private static int _queuedOrRunning;

	private static bool _lockFallbackWarned;

	internal static void Submit(Il2Cpp.GameController gc, bool considerAIHour)
	{
		if (!considerAIHour || System.Threading.Volatile.Read(ref _queuedOrRunning) <= 0)
		{
			System.Threading.Interlocked.Increment(ref _queuedOrRunning);
			WuMingPerformance.Patches.BackgroundWorldTasks.SubmitSelfLocked(considerAIHour ? "AI扫描(例行)" : "AI扫描(强制)", () =>
			{
				RunScan(gc, considerAIHour);
			});
		}
	}

	private static void RunScan(Il2Cpp.GameController gc, bool considerAIHour)
	{
		System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
		int num = 0;
		try
		{
			Il2Cpp.WorldData wd = gc.worldData;
			int sliceSize = WuMingPerformance.PerformanceConfig.AIScanSliceSize;
			if (wd.Player().atAreaID >= 0)
			{
				Il2CppSystem.Collections.Generic.List<int> insideHeros = wd.Player().GetArea().insideHeros;
				for (int i = 0; i < insideHeros.Count; i++)
				{
					int heroID = insideHeros[i];
					if (heroID == 0)
					{
						continue;
					}
					RunSlice(() =>
					{
						Il2Cpp.HeroData hero = wd.GetHero(heroID);
						if (hero != null)
						{
							gc.ManageOneAI(hero, considerAIHour);
						}
					});
					num++;
				}
			}
			int num2 = 1;
			while (num2 < wd.Heros.Count)
			{
				int from = num2;
				int end = num2;
				RunSlice(() =>
				{
					end = System.Math.Min(from + sliceSize, wd.Heros.Count);
					for (int j = from; j < end; j++)
					{
						Il2Cpp.HeroData heroData = wd.Heros[j];
						if (heroData != null)
						{
							gc.ManageOneAI(heroData, considerAIHour);
						}
					}
				});
				num += end - from;
				num2 = System.Math.Max(end, from + 1);
			}
			num2 = 0;
			while (num2 < wd.TempHeros.Count)
			{
				int from2 = num2;
				int end2 = num2;
				RunSlice(() =>
				{
					end2 = System.Math.Min(from2 + sliceSize, wd.TempHeros.Count);
					for (int j = from2; j < end2; j++)
					{
						Il2Cpp.HeroData heroData = wd.TempHeros[j];
						if (heroData != null)
						{
							gc.ManageOneAI(heroData, considerAIHour);
						}
					}
				});
				num += end2 - from2;
				num2 = System.Math.Max(end2, from2 + 1);
			}
		}
		catch (System.Exception ex)
		{
			MelonLoader.MelonLogger.Error("[WorldAsync] AI 扫描切片执行失败: " + ex.Message + "\n" + ex.StackTrace);
		}
		finally
		{
			System.Threading.Interlocked.Decrement(ref _queuedOrRunning);
			if (WuMingPerformance.PerformanceConfig.DiagnosticsEnabled)
			{
				long elapsedMilliseconds = stopwatch.ElapsedMilliseconds;
				if (!considerAIHour || elapsedMilliseconds > 100)
				{
					MelonLoader.MelonLogger.Msg($"[诊断] AI扫描({(considerAIHour ? "例行" : "强制")}): 处理 {num} 角色，耗时 {elapsedMilliseconds}ms");
				}
			}
		}
	}

	private static void RunSlice(System.Action body)
	{
		WuMingPerformance.NativeLockScope nativeLockScope = WuMingPerformance.NativeLockScope.TryEnter(Il2Cpp.GameController.lockObj, "AI扫描切片");
		if (nativeLockScope == null)
		{
			if (!_lockFallbackWarned)
			{
				_lockFallbackWarned = true;
				MelonLoader.MelonLogger.Warning("[WorldAsync] 原生 lockObj 不可用，AI 扫描降级为无锁执行（风险同原版 AI Task）");
			}
			body();
		}
		else
		{
			using (nativeLockScope)
			{
				body();
			}
			System.Threading.Thread.Sleep(2);
		}
	}
}
