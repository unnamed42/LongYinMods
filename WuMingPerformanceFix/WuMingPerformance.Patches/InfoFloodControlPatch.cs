namespace WuMingPerformance.Patches;

[HarmonyLib.HarmonyPatch(typeof(Il2Cpp.InfoController), "Update")]
internal static class InfoFloodControlPatch
{
	private static readonly System.Collections.Generic.List<Il2Cpp.InfoTabData> _heldTabs = new System.Collections.Generic.List<Il2Cpp.InfoTabData>();

	private static readonly System.Collections.Generic.List<Il2Cpp.InfoData> _heldInfos = new System.Collections.Generic.List<Il2Cpp.InfoData>();

	private static readonly System.Collections.Generic.List<Il2Cpp.MailData> _heldMails = new System.Collections.Generic.List<Il2Cpp.MailData>();

	private static readonly System.Collections.Generic.Queue<float> _releasedTabTimes = new System.Collections.Generic.Queue<float>();

	private const float TabLifetimeSeconds = 5f;

	internal static int HeldTabsCount => _heldTabs.Count;

	internal static void Prefix(Il2Cpp.InfoController __instance)
	{
		if (!WuMingPerformance.PerformanceConfig.InfoFloodControlEnabled)
		{
			return;
		}
		try
		{
			float realtimeSinceStartup = UnityEngine.Time.realtimeSinceStartup;
			while (_releasedTabTimes.Count > 0 && realtimeSinceStartup - _releasedTabTimes.Peek() > 5f)
			{
				_releasedTabTimes.Dequeue();
			}
			int val = WuMingPerformance.PerformanceConfig.InfoTabVisibleBudget - _releasedTabTimes.Count;
			RefillTabs(__instance.newInfoTabDatas, System.Math.Min(val, WuMingPerformance.PerformanceConfig.InfoFloodRefillPerFrame), realtimeSinceStartup);
			Refill(__instance.newInfoDatas, _heldInfos);
			Refill(__instance.newMailDatas, _heldMails);
			Cap(__instance.newInfoTabDatas, _heldTabs, "tabs");
			Cap(__instance.newInfoDatas, _heldInfos, "infos");
			Cap(__instance.newMailDatas, _heldMails, "mails");
		}
		catch (System.Exception ex)
		{
			MelonLoader.MelonLogger.Error("[Info限流] 异常: " + ex.Message);
		}
	}

	internal static void ClearHeld()
	{
		_heldTabs.Clear();
		_heldInfos.Clear();
		_heldMails.Clear();
		_releasedTabTimes.Clear();
	}

	private static void RefillTabs(Il2CppSystem.Collections.Generic.List<Il2Cpp.InfoTabData> queue, int maxRelease, float now)
	{
		int num = System.Math.Min(maxRelease, _heldTabs.Count);
		for (int num2 = num - 1; num2 >= 0; num2--)
		{
			queue.Insert(0, _heldTabs[num2]);
			_releasedTabTimes.Enqueue(now);
		}
		_heldTabs.RemoveRange(0, num);
	}

	private static void Cap<T>(Il2CppSystem.Collections.Generic.List<T> queue, System.Collections.Generic.List<T> held, string name)
	{
		int infoFloodMaxPerFrame = WuMingPerformance.PerformanceConfig.InfoFloodMaxPerFrame;
		int num = 0;
		while (queue.Count > infoFloodMaxPerFrame)
		{
			held.Add(queue[infoFloodMaxPerFrame]);
			queue.RemoveAt(infoFloodMaxPerFrame);
			num++;
		}
		if (num > 0 && held.Count == num && WuMingPerformance.PerformanceConfig.DiagnosticsEnabled)
		{
			MelonLoader.MelonLogger.Msg($"[Info限流] 消息洪峰：暂存 {num} 条 {name}，后续帧逐条回放");
		}
	}

	private static void Refill<T>(Il2CppSystem.Collections.Generic.List<T> queue, System.Collections.Generic.List<T> held)
	{
		int num = System.Math.Min(WuMingPerformance.PerformanceConfig.InfoFloodRefillPerFrame, held.Count);
		for (int num2 = num - 1; num2 >= 0; num2--)
		{
			queue.Insert(0, held[num2]);
		}
		held.RemoveRange(0, num);
	}
}
