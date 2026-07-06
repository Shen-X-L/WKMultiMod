using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using HarmonyLib.Tools;
using Steamworks;
using System;
using System.IO;
using System.Reflection;
using UnityEngine;
using WKMPMod.Util;

namespace WKMPMod.Core;

[BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
public class MPMain : BaseUnityPlugin {

	public const string PLUGIN_GUID = "shenxl.MultiPlayerMod";
	public const string PLUGIN_NAME = "MultiPlayer Mod";
	public const string PLUGIN_VERSION = "1.7.6";
	//Assembly.GetExecutingAssembly().Location -> BepInEx\plugins\MultiPlayer\WKMultiPlayerMod.dll
	//Path.GetDirectoryName -> BepInEx\plugins\MultiPlayer
	public static string path = Path.GetDirectoryName(typeof(MPMain).Assembly.Location) ?? string.Empty;
	// 单例实例
	public static MPMain Instance { get; set; }
	// 日志记录器
	internal static new ManualLogSource Logger;
	// Harmony上下文
	private Harmony _harmony;
	// 核心实例访问器
	public static MPCore Core => MPCore.Instance;
	// 蛞蝓猫手部皮肤ID 和 身体皮肤ID
	public const string SLUGCAT_HAND_ID = "slugcat hands";
	public const string SLUGCAT_BODY_FACTORY_ID = "slugcat";


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
		Logger.LogInfo($"[MPMain] {PLUGIN_GUID} {PLUGIN_VERSION} loaded");

		// 使用Harmony打补丁
		_harmony = new Harmony($"{PLUGIN_GUID}");
		_harmony.PatchAll();

		// 配置初始化
		MPConfig.Initialize(base.Config);

		// 文本配置
		Localization.Load();
	}

	private void OnDestroy() {
		LogInfo(Localization.Get("MPMain.Destroy"));
	}

	public static void LogInfo(string log = "") {
		Logger.LogInfo(log);
	}

	public static void LogWarning(string log = "") {
		Logger.LogWarning(log);
	}
	public static void LogError(string log = "") {
		Logger.LogError(log);
	}

	public static void LogDebug(string log = "") {
		Logger.LogDebug("[MP Debug] "+log);
	}

	public static void LogTest(string log = "") {
		Logger.LogWarning("[MP Test] " + log);
	}
}
