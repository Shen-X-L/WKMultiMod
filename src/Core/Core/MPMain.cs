using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using HarmonyLib.Tools;
using Steamworks;
using System;
using UnityEngine;
using WKMPMod.Util;

namespace WKMPMod.Core;

[BepInPlugin(ModGUID, ModName, ModVersion)]
public class MPMain : BaseUnityPlugin {

	public const string ModGUID = "shenxl.MultiPlayerMod";
	public const string ModName = "MultiPlayer Mod";
	public const string ModVersion = "1.1.3.6";

	public static string path = Paths.PluginPath;

	// 单例实例
	public static MPMain Instance { get; set; }

	// 日志记录器
	internal static new ManualLogSource Logger;

	// Harmony上下文
	private Harmony _harmony;

	// 核心实例访问器
	public static MPCore Core => MPCore.Instance;

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
		Logger.LogInfo($"[MPMain] {ModGUID} {ModVersion} loaded");

		//// 日后生命周期完善时使用这个单例创建
		//// 1. 创建一个新的, GameObject
		//GameObject coreGameObject = new GameObject("MultiplayerCore");

		//// 2. 立即保护新对象 (被游戏创建初期销毁了,为什么?)
		//DontDestroyOnLoad(coreGameObject);

		//// 添加组件
		//coreGameObject.AddComponent<MPCore>();

		// 使用Harmony打补丁
		_harmony = new Harmony($"{ModGUID}");
		_harmony.PatchAll();

		// 配置初始化
		MPConfig.Initialize(base.Config);

		// 文本配置
		Localization.Load();
	}

	private void OnDestroy() {
		LogInfo(Localization.Get("MPMain", "Destroy"));
	}

	public static void LogInfo(string log) {
		Logger.LogInfo(log);
	}

	public static void LogWarning(string log) {
		Logger.LogWarning(log);
	}
	public static void LogError(string log) {
		Logger.LogError(log);
	}
}
