using System.Reflection;
using MelonLoader;

namespace ForceOverflowDividend
{
	public sealed class Main : MelonMod
	{
		internal static MelonLogger.Instance Log => Melon<Main>.Logger;

		internal static bool VerboseLog => ForceOverflowRuntime.Config.VerboseLog;

		public override void OnInitializeMelon()
		{
			ForceOverflowRuntime.Initialize(ForceOverflowConfigStore.Load(Log));
			Log.Msg($"ForceOverflowDividend 已加载（构建 {BuildInfo.BuildStamp}）。配置文件：{ForceOverflowConfigStore.ConfigPathDisplay}。启用={FormatEnabled(ForceOverflowRuntime.Config.Enabled)}，资源折现比例可通过 ResourceRate.<id> 单独配置，默认钱=100%，其他=10%，VerboseLog={FormatEnabled(VerboseLog)}。");
		}

		internal static void LogVerbose(string message)
		{
			if (VerboseLog)
			{
				Log.Msg(message);
			}
		}

		private static string FormatEnabled(bool value)
		{
			return value ? "开" : "关";
		}
	}

	/// <summary>
	/// 构建指纹，由 csproj 在编译时写入 AssemblyInformationalVersion（见 BuildStamp 属性组）。
	/// 启动日志会打出来 —— 本项目因为「跑的是旧产物」白耗过两轮，
	/// 症状与真正的逻辑 bug 无法区分，有时间戳一眼就能判断跑的是不是新产物。
	/// </summary>
	internal static class BuildInfo
	{
		internal static readonly string BuildStamp =
			typeof(BuildInfo).Assembly
				.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
				?.InformationalVersion ?? "unknown";
	}
}
