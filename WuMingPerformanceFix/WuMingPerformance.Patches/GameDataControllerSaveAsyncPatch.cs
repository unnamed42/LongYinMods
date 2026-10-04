namespace WuMingPerformance.Patches;

[HarmonyLib.HarmonyPatch(typeof(Il2Cpp.GameDataController), "Save")]
internal static class GameDataControllerSaveAsyncPatch
{
	private static int _saving;

	private static bool _firstSaveLogged;

	private static volatile System.Threading.Tasks.Task? _pendingWrite;

	[System.ThreadStatic]
	private static bool _fallbackInProgress;

	internal static bool IsSaving => System.Threading.Volatile.Read(ref _saving) != 0;

	internal static bool FallbackInProgress => _fallbackInProgress;

	internal static void WaitForPendingSave(int timeoutMs = 15000)
	{
		System.Threading.Tasks.Task pendingWrite = _pendingWrite;
		if (pendingWrite == null || pendingWrite.IsCompleted)
		{
			return;
		}
		try
		{
			if (!pendingWrite.Wait(timeoutMs))
			{
				MelonLoader.MelonLogger.Error($"[SaveAsync] 后台写盘超过 {timeoutMs}ms 未完成，存档可能不完整");
			}
		}
		catch (System.Exception ex)
		{
			MelonLoader.MelonLogger.Error("[SaveAsync] 等待后台写盘异常: " + ex.Message);
		}
	}

	internal static bool Prefix(Il2Cpp.GameDataController __instance, int saveID)
	{
		if (!WuMingPerformance.PerformanceConfig.AsyncSaveEnabled)
		{
			return true;
		}
		if (FallbackInProgress)
		{
			return true;
		}
		Il2Cpp.GameSaveData saveData = __instance.gameSaveData;
		if (saveData == null || !saveData.CheckAllFinished())
		{
			return false;
		}
		if (WuMingPerformance.Patches.BackgroundWorldTasks.HasRunning)
		{
			if (WuMingPerformance.PerformanceConfig.DiagnosticsEnabled)
			{
				MelonLoader.MelonLogger.Msg("[诊断] 世界结算后台任务进行中，本次存档推迟");
			}
			return false;
		}
		if (System.Threading.Interlocked.CompareExchange(ref _saving, 1, 0) != 0)
		{
			MelonLoader.MelonLogger.Warning("[SaveAsync] 上一次存档写盘尚未结束，本次存档请求已忽略");
			return false;
		}
		if (!_firstSaveLogged && WuMingPerformance.PerformanceConfig.DiagnosticsEnabled)
		{
			_firstSaveLogged = true;
			MelonLoader.MelonLogger.Msg("[SaveAsync] 已接管存档：序列化 + 写盘 + 备份复制已移出主线程");
		}
		System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
		string[] paths = new string[4];
		string[] backups = new string[4];
		float prepMs = -1f;
		try
		{
			__instance.GameIntoGameData();
			if (saveData.WorldData == null)
			{
				MelonLoader.MelonLogger.Error("[SaveAsync] worldData 为空，存档取消");
				ReleaseSaving();
				return false;
			}
			saveData.SetAllUnfinish(_loading: false);
			for (int i = 0; i < 4; i++)
			{
				paths[i] = __instance.GetSaveDataPath(saveID, i);
				backups[i] = __instance.GetBackupDataPath(saveID, i);
			}
			__instance.SavePlayerprefData();
			stopwatch.Stop();
			prepMs = (float)stopwatch.Elapsed.TotalMilliseconds;
		}
		catch (System.Exception ex)
		{
			MelonLoader.MelonLogger.Error("[SaveAsync] 存档准备失败: " + ex.Message + "\n" + ex.StackTrace);
			FailSave(saveData, saveID);
			return false;
		}
		_pendingWrite = System.Threading.Tasks.Task.Factory.StartNew(() =>
		{
			if (!WuMingPerformance.Il2CppThreadScope.TryAttach(out var scope))
			{
				MelonLoader.MelonLogger.Error("[SaveAsync] 后台线程 attach il2cpp 失败，回退为主线程存档");
				WuMingPerformance.MainThreadDispatcher.Post(() =>
				{
					FallbackSyncSave(__instance, saveData, saveID);
				});
				ReleaseSaving();
				return;
			}
			try
			{
				System.Diagnostics.Stopwatch stopwatch2 = System.Diagnostics.Stopwatch.StartNew();
				System.IO.File.WriteAllText(paths[3], Il2CppNewtonsoft.Json.JsonConvert.SerializeObject(__instance.GenerateSaveInfo()));
				System.IO.File.WriteAllText(paths[0], Il2CppNewtonsoft.Json.JsonConvert.SerializeObject(saveData.WorldData));
				System.IO.File.WriteAllText(paths[1], Il2CppNewtonsoft.Json.JsonConvert.SerializeObject(saveData.HeroList));
				System.IO.File.WriteAllText(paths[2], Il2CppNewtonsoft.Json.JsonConvert.SerializeObject(saveData.TempHeroList));
				for (int num = 0; num < 4; num++)
				{
					if (System.IO.File.Exists(paths[num]))
					{
						if (System.IO.File.Exists(backups[num]))
						{
							System.IO.File.Delete(backups[num]);
						}
						System.IO.File.Copy(paths[num], backups[num]);
					}
				}
				stopwatch2.Stop();
				float writeMs = (float)stopwatch2.Elapsed.TotalMilliseconds;
				WuMingPerformance.MainThreadDispatcher.Post(() =>
				{
					FinishSave(saveData, saveID, failed: false, prepMs, writeMs);
				});
			}
			catch (System.Exception ex2)
			{
				MelonLoader.MelonLogger.Error("[SaveAsync] 存档写盘失败: " + ex2.Message + "\n" + ex2.StackTrace);
				WuMingPerformance.MainThreadDispatcher.Post(() =>
				{
					FinishSave(saveData, saveID, failed: true);
				});
			}
			finally
			{
				scope.Dispose();
				ReleaseSaving();
			}
		}, System.Threading.Tasks.TaskCreationOptions.LongRunning);
		return false;
	}

