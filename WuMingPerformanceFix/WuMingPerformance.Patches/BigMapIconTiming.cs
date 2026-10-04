namespace WuMingPerformance.Patches;

internal static class BigMapIconTiming
{
	[HarmonyLib.HarmonyPatch(typeof(Il2Cpp.BigMapController), "CreateBigMapNpc")]
	internal static class Create
	{
		internal static void Prefix()
		{
			if (WuMingPerformance.Patches.DiagnosticsState.Enabled)
			{
				_swCreate = System.Diagnostics.Stopwatch.StartNew();
			}
		}

		internal static void Postfix()
		{
			if (WuMingPerformance.Patches.DiagnosticsState.Enabled && _swCreate != null)
			{
				long elapsedMilliseconds = _swCreate.ElapsedMilliseconds;
				_swCreate = null;
				if (elapsedMilliseconds > 10)
				{
					MelonLoader.MelonLogger.Msg($"[诊断] CreateBigMapNpc {elapsedMilliseconds}ms");
				}
			}
		}
	}

	[HarmonyLib.HarmonyPatch(typeof(Il2Cpp.BigMapController), "RefreshBigMapNPC", new System.Type[] { typeof(Il2Cpp.HeroData) })]
	internal static class Refresh
	{
		internal static void Prefix()
		{
			if (WuMingPerformance.Patches.DiagnosticsState.Enabled)
			{
				_swRefresh = System.Diagnostics.Stopwatch.StartNew();
			}
		}

		internal static void Postfix()
		{
			if (WuMingPerformance.Patches.DiagnosticsState.Enabled && _swRefresh != null)
			{
				long elapsedMilliseconds = _swRefresh.ElapsedMilliseconds;
				_swRefresh = null;
				if (elapsedMilliseconds > 10)
				{
					MelonLoader.MelonLogger.Msg($"[诊断] RefreshBigMapNPC {elapsedMilliseconds}ms");
				}
			}
		}
	}

	[HarmonyLib.HarmonyPatch(typeof(Il2Cpp.BigMapController), "RecreateAllBigMapHeroIcon")]
	internal static class Recreate
	{
		internal static void Prefix()
		{
			if (WuMingPerformance.Patches.DiagnosticsState.Enabled)
			{
				_swRecreate = System.Diagnostics.Stopwatch.StartNew();
			}
		}

		internal static void Postfix()
		{
			if (WuMingPerformance.Patches.DiagnosticsState.Enabled && _swRecreate != null)
			{
				long elapsedMilliseconds = _swRecreate.ElapsedMilliseconds;
				_swRecreate = null;
				if (elapsedMilliseconds > 10)
				{
					MelonLoader.MelonLogger.Msg($"[诊断] RecreateAllBigMapHeroIcon {elapsedMilliseconds}ms");
				}
			}
		}
	}

	[System.ThreadStatic]
	private static System.Diagnostics.Stopwatch? _swCreate;

	[System.ThreadStatic]
	private static System.Diagnostics.Stopwatch? _swRefresh;

	[System.ThreadStatic]
	private static System.Diagnostics.Stopwatch? _swRecreate;
}
