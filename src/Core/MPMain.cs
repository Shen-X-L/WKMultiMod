using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using WKMultiMod.src.Core;
using Steamworks;
using System;
using UnityEngine;

namespace WKMultiMod.src.Core;

[BepInPlugin(ModGUID, ModName, ModVersion)]
public class MPMain : BaseUnityPlugin {

	public const string ModGUID = "shenxl.MultiPalyerMod";
	public const string ModName = "MultiPalyer Mod";
	public const string ModVersion = "0.13.1.11";

	// 单例实例
	public static MPMain Instance { get; set; }

	// 日志记录器
	internal static new ManualLogSource Logger;

	// Harmony上下文
	private Harmony _harmony;

	// 核心实例访问器
	public static MultiPlayerCore Core => MultiPlayerCore.Instance;

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
		Logger.LogInfo($"[MP Mod loading] {ModGUID} {ModVersion} 已加载");

		//// 日后生命周期完善时使用这个单例创建
		//// 1. 创建一个新的, GameObject
		//GameObject coreGameObject = new GameObject("MultiplayerCore_DDOL");

		//// 2. 立即保护新对象 (被游戏创建初期销毁了,为什么?)
		//DontDestroyOnLoad(coreGameObject);

		//// 3. 添加该组件
		//coreGameObject.AddComponent<MPMain>();

		// 使用Harmony打补丁
		_harmony = new Harmony($"{ModGUID}");
		_harmony.PatchAll();
	}

	private void OnDestroy() {
		Logger.LogInfo("[MP Mod loading]MultiPalyerMain (启动器) 已被销毁.");
	}
}
