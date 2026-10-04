namespace WuMingPerformance.Patches;

internal static class ChangeDayMidTiming
{
	[HarmonyLib.HarmonyPatch(typeof(Il2Cpp.GameController), "ManageAiForceAttackAreaAndResourcePoint")]
	internal static class Attack
	{
		internal static void Prefix()
		{
			Start(ref _swAttack);
		}

		internal static void Postfix()
		{
			Stop(ref _swAttack, "ManageAiForceAttackAreaAndResourcePoint");
		}
	}

	[HarmonyLib.HarmonyPatch(typeof(Il2Cpp.GameController), "GenerateRandomEnemy")]
	internal static class Enemy
	{
		internal static void Prefix()
		{
			Start(ref _swEnemy);
		}

		internal static void Postfix()
		{
			Stop(ref _swEnemy, "GenerateRandomEnemy");
		}
	}

	[HarmonyLib.HarmonyPatch(typeof(Il2Cpp.GameController), "GenerateRandomEvent")]
	internal static class REvent
	{
		internal static void Prefix()
		{
			Start(ref _swEvent);
		}

		internal static void Postfix()
		{
			Stop(ref _swEvent, "GenerateRandomEvent");
		}
	}

	[HarmonyLib.HarmonyPatch(typeof(Il2Cpp.WorldEventController), "ManageWorldEvent")]
	internal static class WEvent
	{
		internal static void Prefix()
		{
			Start(ref _swWorldEvent);
		}

		internal static void Postfix()
		{
			Stop(ref _swWorldEvent, "ManageWorldEvent");
		}
	}

	[System.ThreadStatic]
	private static System.Diagnostics.Stopwatch? _swAttack;

	[System.ThreadStatic]
	private static System.Diagnostics.Stopwatch? _swEnemy;

	[System.ThreadStatic]
	private static System.Diagnostics.Stopwatch? _swEvent;

	[System.ThreadStatic]
	private static System.Diagnostics.Stopwatch? _swWorldEvent;

	internal static void Start(ref System.Diagnostics.Stopwatch? sw)
	{
		if (WuMingPerformance.Patches.DiagnosticsState.Enabled)
		{
			sw = System.Diagnostics.Stopwatch.StartNew();
		}
	}

	internal static void Stop(ref System.Diagnostics.Stopwatch? sw, string name)
	{
		if (WuMingPerformance.Patches.DiagnosticsState.Enabled && sw != null)
		{
			long elapsedMilliseconds = sw.ElapsedMilliseconds;
			sw = null;
			if (elapsedMilliseconds > 10)
			{
				MelonLoader.MelonLogger.Msg($"[诊断] ChangeDay中段 {name} {elapsedMilliseconds}ms");
			}
		}
	}
}
