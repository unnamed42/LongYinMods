using System;
using System.Collections.Generic;
using Il2Cpp;
using UnityEngine;

// 游戏侧集合类型与托管集合类型同名（都叫 List<T> / Dictionary<K,V>），
// 两个命名空间都 using 会 CS0104 不明确引用。
// 解法：托管集合走 System.Collections.Generic（上面的 using），
// 游戏侧集合用下面这些**具体闭合泛型**别名显式指出来（C# 别名不能带泛型参数，
// 所以只能按实际用到的元素类型逐个声明）。
using GameFloatList = Il2CppSystem.Collections.Generic.List<float>;
using GameStringList = Il2CppSystem.Collections.Generic.List<string>;
using GameHeroList = Il2CppSystem.Collections.Generic.List<Il2Cpp.HeroData>;
using GameResourceDataList = Il2CppSystem.Collections.Generic.List<Il2Cpp.ResourceData>;

namespace ForceOverflowDividend
{
	/// <summary>
	/// 一次 ChangeResource 调用期间的状态：变动前各资源的值 + 本次的净增量。
	/// 由 Harmony 的 __state 在 Prefix/Postfix 之间传递。
	/// </summary>
	internal sealed class ResourceChangeContext
	{
		internal string TriggerText { get; init; } = string.Empty;

		internal Dictionary<int, float> BeforeValues { get; init; } = new Dictionary<int, float>();

		internal Dictionary<int, float> PositiveDeltas { get; } = new Dictionary<int, float>();
	}

	internal sealed class MemberDistribution
	{
		internal HeroData Hero { get; init; } = null!;

		internal int Weight { get; init; }

		internal int Payout { get; set; }

		internal double Remainder { get; init; }
	}

	internal static class ForceOverflowRuntime
	{
		private static ForceOverflowConfig _config = ForceOverflowConfig.CreateDefault();

		private static int[] _trackedResourceIds = Array.Empty<int>();

		private static bool _resourceLookupWarningShown;

		/// <summary>
		/// GetForceName 的调用方式探测结果。
		///
		/// 背景：本 mod 的原始版本编译时，游戏的 <c>ForceData.GetForceName()</c> 还是无参的；
		/// 游戏后续更新把它改成了 <c>GetForceName(bool replacedForce = true)</c>。
		/// 托管侧编译期绑定的签名与运行时不一致，于是每次调用都抛
		/// <c>MissingMethodException</c>。而 SafeForceName 的 catch 把它吞成了
		/// "(未知门派)"，所以症状只是「日志里门派名全变成未知门派」+ 满屏 ERROR，
		/// 折现本身仍在工作 —— 很隐蔽。
		///
		/// 这里**不写死**调用哪个重载，而是反射探测当前运行时可用的签名，
		/// 这样下次游戏再改签名（加/删参数）也只会退化成「少一个门派名」，不会满屏报错。
		/// </summary>
		private static Func<ForceData, string>? _forceNameGetter;

		private static bool _forceNameGetterResolved;

		internal static ForceOverflowConfig Config => _config;

		internal static void Initialize(ForceOverflowConfig config)
		{
			_config = config ?? ForceOverflowConfig.CreateDefault();
			_trackedResourceIds = Array.Empty<int>();
			_resourceLookupWarningShown = false;
			_forceNameGetter = null;
			_forceNameGetterResolved = false;
		}

		internal static ResourceChangeContext? CreateContext(ForceData force, string triggerText)
		{
			if (!_config.Enabled || force == null)
			{
				return null;
			}

			RefreshTrackedResourceIds();
			if (_trackedResourceIds.Length == 0)
			{
				if (!_resourceLookupWarningShown)
				{
					_resourceLookupWarningShown = true;
					Main.Log.Warning("未能读取 GlobalData.ResourceName，已暂时跳过门派资源溢出折现。");
				}
				return null;
			}

			return new ResourceChangeContext
			{
				TriggerText = triggerText,
				BeforeValues = CaptureTrackedResourceValues(force)
			};
		}

		internal static void RecordPositiveDelta(ResourceChangeContext? context, int resourceId, float delta)
		{
			if (context == null || delta <= 0.0001f || !IsTrackedResourceId(resourceId))
			{
				return;
			}

			if (context.PositiveDeltas.TryGetValue(resourceId, out var existing))
			{
				context.PositiveDeltas[resourceId] = existing + delta;
			}
			else
			{
				context.PositiveDeltas[resourceId] = delta;
			}
		}

