namespace WuMingPerformance.Patches;

[HarmonyLib.HarmonyPatch(typeof(Il2Cpp.GameController), "ManageAreaStuff")]
internal static class ManageAreaStuffTimingPatch
{
	internal static void Prefix()
	{
		if (WuMingPerformance.Patches.DiagnosticsState.Enabled)
		{
			WuMingPerformance.Patches.DiagnosticsState.SwArea = System.Diagnostics.Stopwatch.StartNew();
		}
	}

	internal static void Postfix()
	{
		if (WuMingPerformance.Patches.DiagnosticsState.Enabled && WuMingPerformance.Patches.DiagnosticsState.SwArea != null)
		{
			MelonLoader.MelonLogger.Msg($"[诊断] ManageAreaStuff 耗时 {WuMingPerformance.Patches.DiagnosticsState.SwArea.ElapsedMilliseconds}ms");
			WuMingPerformance.Patches.DiagnosticsState.SwArea = null;
		}
	}
}
