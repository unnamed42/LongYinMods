using System;
using System.Collections.Generic;
using System.IO;
using Il2Cpp;
using MelonLoader;

namespace ForceOverflowDividend
{
	/// <summary>
	/// 运行时配置。默认钱资源（id=0）按 100% 折现，其余资源按 10%。
	/// </summary>
	internal sealed class ForceOverflowConfig
	{
		internal const int DefaultMoneyOverflowMoneyRatePercent = 100;

		internal const int DefaultOtherOverflowMoneyRatePercent = 10;

		internal bool Enabled { get; set; } = true;

		internal bool VerboseLog { get; set; }

		internal int FallbackOtherResourceRatePercent { get; set; } = DefaultOtherOverflowMoneyRatePercent;

		internal Dictionary<int, int> ResourceOverflowMoneyRatePercentById { get; } = new Dictionary<int, int>();

		internal static ForceOverflowConfig CreateDefault()
		{
			ForceOverflowConfig forceOverflowConfig = new ForceOverflowConfig();
			forceOverflowConfig.EnsureResourceRateCoverage(1);
			return forceOverflowConfig;
		}

		internal int GetOverflowMoneyRatePercent(int resourceId)
		{
			if (ResourceOverflowMoneyRatePercentById.TryGetValue(resourceId, out var value))
			{
				return value;
			}
			return GetDefaultOverflowMoneyRatePercent(resourceId, FallbackOtherResourceRatePercent);
		}

		internal void SetOverflowMoneyRatePercent(int resourceId, int ratePercent)
		{
			if (resourceId >= 0)
			{
				ResourceOverflowMoneyRatePercentById[resourceId] = Math.Max(0, ratePercent);
			}
		}

		/// <summary>补齐 [0, resourceCount) 区间的资源比例项，返回是否有新增。</summary>
		internal bool EnsureResourceRateCoverage(int resourceCount)
		{
			bool result = false;
			for (int i = 0; i < resourceCount; i++)
			{
				if (!ResourceOverflowMoneyRatePercentById.ContainsKey(i))
				{
					ResourceOverflowMoneyRatePercentById[i] = GetDefaultOverflowMoneyRatePercent(i, FallbackOtherResourceRatePercent);
					result = true;
				}
			}
			return result;
		}

		private static int GetDefaultOverflowMoneyRatePercent(int resourceId, int fallbackOtherRatePercent)
		{
			return (resourceId == 0) ? DefaultMoneyOverflowMoneyRatePercent : Math.Max(0, fallbackOtherRatePercent);
		}
	}

	/// <summary>
	/// 配置文件读写。路径固定在 UserData\ForceOverflowDividend.cfg，
	/// 与旧版（原 mod）完全一致，升级后用户已有配置继续生效。
	/// </summary>
	internal static class ForceOverflowConfigStore
	{
		private const string ConfigFileName = "ForceOverflowDividend.cfg";

		internal const string ConfigPathDisplay = "UserData\\ForceOverflowDividend.cfg";

		internal static readonly string ConfigPath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UserData", ConfigFileName));

		internal static ForceOverflowConfig Load(MelonLogger.Instance log)
		{
			ForceOverflowConfig forceOverflowConfig = ForceOverflowConfig.CreateDefault();
			bool shouldRewrite = false;
			try
			{
				EnsureDirectoryExists();
				shouldRewrite = !File.Exists(ConfigPath) || !TryReadConfig(File.ReadAllLines(ConfigPath), forceOverflowConfig, log);
			}
			catch (Exception ex)
			{
				log.Warning("读取配置失败，将回退默认配置并重写文件：" + ex.Message);
				forceOverflowConfig = ForceOverflowConfig.CreateDefault();
				shouldRewrite = true;
			}
			if (shouldRewrite)
			{
				WriteConfig(forceOverflowConfig, log);
			}
			return forceOverflowConfig;
		}