		internal static void HandleForceResourceChanged(ForceData force, ResourceChangeContext? context)
		{
			if (!_config.Enabled || force == null || context == null || context.PositiveDeltas.Count == 0)
			{
				return;
			}

			GameController instance = GameController.Instance;
			WorldData worldData = instance.worldData;
			int playerWeight = IsPlayerForce(worldData, force) ? GetPlayerPopulationWeight(worldData) : 0;

			if (TryProcessExpectedOverflow(force, worldData, context, out var convertedMoney, out var detailText))
			{
				Main.LogVerbose($"门派资源溢出已立刻结算：门派={SafeForceName(force)}，触发={context.TriggerText}，主角权重={playerWeight}，折现金额={convertedMoney}，{detailText}。结算后资源={BuildForceTrackedResourceSnapshot(force)}。");
			}
		}

		private static bool TryProcessExpectedOverflow(ForceData force, WorldData worldData, ResourceChangeContext context, out int convertedMoney, out string detailText)
		{
			convertedMoney = 0;
			detailText = string.Empty;

			if (force.resourceStore == null || force.resourceStoreMax == null)
			{
				return false;
			}

			List<string> details = new List<string>();
			bool overflowFound = false;

			for (int i = 0; i < _trackedResourceIds.Length; i++)
			{
				int resourceId = _trackedResourceIds[i];
				if (!context.PositiveDeltas.TryGetValue(resourceId, out var delta) || delta <= 0.0001f)
				{
					continue;
				}
				if (!context.BeforeValues.TryGetValue(resourceId, out var beforeValue))
				{
					continue;
				}
				if (!TryGetListValue(force.resourceStoreMax, resourceId, out var maxValue))
				{
					continue;
				}

				float overflow = beforeValue + delta - maxValue;
				if (overflow <= 0.0001f)
				{
					continue;
				}

				overflowFound = true;
				int money = ConvertOverflowToMoney(resourceId, overflow);
				convertedMoney += money;
				details.Add($"{GetResourceName(resourceId)} 原值 {beforeValue:0.##}，本次增加 {delta:0.##}，超出上限 {overflow:0.##}，按 {GetOverflowMoneyRatePercent(resourceId)}% 折现 {money} 钱");
			}

			if (!overflowFound)
			{
				return false;
			}

			List<HeroData> members = CollectEligibleMembers(force);
			if (members.Count == 0)
			{
				detailText = "检测到溢出，但未找到可发放成员";
				Main.Log.Warning("门派 " + SafeForceName(force) + " 检测到资源溢出，但没有可领取折现的门派成员。");
				return true;
			}

			int totalPopulationWeight = GetTotalPopulationWeight(members);
			DistributeMoney(worldData, members, convertedMoney);
			details.Add($"参与分配人数 {members.Count}，总占用人口 {totalPopulationWeight}");
			detailText = string.Join("；", details);
			return true;
		}

		/// <summary>按人口权重把折现总额分给门派成员，余数按小数部分从大到小补齐。</summary>
		private static void DistributeMoney(WorldData worldData, List<HeroData> members, int totalMoney)
		{
			if (totalMoney <= 0 || members.Count == 0)
			{
				return;
			}

			HeroData player = worldData.Player();
			int totalPopulationWeight = GetTotalPopulationWeight(members);
			if (totalPopulationWeight <= 0)
			{
				return;
			}

			List<MemberDistribution> distributions = new List<MemberDistribution>(members.Count);
			int distributed = 0;

			for (int i = 0; i < members.Count; i++)
			{
				HeroData hero = members[i];
				if (hero == null)
				{
					continue;
				}

				int weight = GetHeroPopulationWeight(hero);
				double exact = (double)totalMoney * weight / totalPopulationWeight;
				int floor = (int)Math.Floor(exact);
				distributed += floor;
				distributions.Add(new MemberDistribution
				{
					Hero = hero,
					Weight = weight,
					Payout = floor,
					Remainder = exact - floor
				});
			}

			// 余数分配：小数部分大的优先；并列时按权重大的优先，再并列按 heroID 保证结果确定。
			distributions.Sort((a, b) =>
			{
				int byRemainder = b.Remainder.CompareTo(a.Remainder);
				if (byRemainder != 0)
				{
					return byRemainder;
				}
				int byWeight = b.Weight.CompareTo(a.Weight);
				return (byWeight != 0) ? byWeight : a.Hero.heroID.CompareTo(b.Hero.heroID);
			});

			int remainder = totalMoney - distributed;
			for (int i = 0; i < remainder && i < distributions.Count; i++)
			{
				distributions[i].Payout++;
			}

			for (int i = 0; i < distributions.Count; i++)
			{
				MemberDistribution distribution = distributions[i];
				if (distribution.Payout <= 0)
				{
					continue;
				}

				bool isPlayer = player != null && distribution.Hero.heroID == player.heroID;
				distribution.Hero.ChangeMoney(distribution.Payout, isPlayer);
				Main.LogVerbose($"已按人口权重发放资源溢出折现：hero={distribution.Hero.heroName}，forceLv={distribution.Hero.heroForceLv}，populationWeight={distribution.Weight}，money={distribution.Payout}");
			}
		}

