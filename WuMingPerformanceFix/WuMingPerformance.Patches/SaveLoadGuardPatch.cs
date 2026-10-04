namespace WuMingPerformance.Patches;

[HarmonyLib.HarmonyPatch(typeof(Il2Cpp.GameDataController))]
internal static class SaveLoadGuardPatch
{
	internal static bool Blocked
	{
		get
		{
			if (!WuMingPerformance.Patches.GameDataControllerSaveAsyncPatch.IsSaving)
			{
				return WuMingPerformance.Patches.BackgroundWorldTasks.HasRunning;
			}
			return true;
		}
	}

	[HarmonyLib.HarmonyPatch("CanSaveLoad")]
	[HarmonyLib.HarmonyPostfix]
	internal static void CanSaveLoadPostfix(ref bool __result)
	{
		if (Blocked)
		{
			__result = false;
		}
	}

	[HarmonyLib.HarmonyPatch("CanLoad")]
	[HarmonyLib.HarmonyPostfix]
	internal static void CanLoadPostfix(ref bool __result)
	{
		if (Blocked)
		{
			__result = false;
		}
	}

	[HarmonyLib.HarmonyPatch("Load")]
	[HarmonyLib.HarmonyPrefix]
	internal static bool LoadPrefix()
	{
		if (!Blocked)
		{
			return true;
		}
		MelonLoader.MelonLogger.Warning("[性能优化] 后台任务（存档/世界结算）进行中，已阻止本次读档");
		return false;
	}
}