		private static bool TryReadConfig(IEnumerable<string> lines, ForceOverflowConfig config, MelonLogger.Instance log)
		{
			bool sawEnabled = false;
			bool sawVerbose = false;
			bool sawAnyResourceRate = false;
			bool isClean = true;
			int fallbackOtherResourceRatePercent = ForceOverflowConfig.DefaultOtherOverflowMoneyRatePercent;
			bool sawLegacyFallback = false;

			foreach (string line in lines)
			{
				string text = line?.Trim() ?? string.Empty;
				if (text.Length == 0 || text.StartsWith("#", StringComparison.Ordinal))
				{
					continue;
				}

				int num = text.IndexOf('=');
				if (num <= 0 || num == text.Length - 1)
				{
					log.Warning("发现无效配置行，将按默认值重写：" + text);
					isClean = false;
					continue;
				}

				string key = text.Substring(0, num).Trim();
				string value = text.Substring(num + 1).Trim();

				if (key.Equals("Enabled", StringComparison.OrdinalIgnoreCase))
				{
					if (bool.TryParse(value, out var enabled))
					{
						config.Enabled = enabled;
						sawEnabled = true;
					}
					else
					{
						log.Warning("Enabled 配置无效，将回退默认值：" + value);
						isClean = false;
					}
				}
				else if (key.Equals("VerboseLog", StringComparison.OrdinalIgnoreCase))
				{
					if (bool.TryParse(value, out var verbose))
					{
						config.VerboseLog = verbose;
						sawVerbose = true;
					}
					else
					{
						log.Warning("VerboseLog 配置无效，将回退默认值：" + value);
						isClean = false;
					}
				}
				else if (key.StartsWith("ResourceRate.", StringComparison.OrdinalIgnoreCase))
				{
					string idText = key.Substring("ResourceRate.".Length).Trim();
					if (!int.TryParse(idText, out var resourceId) || resourceId < 0)
					{
						log.Warning("资源比例配置项无效，将按默认值重写：" + key);
						isClean = false;
					}
					else if (TryParseNonNegativeInt(value, out var rate))
					{
						config.SetOverflowMoneyRatePercent(resourceId, rate);
						sawAnyResourceRate = true;
					}
					else
					{
						log.Warning(key + " 配置无效，将回退默认值：" + value);
						isClean = false;
					}
				}
				else if (key.Equals("NonMoneyOverflowMoneyRatePercent", StringComparison.OrdinalIgnoreCase))
				{
					// 旧版键名，保留兼容：读进来作为「其他资源」的兜底比例。
					if (TryParseNonNegativeInt(value, out var legacyRate))
					{
						fallbackOtherResourceRatePercent = legacyRate;
						sawLegacyFallback = true;
					}
					else
					{
						log.Warning("旧版 NonMoneyOverflowMoneyRatePercent 配置无效，将回退默认值：" + value);
					}
					isClean = false;
				}
				else
				{
					log.Warning("发现未知配置项，将保留其余有效配置并重写文件：" + key);
					isClean = false;
				}
			}

			if (sawLegacyFallback)
			{
				config.FallbackOtherResourceRatePercent = fallbackOtherResourceRatePercent;
			}
			if (!sawAnyResourceRate)
			{
				config.EnsureResourceRateCoverage(1);
				isClean = false;
			}

			return isClean & sawEnabled & sawVerbose;
		}

		/// <summary>
		/// 资源表加载后补齐配置项。原 mod 依赖此方法把「首次启动时资源表还没加载」
		/// 的空缺补全，缺了会导致只有 ResourceRate.0 一项、其他资源静默不折现。
		/// </summary>
		internal static void SyncKnownResourceRates(ForceOverflowConfig config, MelonLogger.Instance log)
		{
			int managedResourceCount = GetManagedResourceCount();
			if (config != null && managedResourceCount > 0 && config.EnsureResourceRateCoverage(managedResourceCount))
			{
				WriteConfig(config, log);
			}
		}

		private static bool TryParseNonNegativeInt(string value, out int parsed)
		{
			return int.TryParse(value, out parsed) && parsed >= 0;
		}

		private static void EnsureDirectoryExists()
		{
			string? directoryName = Path.GetDirectoryName(ConfigPath);
			if (!string.IsNullOrEmpty(directoryName))
			{
				Directory.CreateDirectory(directoryName);
			}
		}

		private static void WriteConfig(ForceOverflowConfig config, MelonLogger.Instance log)
		{
			try
			{
				EnsureDirectoryExists();
				config.EnsureResourceRateCoverage(1);

				List<string> list = new List<string>
				{
					"# ForceOverflowDividend 配置文件",
					"# 每次门派资源变动后立即检查资源溢出。",
					"# 除最后一项威望外，其他资源都可以单独配置溢出后折现为金钱的比例。",
					"# ResourceRate.<id> 表示该资源溢出部分按基础价值折现为金钱的百分比，允许为大于等于 0 的整数。",
					"# 默认规则为：钱资源 100%，其他资源 10%。如果首次生成时资源表尚未加载，后续进入游戏后会自动补齐全部可处理资源项。",
					"# VerboseLog 表示是否输出详细过程日志。",
					"Enabled=" + config.Enabled.ToString().ToLowerInvariant(),
					"VerboseLog=" + config.VerboseLog.ToString().ToLowerInvariant(),
					string.Empty,
					"# 资源折现比例"
				};

				int managedResourceCount = GetManagedResourceCount();
				if (managedResourceCount > 0)
				{
					config.EnsureResourceRateCoverage(managedResourceCount);
				}

				SortedSet<int> sortedSet = new SortedSet<int>(config.ResourceOverflowMoneyRatePercentById.Keys);
				if (managedResourceCount > 0)
				{
					for (int i = 0; i < managedResourceCount; i++)
					{
						sortedSet.Add(i);
					}
				}

				foreach (int item in sortedSet)
				{
					list.Add($"# {item}: {GetResourceName(item)}");
					list.Add($"ResourceRate.{item}={config.GetOverflowMoneyRatePercent(item)}");
				}

				File.WriteAllLines(ConfigPath, list);
			}
			catch (Exception value)
			{
				log.Error($"写入配置失败：{ConfigPath}，{value}");
			}
		}

		private static string GetResourceName(int resourceId)
		{
			try
			{
				var names = GlobalData.ResourceName;
				if (names == null || resourceId < 0 || resourceId >= names.Count)
				{
					return $"资源{resourceId}";
				}
				return names[resourceId] ?? $"资源{resourceId}";
			}
			catch
			{
				return $"资源{resourceId}";
			}
		}

		/// <summary>可折现资源数量 = 资源表条目数 - 1（最后一项是威望，不折现）。</summary>
		private static int GetManagedResourceCount()
		{
			try
			{
				var names = GlobalData.ResourceName;
				if (names == null || names.Count <= 0)
				{
					return 0;
				}
				return Math.Max(0, names.Count - 1);
			}
			catch
			{
				return 0;
			}
		}
	}
}
