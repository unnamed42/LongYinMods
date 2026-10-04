namespace WuMingPerformance.Patches;

[HarmonyLib.HarmonyPatch(typeof(Il2Cpp.GameController), "CountForceBookStorage")]
internal static class CountForceBookStorageDeferPatch
{
	internal static bool Prefix(Il2Cpp.GameController __instance, Il2Cpp.ForceData force)
	{
		if (WuMingPerformance.Patches.WorldAsyncContext.InBackgroundExecution)
		{
			return true;
		}
		if (!WuMingPerformance.Patches.LateUpdateBudgetPatch.InLateUpdate || !WuMingPerformance.PerformanceConfig.LateUpdateBudgetEnabled)
		{
			return true;
		}
		force.bookStorageDirty = false;
		WuMingPerformance.Patches.BackgroundWorldTasks.Submit("CountForceBookStorage", () =>
		{
			WuMingPerformance.Patches.WorldAsyncContext.InBackgroundExecution = true;
			try
			{
				__instance.CountForceBookStorage(force);
			}
			finally
			{
				WuMingPerformance.Patches.WorldAsyncContext.InBackgroundExecution = false;
			}
		}, waitForDaySignal: false, quiet: true);
		WuMingPerformance.Patches.LateUpdateBudgetPatch.CountBookDeferred();
		return false;
	}
}
