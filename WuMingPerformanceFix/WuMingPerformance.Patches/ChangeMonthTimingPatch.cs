namespace WuMingPerformance.Patches;

[HarmonyLib.HarmonyPatch(typeof(Il2Cpp.GameController), "ChangeMonth")]
internal static class ChangeMonthTimingPatch
{
	internal static void Prefix()
	{
		if (WuMingPerformance.Patches.DiagnosticsState.Enabled)
		{
			WuMingPerformance.Patches.DiagnosticsState.SwMonth = System.Diagnostics.Stopwatch.StartNew();
		}
	}

	internal static void Postfix()
	{
		if (WuMingPerformance.Patches.DiagnosticsState.Enabled && WuMingPerformance.Patches.DiagnosticsState.SwMonth != null)
		{
			MelonLoader.MelonLogger.Msg($"[诊断] ChangeMonth 主线程总耗时 {WuMingPerformance.Patches.DiagnosticsState.SwMonth.ElapsedMilliseconds}ms");
			WuMingPerformance.Patches.DiagnosticsState.SwMonth = null;
		}
	}
}
