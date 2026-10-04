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
	/// 构建指纹：静态版本号 + 自身 md5，启动日志打印。
	///
	/// ★ 不再读 InformationalVersion：它曾是「构建时刻」的载体，
	///   但时间戳进程序集内容会破坏 &lt;Deterministic&gt;，
	///   同一份源码两次构建 checksum 不同，md5 根本没法比较（已移除）。
	///   「跑的是不是新产物」现在以 md5 为准。
	/// 本项目因为「跑的是旧产物」白耗过两轮，症状与真正的逻辑 bug 无法区分。
	/// </summary>
	internal static class BuildInfo
	{
		internal static readonly string BuildStamp = Read();

		private static string Read()
		{
			try
			{
				var asm = typeof(BuildInfo).Assembly;
				string v = asm.GetName().Version?.ToString() ?? "unknown";

				return string.IsNullOrEmpty(asm.Location)
					? v
					: $"{v} md5={AssemblyVersionHash.OfFile(asm.Location)}";
			}
			catch
			{
				// 诊断信息绝不能影响启动。
				return "unknown";
			}
		}
	}
}
