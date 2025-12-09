using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using WKMultiMod.Core;

namespace WKMultiMod.Main;

[BepInPlugin(ModGUID, ModName, ModVersion)]
public class MultiPlayerMain : BaseUnityPlugin {

	public const String ModGUID = "shenxl.MultiPalyerMod";
	public const String ModName = "MultiPalyer Mod";
	public const String ModVersion = "0.12.7";

	// 单例实例
	public static MultiPlayerMain Instance;

	// MultiplayerCore 实例 (核心逻辑的入口)
	public static MultiPlayerCore CoreInstance;

	// 日志记录器
	internal static new ManualLogSource Logger;

	// Harmony上下文
	private Harmony _harmony;

	// 共享状态变量：用于控制是否启用关卡标准化 Patch
	public static bool IsMultiplayerActive = false;

	// Awake在对象创建时调用, 早于Start
	private void Awake() {
		// 日志初始化
		Instance = this;
		Logger = base.Logger;
		Logger.LogInfo($"[MP Mod loading] {ModGUID} {ModVersion} 已加载");

		// 1. 创建一个新的, GameObject
		GameObject coreGameObject = new GameObject("MultiplayerCore_DDOL");

		// 2. 将核心脚本添加到新对象上
		CoreInstance = coreGameObject.AddComponent<MultiPlayerCore>();

		// 3. 立即保护新对象 (被游戏创建初期销毁了,为什么?)
		DontDestroyOnLoad(coreGameObject);

		// 4. 使用Harmony打补丁
		_harmony = new Harmony($"{ModGUID}");
		_harmony.PatchAll();
	}

	private void OnDestroy() {
		// 这个方法应该在场景切换时运行, 确认主组件被清理.
		Logger.LogInfo("[MP Mod loading]MultiPalyerMain (启动器) 已被销毁.");
	}
}
