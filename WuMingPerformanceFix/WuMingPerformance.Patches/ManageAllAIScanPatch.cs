namespace WuMingPerformance.Patches;

[HarmonyLib.HarmonyPatch(typeof(Il2Cpp.GameController), "ManageAllAI")]
internal static class ManageAllAIScanPatch
{
	private static float _lastSubmitRealtime = float.NegativeInfinity;

	private static int _skippedCount;

	private static float _lastSummaryRealtime;

	private static bool _firstFireLogged;

	internal static void Reset()
	{
		_lastSubmitRealtime = float.NegativeInfinity;
		_skippedCount = 0;
		_lastSummaryRealtime = 0f;
	}

	internal static bool Prefix(Il2Cpp.GameController __instance, bool considerAIHour)
	{
		if (!WuMingPerformance.PerformanceConfig.AIGateEnabled)
		{
			return true;
		}
		if (considerAIHour)
		{
			float aIMinSubmitInterval = WuMingPerformance.PerformanceConfig.AIMinSubmitInterval;
			if (aIMinSubmitInterval > 0f)
			{
				float realtimeSinceStartup = UnityEngine.Time.realtimeSinceStartup;
				if (realtimeSinceStartup - _lastSubmitRealtime < aIMinSubmitInterval)
				{
					_skippedCount++;
					if (WuMingPerformance.PerformanceConfig.DiagnosticsEnabled)
					{
						if (!_firstFireLogged)
						{
							_firstFireLogged = true;
							MelonLoader.MelonLogger.Msg("[AI闸] 首次生效: 例行全量 AI 扫描最小间隔 " + aIMinSubmitInterval.ToString("F2") + "s（跳过高频提交）");
						}
						if (realtimeSinceStartup - _lastSummaryRealtime >= 60f)
						{
							MelonLoader.MelonLogger.Msg("[AI闸] 近 60 秒合并跳过了 " + _skippedCount + " 次例行全量 AI 扫描");
							_skippedCount = 0;
							_lastSummaryRealtime = realtimeSinceStartup;
						}
					}
					else
					{
						_skippedCount = 0;
					}
					return false;
				}
				_lastSubmitRealtime = realtimeSinceStartup;
			}
		}
		if (WuMingPerformance.PerformanceConfig.AIScanSliceEnabled)
		{
			WuMingPerformance.Patches.AIScanSlicer.Submit(__instance, considerAIHour);
			return false;
		}
		return true;
	}
}
