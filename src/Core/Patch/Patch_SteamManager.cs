using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using WKMPMod.Core;
using WKMPMod.Util;
using Object = UnityEngine.Object;

namespace WKMPMod.Patch;

[HarmonyPatch(typeof(SteamManager))]
public class Patch_SteamManager {
	private static bool _hasCoreInjected = false;

	[HarmonyPostfix]
	[HarmonyPatch("Awake")]
	public static void Postfix(SteamManager __instance) {
		MPMain.LogInfo(Localization.Get("Patch", "PreparingToInjectCore"));

		if (_hasCoreInjected) {
			MPMain.LogWarning(Localization.Get("Patch", "CoreAlreadyInjected"));
			return;
		}

		// 简化的检查：只看是否已经存在任何MultiPlayerCore实例
		var existingCore = Object.FindObjectOfType<MPCore>();
		if (existingCore != null) {
			MPMain.LogWarning(Localization.Get("Patch", "CoreInstanceExists",existingCore.name));
			_hasCoreInjected = true;
			return;
		}

		// 创建核心对象
		try {
			GameObject coreGameObject = new GameObject("MultiplayerCore");
			coreGameObject.transform.SetParent(__instance.transform, false);
			coreGameObject.AddComponent<MPCore>();

			MPMain.LogInfo(Localization.Get("Patch", "CoreInjectionSuccess"));
			_hasCoreInjected = true;

		} catch (System.Exception e) {
			MPMain.LogError(Localization.Get("Patch", "CoreInjectionFailed",e.Message));
		}
	}
}
