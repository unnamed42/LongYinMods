namespace WuMingPerformance.Patches;

[HarmonyLib.HarmonyPatch(typeof(Il2Cpp.GameController), "RemoveHero")]
internal static class RemoveHeroBudgetPatch
{
	internal static bool Prefix()
	{
		if (!WuMingPerformance.Patches.LateUpdateBudgetPatch.InLateUpdate || !WuMingPerformance.PerformanceConfig.LateUpdateBudgetEnabled)
		{
			return true;
		}
		if (WuMingPerformance.Patches.LateUpdateBudgetPatch.RemoveBudgetLeft-- > 0)
		{
			WuMingPerformance.Patches.LateUpdateBudgetPatch.CountRemove(skipped: false);
			return true;
		}
		WuMingPerformance.Patches.LateUpdateBudgetPatch.CountRemove(skipped: true);
		return false;
	}
}
