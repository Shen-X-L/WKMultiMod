using HarmonyLib;
using Steamworks;
using WKMPMod.Core;
using WKMPMod.Util;
using Object = UnityEngine.Object;

namespace WKMPMod.Patch;

[HarmonyPatch(typeof(SteamClient))]
public class Patch_SteamClient {
	[HarmonyPatch(nameof(SteamClient.Init))]
	[HarmonyPrefix]
	public static void Patch_Init(ref uint appid) {
		if (MPConfig.UsePiratedMode == true) appid = 480; 
		return;
	}
}
