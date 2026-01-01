using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Steamworks;
using System;
using UnityEngine;
using WKMultiMod.src.Core;

namespace WKMultiMod.src.Core;

[BepInPlugin(ModGUID, ModName, ModVersion)]
public class MPMain : BaseUnityPlugin {

	public const string ModGUID = "shenxl.MultiPlayerMod";
	public const string ModName = "MultiPlayer Mod";
	public const string ModVersion = "0.14.4.5";

	// 单例实例
	public static MPMain Instance { get; set; }

	// 日志记录器
	internal static new ManualLogSource Logger;

	// Harmony上下文
	private Harmony _harmony;

	// 核心实例访问器
	public static MPCore Core => MPCore.Instance;

	// Debug日志语言类型
	private static ConfigEntry<int> _debugLogLanguage;
	public static int DebugLogLanguage {
		get { return _debugLogLanguage.Value; }
	}


	// 头顶名称标签字体最大值
	private static ConfigEntry<float> _nameTagSizeMax;
	// 头顶名称标签字体最小值
	private static ConfigEntry<float> _nameTagSizeMin;

	public static float NameTagSizeMax {
		get { return _nameTagSizeMax.Value; }
	}
	public static float NameTagSizeMin {
		get { return _nameTagSizeMin.Value; }
	}

	// Awake在对象创建时调用, 早于Start
	private void Awake() {
		// 单例检查
		if (Instance != null) {
			Destroy(this);
			return;
		}
		Instance = this;

		// 日志初始化
		Logger = base.Logger;
		Logger.LogInfo($"[MPMain] {ModGUID} {ModVersion} 已加载");

		_debugLogLanguage = Config.Bind<int>(
			"Debug", "LogLanguage", 1,
			"值为0时使用中文输出日志, Use English logs when the value is 1.");
		_nameTagSizeMax = Config.Bind<float>(
			"RemotePlayer", "NameTagSizeMax", 0.3f,
			"This value sets the maximum size for player name tags above their heads.");

		_nameTagSizeMin = Config.Bind<float>(
			"RemotePlayer", "NameTagSizeMin", 0.15f,
			"This value sets the minimum size for player name tags above their heads.");


		//// 日后生命周期完善时使用这个单例创建
		//// 1. 创建一个新的, GameObject
		//GameObject coreGameObject = new GameObject("MultiplayerCore_DDOL");

		//// 2. 立即保护新对象 (被游戏创建初期销毁了,为什么?)
		//DontDestroyOnLoad(coreGameObject);

		// 使用Harmony打补丁
		_harmony = new Harmony($"{ModGUID}");
		_harmony.PatchAll();
	}

	private void OnDestroy() {
		Logger.LogInfo("[MPMain] MultiPalyerMain (启动器) 已被销毁.");
	}

	public static void LogInfo(string chineseLog, string englishLog) {
		if (_debugLogLanguage.Value == 0) Logger.LogInfo(chineseLog);
		else Logger.LogInfo(englishLog);
	}

	public static void LogWarning(string chineseLog, string englishLog) {
		if (_debugLogLanguage.Value == 0) Logger.LogWarning(chineseLog);
		else Logger.LogWarning(englishLog);
	}

	public static void LogError(string chineseLog, string englishLog) {
		if (_debugLogLanguage.Value == 0) Logger.LogError(chineseLog);
		else Logger.LogError(englishLog);
	}
}