	private static void ReleaseSaving()
	{
		System.Threading.Interlocked.Exchange(ref _saving, 0);
	}

	private static void FallbackSyncSave(Il2Cpp.GameDataController instance, Il2Cpp.GameSaveData saveData, int saveID)
	{
		try
		{
			saveData.worldDataFinished = true;
			saveData.heroListFinished = true;
			saveData.tempHeroListFinished = true;
			saveData.saveFailed = false;
			bool fallbackInProgress = _fallbackInProgress;
			_fallbackInProgress = true;
			try
			{
				instance.Save(saveID);
			}
			finally
			{
				_fallbackInProgress = fallbackInProgress;
			}
		}
		catch (System.Exception ex)
		{
			MelonLoader.MelonLogger.Error("[SaveAsync] 同步兜底存档失败: " + ex.Message);
			FinishSave(saveData, saveID, failed: true);
		}
	}

	private static void FailSave(Il2Cpp.GameSaveData saveData, int saveID)
	{
		ReleaseSaving();
		FinishSave(saveData, saveID, failed: true);
	}

	private static void FinishSave(Il2Cpp.GameSaveData saveData, int saveID, bool failed, float prepMs = -1f, float writeMs = -1f)
	{
		try
		{
			if (failed)
			{
				saveData.SetSaveFailed();
			}
			else
			{
				saveData.worldDataFinished = true;
				saveData.heroListFinished = true;
				saveData.tempHeroListFinished = true;
				saveData.saveFailed = false;
			}
		}
		catch (System.Exception ex)
		{
			MelonLoader.MelonLogger.Error("[SaveAsync] 回写存档状态失败: " + ex.Message);
		}
		if (!failed && prepMs >= 0f && WuMingPerformance.PerformanceConfig.DiagnosticsEnabled)
		{
			string text = saveID switch
			{
				10 => "快速", 
				0 => "自动", 
				_ => "手动", 
			};
			MelonLoader.MelonLogger.Msg("[SaveAsync] " + text + "存档完成: 主线程准备 " + prepMs.ToString("F0") + "ms + 后台写盘 " + writeMs.ToString("F0") + "ms");
		}
		try
		{
			string arg = saveID switch
			{
				10 => "快速", 
				0 => "自动", 
				_ => "", 
			};
			Il2Cpp.InfoController.Instance?.AddInfoTab(string.Format(failed ? "{0}存档失败，请重试！" : "{0}存档成功！", arg), "UIAtlas", "从事工作_学习", "Woosh", 1f, 5f, failed ? UnityEngine.Color.red : UnityEngine.Color.green);
			Il2Cpp.NGUITools.PlaySound(UnityEngine.Resources.Load(failed ? "Sound/SoundEffect/Fail" : "Sound/SoundEffect/NoticeLittle") as UnityEngine.AudioClip);
		}
		catch (System.Exception ex2)
		{
			MelonLoader.MelonLogger.Error("[SaveAsync] 存档提示失败: " + ex2.Message);
		}
	}
}
