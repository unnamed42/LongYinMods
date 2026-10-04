namespace WuMingPerformance;

internal readonly struct Il2CppThreadScope : System.IDisposable
{
	private const string Il2CppDll = "GameAssembly";

	private readonly System.IntPtr _thread;

	private readonly bool _owns;

	[System.Runtime.InteropServices.DllImport("GameAssembly", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
	private static extern System.IntPtr il2cpp_domain_get();

	[System.Runtime.InteropServices.DllImport("GameAssembly", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
	private static extern System.IntPtr il2cpp_thread_attach(System.IntPtr domain);

	[System.Runtime.InteropServices.DllImport("GameAssembly", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
	private static extern void il2cpp_thread_detach(System.IntPtr thread);

	[System.Runtime.InteropServices.DllImport("GameAssembly", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
	private static extern System.IntPtr il2cpp_thread_current();

	private Il2CppThreadScope(System.IntPtr thread, bool owns)
	{
		_thread = thread;
		_owns = owns;
	}

	internal static bool TryAttach(out WuMingPerformance.Il2CppThreadScope scope)
	{
		scope = default;
		try
		{
			if (il2cpp_thread_current() != System.IntPtr.Zero)
			{
				scope = new WuMingPerformance.Il2CppThreadScope(System.IntPtr.Zero, owns: false);
				return true;
			}
			System.IntPtr intPtr = il2cpp_domain_get();
			if (intPtr == System.IntPtr.Zero)
			{
				MelonLoader.MelonLogger.Error("[Il2CppThreadScope] il2cpp_domain_get 返回 null");
				return false;
			}
			System.IntPtr intPtr2 = il2cpp_thread_attach(intPtr);
			if (intPtr2 == System.IntPtr.Zero)
			{
				MelonLoader.MelonLogger.Error("[Il2CppThreadScope] il2cpp_thread_attach 失败");
				return false;
			}
			scope = new WuMingPerformance.Il2CppThreadScope(intPtr2, owns: true);
			return true;
		}
		catch (System.Exception ex)
		{
			MelonLoader.MelonLogger.Error("[Il2CppThreadScope] attach 异常: " + ex.Message);
			return false;
		}
	}

	public void Dispose()
	{
		if (!_owns || _thread == System.IntPtr.Zero)
		{
			return;
		}
		try
		{
			il2cpp_thread_detach(_thread);
		}
		catch (System.Exception ex)
		{
			MelonLoader.MelonLogger.Error("[Il2CppThreadScope] detach 异常: " + ex.Message);
		}
	}
}
