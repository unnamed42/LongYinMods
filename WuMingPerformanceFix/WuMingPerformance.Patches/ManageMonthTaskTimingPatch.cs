namespace WuMingPerformance.Patches;

[HarmonyLib.HarmonyPatch(typeof(Il2Cpp.GameController), "ManageMonthTask")]
internal static class ManageMonthTaskTimingPatch
{
	internal static void Prefix()
	{
		if (WuMingPerformance.Patches.DiagnosticsState.Enabled)
		{
			WuMingPerformance.Patches.DiagnosticsState.SwMonthTask = System.Diagnostics.Stopwatch.StartNew();
		}
	}

	internal static void Postfix()
	{
		if (WuMingPerformance.Patches.DiagnosticsState.Enabled && WuMingPerformance.Patches.DiagnosticsState.SwMonthTask != null)
		{
			MelonLoader.MelonLogger.Msg($"[诊断] ManageMonthTask 等锁+执行 {WuMingPerformance.Patches.DiagnosticsState.SwMonthTask.ElapsedMilliseconds}ms");
			WuMingPerformance.Patches.DiagnosticsState.SwMonthTask = null;
		}
	}
}
