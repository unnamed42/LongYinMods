namespace WuMingPerformance.Patches;

[HarmonyLib.HarmonyPatch(typeof(Il2Cpp.GameController), "CountForceData")]
internal static class CountForceDataDeferPatch
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
		force.forceDetailDirty = false;
		WuMingPerformance.Patches.BackgroundWorldTasks.Submit("CountForceData", () =>
		{
			WuMingPerformance.Patches.WorldAsyncContext.InBackgroundExecution = true;
			try
			{
				__instance.CountForceData(force);
			}
			finally
			{
				WuMingPerformance.Patches.WorldAsyncContext.InBackgroundExecution = false;
			}
		}, waitForDaySignal: false, quiet: true);
		WuMingPerformance.Patches.LateUpdateBudgetPatch.CountForceDeferred();
		return false;
	}
}
