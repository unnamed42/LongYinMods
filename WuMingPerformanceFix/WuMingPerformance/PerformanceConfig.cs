namespace WuMingPerformance;

internal static class PerformanceConfig
{
	private const string Category = "WuMingPerformance";

	private static readonly string ConfigPath = System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "UserData", "WuMingPerformance.cfg");

	private static MelonLoader.MelonPreferences_Category? _category;

	private static MelonLoader.MelonPreferences_Entry<bool>? _aiGateEnabled;

	private static MelonLoader.MelonPreferences_Entry<float>? _aiMinSubmitInterval;

	private static MelonLoader.MelonPreferences_Entry<bool>? _asyncSaveEnabled;

	private static MelonLoader.MelonPreferences_Entry<bool>? _asyncForceStuffEnabled;

	private static MelonLoader.MelonPreferences_Entry<bool>? _asyncHeroQuitEnterEnabled;

	private static MelonLoader.MelonPreferences_Entry<bool>? _asyncMonthTaskEnabled;

	private static MelonLoader.MelonPreferences_Entry<bool>? _aiScanSliceEnabled;

	private static MelonLoader.MelonPreferences_Entry<int>? _aiScanSliceSize;

	private static MelonLoader.MelonPreferences_Entry<bool>? _diagnosticsEnabled;

	private static MelonLoader.MelonPreferences_Entry<bool>? _infoFloodControlEnabled;

	private static MelonLoader.MelonPreferences_Entry<int>? _infoFloodMaxPerFrame;

	private static MelonLoader.MelonPreferences_Entry<int>? _infoFloodRefillPerFrame;

	private static MelonLoader.MelonPreferences_Entry<int>? _infoTabVisibleBudget;

	private static MelonLoader.MelonPreferences_Entry<bool>? _lateUpdateBudgetEnabled;

	private static MelonLoader.MelonPreferences_Entry<int>? _lateUpdateRemoveBudget;

	internal static bool AIGateEnabled => _aiGateEnabled?.Value ?? true;

	internal static float AIMinSubmitInterval
	{
		get
		{
			float num = _aiMinSubmitInterval?.Value ?? 0.5f;
			if (!(num < 0f))
			{
				if (!(num > 5f))
				{
					return num;
				}
				return 5f;
			}
			return 0f;
		}
	}

	internal static bool AsyncSaveEnabled => _asyncSaveEnabled?.Value ?? true;

	internal static bool AsyncForceStuffEnabled => _asyncForceStuffEnabled?.Value ?? true;

	internal static bool AsyncHeroQuitEnterEnabled => _asyncHeroQuitEnterEnabled?.Value ?? true;

	internal static bool AsyncMonthTaskEnabled => _asyncMonthTaskEnabled?.Value ?? true;

	internal static bool AIScanSliceEnabled => _aiScanSliceEnabled?.Value ?? true;

	internal static int AIScanSliceSize
	{
		get
		{
			int num = _aiScanSliceSize?.Value ?? 150;
			if (num >= 30)
			{
				if (num <= 500)
				{
					return num;
				}
				return 500;
			}
			return 30;
		}
	}

	internal static bool DiagnosticsEnabled => _diagnosticsEnabled?.Value ?? true;

	internal static bool InfoFloodControlEnabled => _infoFloodControlEnabled?.Value ?? true;

	internal static int InfoFloodMaxPerFrame
	{
		get
		{
			int num = _infoFloodMaxPerFrame?.Value ?? 8;
			if (num >= 2)
			{
				if (num <= 30)
				{
					return num;
				}
				return 30;
			}
			return 2;
		}
	}

	internal static int InfoFloodRefillPerFrame
	{
		get
		{
			int num = _infoFloodRefillPerFrame?.Value ?? 6;
			if (num >= 1)
			{
				if (num <= 20)
				{
					return num;
				}
				return 20;
			}
			return 1;
		}
	}

	internal static int InfoTabVisibleBudget
	{
		get
		{
			int num = _infoTabVisibleBudget?.Value ?? 12;
			if (num >= 4)
			{
				if (num <= 40)
				{
					return num;
				}
				return 40;
			}
			return 4;
		}
	}

	internal static bool LateUpdateBudgetEnabled => _lateUpdateBudgetEnabled?.Value ?? true;

	internal static int LateUpdateRemoveBudget
	{
		get
		{
			int num = _lateUpdateRemoveBudget?.Value ?? 2;
			if (num >= 1)
			{
				if (num <= 10)
				{
					return num;
				}
				return 10;
			}
			return 1;
		}
	}

	internal static string FilePath => ConfigPath;

	internal static void Initialize()
	{
		try
		{
			_category = MelonLoader.MelonPreferences.CreateCategory("WuMingPerformance");
			_category.SetFilePath(ConfigPath);
			_category.LoadFromFile(printmsg: false);
			_aiGateEnabled = _category.CreateEntry("AIGateEnabled", default_value: true, "启用 AI 全量扫描频率闸");
			_aiMinSubmitInterval = _category.CreateEntry("AIMinSubmitInterval", 0.5f, "AI 扫描最小间隔(秒, 0~5)");
			_asyncSaveEnabled = _category.CreateEntry("AsyncSaveEnabled", default_value: true, "启用异步存档(写盘移出主线程)");
			_asyncForceStuffEnabled = _category.CreateEntry("AsyncForceStuffEnabled", default_value: true, "过日门派事务后台化(ManageForceStuff)");
			_asyncHeroQuitEnterEnabled = _category.CreateEntry("AsyncHeroQuitEnterEnabled", default_value: true, "过月退隐招募后台化(AQAE)");
			_asyncMonthTaskEnabled = _category.CreateEntry("AsyncMonthTaskEnabled", default_value: true, "原版月度任务延后到主线程过日后(ManageMonthTask)");
			_aiScanSliceEnabled = _category.CreateEntry("AIScanSliceEnabled", default_value: true, "AI 扫描切片化(主线程等锁 ≤ 一片)");
			_aiScanSliceSize = _category.CreateEntry("AIScanSliceSize", 150, "AI 扫描每片角色数(30~500)");
			_diagnosticsEnabled = _category.CreateEntry("DiagnosticsEnabled", default_value: false, "输出过日/过月耗时诊断日志([诊断] 前缀)");
			_infoFloodControlEnabled = _category.CreateEntry("InfoFloodControlEnabled", default_value: true, "消息洪峰限流(InfoController)");
			_infoFloodMaxPerFrame = _category.CreateEntry("InfoFloodMaxPerFrame", 8, "每帧每队列最多消化消息数(2~30)");
			_infoFloodRefillPerFrame = _category.CreateEntry("InfoFloodRefillPerFrame", 6, "暂存消息每帧回放数(1~20)");
			_infoTabVisibleBudget = _category.CreateEntry("InfoTabVisibleBudget", 12, "弹窗tab共存预算(4~40, 超出压着等过期)");
			_lateUpdateBudgetEnabled = _category.CreateEntry("LateUpdateBudgetEnabled", default_value: true, "LateUpdate 脏数据后台化(CountAreaData等延迟重算)");
			_lateUpdateRemoveBudget = _category.CreateEntry("LateUpdateRemoveBudget", 2, "每周期角色删除上限(1~10)");
			if (DiagnosticsEnabled)
			{
				MelonLoader.MelonLogger.Msg("[性能优化] 配置已加载: " + ConfigPath);
			}
		}
		catch (System.Exception ex)
		{
			MelonLoader.MelonLogger.Warning("[性能优化] 配置初始化失败，使用默认值: " + ex.Message);
		}
	}

	internal static void Save()
	{
		try
		{
			_category?.SaveToFile(printmsg: false);
			if (DiagnosticsEnabled)
			{
				MelonLoader.MelonLogger.Msg("[性能优化] 配置已保存: " + ConfigPath);
			}
		}
		catch (System.Exception ex)
		{
			MelonLoader.MelonLogger.Warning("[性能优化] 配置保存失败: " + ex.Message);
		}
	}

	internal static string Describe()
	{
		return $"AI闸={AIGateEnabled}, AI最小间隔={AIMinSubmitInterval:F2}s, 异步存档={AsyncSaveEnabled}, Force后台={AsyncForceStuffEnabled}, AQAE后台={AsyncHeroQuitEnterEnabled}";
	}
}
