namespace WuMingPerformance;

internal static class MainThreadDispatcher
{
	private static readonly System.Collections.Generic.Queue<System.Action> _pending = new System.Collections.Generic.Queue<System.Action>();

	internal static void Post(System.Action action)
	{
		if (action == null)
		{
			return;
		}
		lock (_pending)
		{
			_pending.Enqueue(action);
		}
	}

	internal static void Drain()
	{
		while (true)
		{
			System.Action action;
			lock (_pending)
			{
				if (_pending.Count == 0)
				{
					break;
				}
				action = _pending.Dequeue();
			}
			try
			{
				action();
			}
			catch (System.Exception ex)
			{
				MelonLoader.MelonLogger.Error("[性能优化] 主线程回调异常: " + ex.Message + "\n" + ex.StackTrace);
			}
		}
	}
}
