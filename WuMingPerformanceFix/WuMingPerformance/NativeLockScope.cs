namespace WuMingPerformance;

internal sealed class NativeLockScope : System.IDisposable
{
	private const string Il2CppDll = "GameAssembly";

	private readonly System.IntPtr _obj;

	private int _holdCount;

	internal int HoldCount => _holdCount;

	[System.Runtime.InteropServices.DllImport("GameAssembly", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
	private static extern void il2cpp_monitor_enter(System.IntPtr obj);

	[System.Runtime.InteropServices.DllImport("GameAssembly", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
	private static extern void il2cpp_monitor_exit(System.IntPtr obj);

	[System.Runtime.InteropServices.DllImport("GameAssembly", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
	[return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.I1)]
	private static extern bool il2cpp_monitor_try_enter(System.IntPtr obj, uint timeoutMs);

	private NativeLockScope(System.IntPtr obj)
	{
		_obj = obj;
	}

	internal static WuMingPerformance.NativeLockScope? TryEnter(Il2CppSystem.Object target, string nameForLog)
	{
		if (target == null)
		{
			MelonLoader.MelonLogger.Error("[NativeLock] " + nameForLog + ": lock 目标为 null");
			return null;
		}
		try
		{
			System.IntPtr obj = Il2CppInterop.Runtime.IL2CPP.Il2CppObjectBaseToPtrNotNull(target);
			il2cpp_monitor_enter(obj);
			return new WuMingPerformance.NativeLockScope(obj)
			{
				_holdCount = 1
			};
		}
		catch (System.Exception ex)
		{
			MelonLoader.MelonLogger.Error("[NativeLock] " + nameForLog + ": monitor_enter 失败: " + ex.Message);
			return null;
		}
	}

	internal static WuMingPerformance.NativeLockScope? TryEnterTimeout(Il2CppSystem.Object target, uint timeoutMs, string nameForLog)
	{
		if (target == null)
		{
			MelonLoader.MelonLogger.Error("[NativeLock] " + nameForLog + ": lock 目标为 null");
			return null;
		}
		try
		{
			System.IntPtr obj = Il2CppInterop.Runtime.IL2CPP.Il2CppObjectBaseToPtrNotNull(target);
			if (!il2cpp_monitor_try_enter(obj, timeoutMs))
			{
				return null;
			}
			return new WuMingPerformance.NativeLockScope(obj)
			{
				_holdCount = 1
			};
		}
		catch (System.Exception ex)
		{
			MelonLoader.MelonLogger.Error("[NativeLock] " + nameForLog + ": monitor_try_enter 异常: " + ex.Message);
			return null;
		}
	}

	internal int ReleaseAll()
	{
		int holdCount = _holdCount;
		while (_holdCount > 0)
		{
			_holdCount--;
			try
			{
				il2cpp_monitor_exit(_obj);
			}
			catch (System.Exception ex)
			{
				MelonLoader.MelonLogger.Error("[NativeLock] monitor_exit 失败（可能锁所有权不匹配）: " + ex.Message);
			}
		}
		return holdCount;
	}

	internal void Reacquire(int count)
	{
		for (int i = 0; i < count; i++)
		{
			il2cpp_monitor_enter(_obj);
			_holdCount++;
		}
	}

	public void Dispose()
	{
		ReleaseAll();
	}
}
