namespace WuMingPerformance.Patches;

[HarmonyLib.HarmonyPatch(typeof(Il2Cpp.GameController), "ManageHeroAutoQuitAndEnter")]
internal static class HeroAutoQuitEnterAsyncPatch
{
	internal static bool Prefix(Il2Cpp.GameController __instance)
	{
		if (WuMingPerformance.Patches.WorldAsyncContext.InBackgroundExecution)
		{
			return true;
		}
		if (!WuMingPerformance.PerformanceConfig.AsyncHeroQuitEnterEnabled)
		{
			return true;
		}
		WuMingPerformance.Patches.BackgroundWorldTasks.Submit("ManageHeroAutoQuitAndEnter", () =>
		{
			WuMingPerformance.Patches.WorldAsyncContext.InBackgroundExecution = true;
			WuMingPerformance.Patches.WorldAsyncContext.DeferRecruit = true;
			try
			{
				__instance.ManageHeroAutoQuitAndEnter();
			}
			finally
			{
				WuMingPerformance.Patches.WorldAsyncContext.InBackgroundExecution = false;
				WuMingPerformance.Patches.WorldAsyncContext.DeferRecruit = false;
			}
		}, waitForDaySignal: true);
		return false;
	}
}
