namespace WuMingPerformance.Patches;

[HarmonyLib.HarmonyPatch(typeof(Il2Cpp.GameController), "ManageForceStuff")]
internal static class ManageForceStuffAsyncPatch
{
	internal static bool Prefix(Il2Cpp.GameController __instance)
	{
		if (WuMingPerformance.Patches.WorldAsyncContext.InBackgroundExecution)
		{
			return true;
		}
		WuMingPerformance.Patches.BackgroundWorldTasks.NotifyMainThreadPassedForceStuff();
		if (WuMingPerformance.Patches.DiagnosticsState.Enabled && WuMingPerformance.Patches.DiagnosticsState.ChangeDayStartRealtime >= 0f)
		{
			float value = (UnityEngine.Time.realtimeSinceStartup - WuMingPerformance.Patches.DiagnosticsState.ChangeDayStartRealtime) * 1000f;
			MelonLoader.MelonLogger.Msg($"[诊断] ChangeDay 起点 → ManageForceStuff 提交点 {value:F0}ms（含 :4339 等锁）");
		}
		if (!WuMingPerformance.PerformanceConfig.AsyncForceStuffEnabled)
		{
			return true;
		}
		WuMingPerformance.Patches.BackgroundWorldTasks.Submit("ManageForceStuff", () =>
		{
			WuMingPerformance.Patches.WorldAsyncContext.InBackgroundExecution = true;
			try
			{
				__instance.ManageForceStuff();
			}
			finally
			{
				WuMingPerformance.Patches.WorldAsyncContext.InBackgroundExecution = false;
			}
		});
		return false;
	}
}
