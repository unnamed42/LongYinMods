namespace WuMingPerformance.Patches;

[HarmonyLib.HarmonyPatch(typeof(Il2Cpp.GameController), "LateUpdate")]
internal static class LateUpdateBudgetPatch
{
	[System.ThreadStatic]
	internal static bool InLateUpdate;

	internal static int RemoveBudgetLeft;

	private static System.Diagnostics.Stopwatch? _sw;

	private static int _areaDeferred;

	private static int _forceDeferred;

	private static int _bookDeferred;

	private static int _removeDone;

	private static int _removeSkipped;

	internal static void Prefix()
	{
		InLateUpdate = true;
		RemoveBudgetLeft = WuMingPerformance.PerformanceConfig.LateUpdateRemoveBudget;
		_areaDeferred = (_forceDeferred = (_bookDeferred = (_removeDone = (_removeSkipped = 0))));
		if (WuMingPerformance.Patches.DiagnosticsState.Enabled)
		{
			_sw = System.Diagnostics.Stopwatch.StartNew();
		}
	}

	internal static void Postfix()
	{
		InLateUpdate = false;
		if (WuMingPerformance.Patches.DiagnosticsState.Enabled && _sw != null)
		{
			long elapsedMilliseconds = _sw.ElapsedMilliseconds;
			_sw = null;
			if (elapsedMilliseconds > 25 || _areaDeferred > 0 || _forceDeferred > 0 || _removeSkipped > 0)
			{
				MelonLoader.MelonLogger.Msg($"[诊断] LateUpdate {elapsedMilliseconds}ms | 延迟重算 区域{_areaDeferred} 门派{_forceDeferred} 书库{_bookDeferred} | RemoveHero 执行{_removeDone}/跳过{_removeSkipped}");
			}
		}
	}

	internal static void CountAreaDeferred()
	{
		_areaDeferred++;
	}

	internal static void CountForceDeferred()
	{
		_forceDeferred++;
	}

	internal static void CountBookDeferred()
	{
		_bookDeferred++;
	}

	internal static void CountRemove(bool skipped)
	{
		if (skipped)
		{
			_removeSkipped++;
		}
		else
		{
			_removeDone++;
		}
	}
}