		private static List<HeroData> CollectEligibleMembers(ForceData force)
		{
			List<HeroData> result = new List<HeroData>();
			HashSet<int> seen = new HashSet<int>();

			GameHeroList ownHeros = force.GetOwnHeros();
			if (ownHeros != null)
			{
				for (int i = 0; i < ownHeros.Count; i++)
				{
					HeroData hero = ownHeros[i];
					if (IsEligibleMember(hero) && seen.Add(hero.heroID))
					{
						result.Add(hero);
					}
				}
			}

			// 兜底：门派一个可用成员都没有时，至少考虑掌门。
			if (result.Count == 0)
			{
				HeroData leader = force.GetLeader();
				if (IsEligibleMember(leader))
				{
					result.Add(leader);
				}
			}

			return result;
		}

		private static bool IsEligibleMember(HeroData? hero)
		{
			return hero != null && !hero.dead;
		}

		private static int GetTotalPopulationWeight(List<HeroData> members)
		{
			int total = 0;
			for (int i = 0; i < members.Count; i++)
			{
				total += GetHeroPopulationWeight(members[i]);
			}
			return total;
		}

		/// <summary>人口权重 = 2^clamp(heroForceLv, 0, 5)，即 1/2/4/8/16/32。</summary>
		private static int GetHeroPopulationWeight(HeroData? hero)
		{
			if (hero == null)
			{
				return 0;
			}
			int level = Mathf.Clamp(hero.heroForceLv, 0, 5);
			return 1 << level;
		}

		private static int GetPlayerPopulationWeight(WorldData worldData)
		{
			return GetHeroPopulationWeight(worldData.Player());
		}

		private static int ConvertOverflowToMoney(int resourceId, float overflow)
		{
			float unitValue = 1f;
			GameFloatList resourceValue = GlobalData.ResourceValue;
			if (resourceValue != null && resourceId >= 0 && resourceId < resourceValue.Count)
			{
				unitValue = Mathf.Max(0f, resourceValue[resourceId]);
			}

			float baseMoney = overflow * unitValue;
			return Mathf.FloorToInt(baseMoney * GetOverflowMoneyRatePercent(resourceId) / 100f);
		}

		private static void RefreshTrackedResourceIds()
		{
			GameStringList resourceName = GlobalData.ResourceName;
			if (resourceName == null || resourceName.Count == 0)
			{
				_trackedResourceIds = Array.Empty<int>();
				return;
			}

			// 资源表已加载：把配置里缺的资源项补上并落盘（首次启动时资源表通常还没加载）。
			ForceOverflowConfigStore.SyncKnownResourceRates(_config, Main.Log);

			// 最后一项是威望，不参与折现。
			int count = Math.Max(0, resourceName.Count - 1);
			int[] ids = new int[count];
			for (int i = 0; i < count; i++)
			{
				ids[i] = i;
			}
			_trackedResourceIds = ids;
		}

		private static int GetOverflowMoneyRatePercent(int resourceId)
		{
			return _config.GetOverflowMoneyRatePercent(resourceId);
		}

		internal static bool ShouldInspectResourceChange(int resourceId, float delta)
		{
			return delta > 0.0001f && IsTrackedResourceId(resourceId);
		}

