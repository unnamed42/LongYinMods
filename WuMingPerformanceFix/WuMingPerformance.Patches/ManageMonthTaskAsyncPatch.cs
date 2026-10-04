namespace WuMingPerformance.Patches;

[HarmonyLib.HarmonyPatch(typeof(Il2Cpp.GameController), "ManageMonthTask")]
internal static class ManageMonthTaskAsyncPatch
{
	internal static bool Prefix(Il2Cpp.GameController __instance)
	{
		if (WuMingPerformance.Patches.WorldAsyncContext.InBackgroundExecution)
		{
			return true;
		}
		if (!WuMingPerformance.PerformanceConfig.AsyncMonthTaskEnabled)
		{
			return true;
		}
		WuMingPerformance.Patches.BackgroundWorldTasks.Submit("ManageMonthTask", () =>
		{
			WuMingPerformance.Patches.WorldAsyncContext.InBackgroundExecution = true;
			try
			{
				__instance.ManageMonthTask();
			}
			finally
			{
				WuMingPerformance.Patches.WorldAsyncContext.InBackgroundExecution = false;
			}
		}, waitForDaySignal: true);
		return false;
	}
}
