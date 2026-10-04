using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;

// 与 ForceOverflowRuntime.cs 同理：游戏侧集合类型与托管集合同名。
// Harmony 补丁参数的类型必须**精确**匹配被补丁方法的签名，所以这里
// 用别名把三个游戏侧重载各自钉死。
using GameFloatList = Il2CppSystem.Collections.Generic.List<float>;
using GameResourceDataList = Il2CppSystem.Collections.Generic.List<Il2Cpp.ResourceData>;

namespace ForceOverflowDividend
{
	/// <summary>
	/// <c>ForceData.ChangeResource(int id, float num, bool showInfo, bool showHud)</c>
	/// </summary>
	[HarmonyPatch(typeof(ForceData), "ChangeResource", new Type[]
	{
		typeof(int),
		typeof(float),
		typeof(bool),
		typeof(bool)
	})]
	internal static class ForceData_ChangeResource_Int_Patch
	{
		private static void Prefix(ForceData __instance, int id, float num, ref ResourceChangeContext? __state)
		{
			if (ForceOverflowRuntime.ShouldInspectResourceChange(id, num))
			{
				__state = ForceOverflowRuntime.CreateContext(__instance, $"单项资源变化：resourceId={id}，delta={num:0.##}");
				ForceOverflowRuntime.RecordPositiveDelta(__state, id, num);
			}
		}

		private static void Postfix(ForceData __instance, ResourceChangeContext? __state)
		{
			try
			{
				ForceOverflowRuntime.HandleForceResourceChanged(__instance, __state);
			}
			catch (Exception value)
			{
				Main.Log.Error($"处理资源变动后的门派资源溢出折现失败：{value}");
			}
		}
	}

	/// <summary>
	/// <c>ForceData.ChangeResource(List&lt;float&gt; resourceList, bool showInfo, bool showHud)</c>
	/// —— 按资源 id 下标解释列表。
	/// </summary>
	[HarmonyPatch(typeof(ForceData), "ChangeResource", new Type[]
	{
		typeof(GameFloatList),
		typeof(bool),
		typeof(bool)
	})]
	internal static class ForceData_ChangeResource_ListFloat_Patch
	{
		private static void Prefix(ForceData __instance, GameFloatList resourceList, ref ResourceChangeContext? __state)
		{
			if (resourceList == null)
			{
				return;
			}

			ResourceChangeContext? context = ForceOverflowRuntime.CreateContext(__instance, "批量资源变化：List<float>");
			if (context == null)
			{
				return;
			}

			for (int i = 0; i < resourceList.Count; i++)
			{
				ForceOverflowRuntime.RecordPositiveDelta(context, i, resourceList[i]);
			}

			if (context.PositiveDeltas.Count > 0)
			{
				__state = context;
			}
		}

		private static void Postfix(ForceData __instance, ResourceChangeContext? __state)
		{
			try
			{
				ForceOverflowRuntime.HandleForceResourceChanged(__instance, __state);
			}
			catch (Exception value)
			{
				Main.Log.Error($"处理批量资源变动后的门派资源溢出折现失败：{value}");
			}
		}
	}

	/// <summary>
	/// <c>ForceData.ChangeResource(List&lt;ResourceData&gt; resourceList, bool showInfo, bool showHud)</c>
	/// —— 按 resourceType 解释每一项。
	/// </summary>
	[HarmonyPatch(typeof(ForceData), "ChangeResource", new Type[]
	{
		typeof(GameResourceDataList),
		typeof(bool),
		typeof(bool)
	})]
	internal static class ForceData_ChangeResource_ListResourceData_Patch
	{
		private static void Prefix(ForceData __instance, GameResourceDataList resourceList, ref ResourceChangeContext? __state)
		{
			if (resourceList == null)
			{
				return;
			}

			ResourceChangeContext? context = ForceOverflowRuntime.CreateContext(__instance, "批量资源变化：List<ResourceData>");
			if (context == null)
			{
				return;
			}

			for (int i = 0; i < resourceList.Count; i++)
			{
				ResourceData resource = resourceList[i];
				if (resource != null)
				{
					ForceOverflowRuntime.RecordPositiveDelta(context, resource.resourceType, resource.resourceNum);
				}
			}

			if (context.PositiveDeltas.Count > 0)
			{
				__state = context;
			}
		}

		private static void Postfix(ForceData __instance, ResourceChangeContext? __state)
		{
			try
			{
				ForceOverflowRuntime.HandleForceResourceChanged(__instance, __state);
			}
			catch (Exception value)
			{
				Main.Log.Error($"处理资源对象列表变动后的门派资源溢出折现错误：{value}");
			}
		}
	}
}
