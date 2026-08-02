using HarmonyLib;
using Steamworks;
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

		if (_hasCoreInjected) return;

		// 创建核心对象
		try {
			_ = MPCore.Instance;

			_hasCoreInjected = true;

		} catch (System.Exception e) {
			MPMain.LogError(Localization.Get("Patch.CoreInjectionFailed",e.Message));
		}
	}
}

[HarmonyPatch(typeof(SteamClient))]
public class Patch_SteamClient {
	[HarmonyPatch(nameof(SteamClient.Init))]
	[HarmonyPrefix]
	public static void Patch_Init(ref uint appid) {
		if (MPConfig.UsePiratedMode == true) appid = 480; 
		return;
	}
}
