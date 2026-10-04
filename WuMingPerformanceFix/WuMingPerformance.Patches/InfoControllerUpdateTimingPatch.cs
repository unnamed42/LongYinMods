namespace WuMingPerformance.Patches;

[HarmonyLib.HarmonyPatch(typeof(Il2Cpp.InfoController), "Update")]
internal static class InfoControllerUpdateTimingPatch
{
	internal static void Prefix()
	{
		if (WuMingPerformance.Patches.DiagnosticsState.Enabled)
		{
			WuMingPerformance.Patches.DiagnosticsState.SwInfoUpdate = System.Diagnostics.Stopwatch.StartNew();
		}
	}

	internal static void Postfix()
	{
		if (WuMingPerformance.Patches.DiagnosticsState.Enabled && WuMingPerformance.Patches.DiagnosticsState.SwInfoUpdate != null)
		{
			long elapsedMilliseconds = WuMingPerformance.Patches.DiagnosticsState.SwInfoUpdate.ElapsedMilliseconds;
			WuMingPerformance.Patches.DiagnosticsState.SwInfoUpdate = null;
			if (elapsedMilliseconds > 8)
			{
				MelonLoader.MelonLogger.Msg($"[诊断] InfoController.Update 消息处理 {elapsedMilliseconds}ms（UI 洪峰）");
			}
		}
	}
}
