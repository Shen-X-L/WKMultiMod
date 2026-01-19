using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using WKMPMod.Core;
using Object = UnityEngine.Object;

namespace WKMPMod.Patch;

[HarmonyPatch(typeof(SteamManager))]
public class Patch_SteamManager {
	private static bool _hasCoreInjected = false;

	[HarmonyPostfix]
	[HarmonyPatch("Awake")]
	public static void Postfix(SteamManager __instance) {
		MPMain.LogInfo(
			"[Patch] SteamManager.Awake 调用,准备注入MPCore",
			"[Patch] SteamManager.Awake called, preparing to inject core.");

		if (_hasCoreInjected) {
			MPMain.LogWarning(
				"[Patch] Core已经注入过,跳过",
				"[Patch] MPCore already injected, skipping.");
			return;
		}

		// 简化的检查：只看是否已经存在任何MultiPlayerCore实例
		var existingCore = Object.FindObjectOfType<MPCore>();
		if (existingCore != null) {
			MPMain.LogWarning(
				$"[Patch] 已存在核心实例: {existingCore.name}",
				$"[Patch] MPCore instance already exists. GameObjectName: {existingCore.name}");
			_hasCoreInjected = true;
			return;
		}

		// 创建核心对象
		try {
			GameObject coreGameObject = new GameObject("MultiplayerCore");
			coreGameObject.transform.SetParent(__instance.transform, false);
			coreGameObject.AddComponent<MPCore>();

			MPMain.LogInfo(
				"[Patch] MPCore 对象已成功注入 SteamManager",
				"[Patch] MPCore object successfully injected into SteamManager.");
			_hasCoreInjected = true;

		} catch (System.Exception e) {
			MPMain.LogError(
				$"[Patch] 注入核心失败: {e.Message}",
				$"[Patch] Failed to inject MPCore: {e.Message}");
		}
	}
}