		private static string BuildForceTrackedResourceSnapshot(ForceData force)
		{
			if (force == null || _trackedResourceIds.Length == 0)
			{
				return "(无可用资源快照)";
			}

			List<string> entries = new List<string>(_trackedResourceIds.Length);
			for (int i = 0; i < _trackedResourceIds.Length; i++)
			{
				int resourceId = _trackedResourceIds[i];
				if (TryGetListValue(force.resourceStore, resourceId, out var current) && TryGetListValue(force.resourceStoreMax, resourceId, out var max))
				{
					entries.Add($"{GetResourceName(resourceId)}={current:0.##}/{max:0.##}");
				}
			}

			return (entries.Count > 0) ? string.Join("，", entries) : "(无可用资源快照)";
		}

		private static Dictionary<int, float> CaptureTrackedResourceValues(ForceData force)
		{
			Dictionary<int, float> snapshot = new Dictionary<int, float>();
			if (force.resourceStore == null)
			{
				return snapshot;
			}

			for (int i = 0; i < _trackedResourceIds.Length; i++)
			{
				int resourceId = _trackedResourceIds[i];
				if (TryGetListValue(force.resourceStore, resourceId, out var value))
				{
					snapshot[resourceId] = value;
				}
			}

			return snapshot;
		}

		private static bool IsTrackedResourceId(int resourceId)
		{
			RefreshTrackedResourceIds();
			for (int i = 0; i < _trackedResourceIds.Length; i++)
			{
				if (_trackedResourceIds[i] == resourceId)
				{
					return true;
				}
			}
			return false;
		}

		private static bool TryGetListValue(GameFloatList? values, int index, out float value)
		{
			value = 0f;
			if (values == null || index < 0 || index >= values.Count)
			{
				return false;
			}
			value = values[index];
			return true;
		}

		private static bool IsPlayerForce(WorldData? worldData, ForceData? force)
		{
			if (worldData == null || force == null)
			{
				return false;
			}

			HeroData player = worldData.Player();
			return player != null && player.belongForceID == force.forceID;
		}

		private static string GetResourceName(int resourceId)
		{
			GameStringList resourceName = GlobalData.ResourceName;
			if (resourceName == null || resourceId < 0 || resourceId >= resourceName.Count)
			{
				return $"资源{resourceId}";
			}
			return resourceName[resourceId] ?? $"资源{resourceId}";
		}

		/// <summary>
		/// 取门派名，绝不抛异常 —— 名字只是日志装饰，不能因为取名字失败而中断折现结算。
		/// </summary>
		private static string SafeForceName(ForceData force)
		{
			try
			{
				return ResolveForceNameGetter()?.Invoke(force) ?? force.forceName ?? "(未知门派)";
			}
			catch (Exception ex)
			{
				// 只报一次，避免刷屏；同时明确降级为直接读 forceName 字段。
				if (!_forceNameWarningShown)
				{
					_forceNameWarningShown = true;
					Main.Log.Warning($"读取门派名失败，已降级为直接读取 forceName 字段：{ex.GetType().Name}: {ex.Message}");
				}
				try
				{
					return force.forceName ?? "(未知门派)";
				}
				catch
				{
					return "(未知门派)";
				}
			}
		}

		private static bool _forceNameWarningShown;

		/// <summary>
		/// 探测运行时实际存在的 GetForceName 重载。
		///
		/// 反射**不会**因为签名不匹配而抛 MissingMethodException —— 拿不到就返回 null，
		/// 这正是我们想要的：签名漂移退化成「没有门派名」，而不是每次调用炸一次。
		/// </summary>
		private static Func<ForceData, string>? ResolveForceNameGetter()
		{
			if (_forceNameGetterResolved)
			{
				return _forceNameGetter;
			}
			_forceNameGetterResolved = true;

			try
			{
				// 优先匹配当前版本的 GetForceName(bool)，其次无参重载，
				// 最后退化到 forceName 字段读取。
				foreach (var method in typeof(ForceData).GetMethods())
				{
					if (method.Name != "GetForceName")
					{
						continue;
					}

					var parameters = method.GetParameters();
					if (parameters.Length == 1 && parameters[0].ParameterType == typeof(bool))
					{
						_forceNameGetter = f => method.Invoke(f, new object[] { true }) as string ?? string.Empty;
						return _forceNameGetter;
					}
					if (parameters.Length == 0)
					{
						_forceNameGetter = f => method.Invoke(f, null) as string ?? string.Empty;
						return _forceNameGetter;
					}
				}
			}
			catch (Exception ex)
			{
				Main.Log.Warning($"探测 ForceData.GetForceName 失败，将使用 forceName 字段：" + ex.Message);
			}

			_forceNameGetter = null;
			return null;
		}
	}
}
