namespace WuMingPerformance.Patches;

[HarmonyLib.HarmonyPatch(typeof(Il2Cpp.GameController), "WorldAddNewHero")]
internal static class WorldAddNewHeroRecruitDeferPatch
{
	private const int MaxRetryFrames = 3600;

	private const int StarvationWarnFrames = 600;

	private const uint LockWaitMs = 2u;

	internal static bool Prefix(Il2Cpp.GameController __instance, int forceID, int heroForceLv, bool outSideForce, bool _showInfo, ref Il2Cpp.HeroData __result)
	{
		if (!WuMingPerformance.Patches.WorldAsyncContext.DeferRecruit || !WuMingPerformance.Patches.WorldAsyncContext.IsWorkerThread)
		{
			return true;
		}
		Il2Cpp.WorldData worldData = __instance.worldData;
		Il2Cpp.HeroData hero = __instance.GenerateHeroData(null, -1, forceID, heroForceLv, null, isTempHero: true, Il2Cpp.SexLimit.None, isRandomEnemy: false, outSideForce);
		int targetAreaID;
		if (forceID >= 0 && !outSideForce)
		{
			targetAreaID = worldData.GetForce(forceID).mainAreaID;
		}
		else
		{
			Il2CppSystem.Collections.Generic.List<int> list = ((Il2Cpp.GlobalData.RandomRangeDouble() < 0.4000000059604645) ? worldData.cityAreaID : worldData.villageAreaID);
			targetAreaID = list[Il2Cpp.GlobalData.RandomRange(0, list.Count)];
		}
		hero.atAreaID = targetAreaID;
		WuMingPerformance.Patches.BackgroundWorldTasks.Track();
		WuMingPerformance.MainThreadDispatcher.Post(() =>
		{
			DeferredRecruit(__instance, worldData, hero, forceID, heroForceLv, outSideForce, _showInfo, targetAreaID, 0);
		});
		__result = hero;
		return false;
	}

	private static void DeferredRecruit(Il2Cpp.GameController gc, Il2Cpp.WorldData capturedWorld, Il2Cpp.HeroData hero, int forceID, int heroForceLv, bool outSideForce, bool showInfo, int targetAreaID, int retryCount)
	{
		bool flag = true;
		try
		{
			if (gc.worldData != capturedWorld)
			{
				MelonLoader.MelonLogger.Warning("[WorldAsync] 世界已切换，丢弃一次延迟招募");
				return;
			}
			if (WuMingPerformance.Patches.GameDataControllerSaveAsyncPatch.IsSaving)
			{
				if (retryCount < 3600)
				{
					flag = false;
					WuMingPerformance.MainThreadDispatcher.Post(() =>
					{
						DeferredRecruit(gc, capturedWorld, hero, forceID, heroForceLv, outSideForce, showInfo, targetAreaID, retryCount + 1);
					});
				}
				else
				{
					MelonLoader.MelonLogger.Error("[WorldAsync] 延迟招募等存档超时，本次招募丢失");
				}
				return;
			}
			WuMingPerformance.NativeLockScope nativeLockScope = WuMingPerformance.NativeLockScope.TryEnterTimeout(Il2Cpp.GameController.lockObj, 2u, "DeferredRecruit");
			if (nativeLockScope == null)
			{
				if (retryCount == 600)
				{
					MelonLoader.MelonLogger.Warning($"[WorldAsync] 延迟招募已等锁 {600} 帧，lockObj 持续饱和");
				}
				if (retryCount < 3600)
				{
					flag = false;
					WuMingPerformance.MainThreadDispatcher.Post(() =>
					{
						DeferredRecruit(gc, capturedWorld, hero, forceID, heroForceLv, outSideForce, showInfo, targetAreaID, retryCount + 1);
					});
				}
				else
				{
					MelonLoader.MelonLogger.Error("[WorldAsync] 延迟招募等锁超时，本次招募丢失");
				}
				return;
			}
			using (nativeLockScope)
			{
				hero.atAreaID = -1;
				gc.worldData.AddNewHero(hero);
				if (forceID >= 0 && !outSideForce)
				{
					hero.JoinForce(forceID, heroForceLv, -1, showInfo);
					gc.HeroEnterArea(hero, capturedWorld.GetForce(forceID).mainAreaID);
				}
				else
				{
					gc.HeroEnterArea(hero, targetAreaID);
				}
			}
		}
		catch (System.Exception ex)
		{
			MelonLoader.MelonLogger.Error("[WorldAsync] 延迟招募执行失败: " + ex.Message + "\n" + ex.StackTrace);
		}
		finally
		{
			if (flag)
			{
				WuMingPerformance.Patches.BackgroundWorldTasks.Complete();
			}
		}
	}
}
