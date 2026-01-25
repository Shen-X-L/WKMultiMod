using HarmonyLib;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.Util;

namespace WKMPMod.Patch;

[HarmonyPatch(typeof(ENT_Player))]
public class Patch_ENT_Player {
	[HarmonyPostfix]
	[HarmonyPatch("Kill")]
	public static void Postfix(ENT_Player __instance, string type) {
		if (MPCore.IsInLobby) {
			MPEventBusGame.NotifyPlayerDeath(type);
			MPMain.LogInfo(Localization.Get("Patch", "PlayerDeath", type));
		}
	}
}