namespace WuMingPerformance.Patches;

internal static class WorldAsyncContext
{
	[System.ThreadStatic]
	internal static bool InBackgroundExecution;

	[System.ThreadStatic]
	internal static bool IsWorkerThread;

	[System.ThreadStatic]
	internal static bool DeferRecruit;
}
