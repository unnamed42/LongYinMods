namespace WuMingPerformance.Patches;

[HarmonyLib.HarmonyPatch(typeof(Il2Cpp.GameController), "CountAreaData")]
internal static class CountAreaDataDeferPatch
{
	internal static bool Prefix(Il2Cpp.GameController __instance, Il2Cpp.AreaData area)
	{
		if (WuMingPerformance.Patches.WorldAsyncContext.InBackgroundExecution)
		{
			return true;
		}
		if (!WuMingPerformance.Patches.LateUpdateBudgetPatch.InLateUpdate || !WuMingPerformance.PerformanceConfig.LateUpdateBudgetEnabled)
		{
			return true;
		}
		area.areaDetailDirty = false;
		WuMingPerformance.Patches.BackgroundWorldTasks.Submit("CountAreaData", () =>
		{
			WuMingPerformance.Patches.WorldAsyncContext.InBackgroundExecution = true;
			try
			{
				__instance.CountAreaData(area);
			}
			finally
			{
				WuMingPerformance.Patches.WorldAsyncContext.InBackgroundExecution = false;
			}
		}, waitForDaySignal: false, quiet: true);
		WuMingPerformance.Patches.LateUpdateBudgetPatch.CountAreaDeferred();
		return false;
	}
}
