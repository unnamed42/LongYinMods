namespace WuMingPerformance.Patches;

internal static class DiagnosticsState
{
	[System.ThreadStatic]
	internal static System.Diagnostics.Stopwatch? SwDay;

	[System.ThreadStatic]
	internal static System.Diagnostics.Stopwatch? SwMonth;

	[System.ThreadStatic]
	internal static System.Diagnostics.Stopwatch? SwArea;

	[System.ThreadStatic]
	internal static System.Diagnostics.Stopwatch? SwMonthTask;

	[System.ThreadStatic]
	internal static System.Diagnostics.Stopwatch? SwInfoUpdate;

	internal static float ChangeDayStartRealtime = -1f;

	internal static bool Enabled => WuMingPerformance.PerformanceConfig.DiagnosticsEnabled;
}
