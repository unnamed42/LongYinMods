namespace WuMingPerformance.Patches;

[HarmonyLib.HarmonyPatch(typeof(Il2Cpp.GameController), "ChangeDay", new System.Type[] { })]
internal static class ChangeDayTimingPatch
{
	internal static void Prefix()
	{
		if (WuMingPerformance.Patches.DiagnosticsState.Enabled)
		{
			WuMingPerformance.Patches.DiagnosticsState.SwDay = System.Diagnostics.Stopwatch.StartNew();
			WuMingPerformance.Patches.DiagnosticsState.ChangeDayStartRealtime = UnityEngine.Time.realtimeSinceStartup;
		}
	}

	internal static void Postfix()
	{
		if (!WuMingPerformance.Patches.DiagnosticsState.Enabled || WuMingPerformance.Patches.DiagnosticsState.SwDay == null)
		{
			return;
		}
		long elapsedMilliseconds = WuMingPerformance.Patches.DiagnosticsState.SwDay.ElapsedMilliseconds;
		WuMingPerformance.Patches.DiagnosticsState.SwDay = null;
		int value = 0;
		int value2 = 0;
		int value3 = 0;
		try
		{
			if (Il2Cpp.InfoController.Instance != null)
			{
				value = Il2Cpp.InfoController.Instance.newInfoTabDatas?.Count ?? 0;
				value2 = Il2Cpp.InfoController.Instance.newInfoDatas?.Count ?? 0;
				value3 = Il2Cpp.InfoController.Instance.newMailDatas?.Count ?? 0;
			}
		}
		catch
		{
		}
		MelonLoader.MelonLogger.Msg($"[诊断] ChangeDay 主线程总耗时 {elapsedMilliseconds}ms | 消息积压 tabs={value} infos={value2} mails={value3}");
	}
}
