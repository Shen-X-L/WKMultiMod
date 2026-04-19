using HarmonyLib;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.Util;

namespace WKMPMod.Patch;

[HarmonyPatch(typeof(ENT_Player))]
public class Patch_ENT_Player {
	[HarmonyPatch(nameof(ENT_Player.Kill))]
	public static void Prefix(ENT_Player __instance, string type) {
		// 死亡切换发生前通知总线
		// 避免死亡后重复通知
		if (MPCore.IsInLobby&& __instance.dead == false) {
			MPEventBusGame.NotifyPlayerDeath(type);
			MPMain.LogInfo(Localization.Get("Patch.PlayerDeath", type));
		}
	}
}