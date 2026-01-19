using HarmonyLib;
using WKMPMod.Core;
using WKMPMod.Data;

namespace WKMPMod.Patch;

[HarmonyPatch(typeof(ENT_Player))]
public class Patch_ENT_Player {
	[HarmonyPostfix]
	[HarmonyPatch("Kill")]
	public static void Postfix(ENT_Player __instance, string type) {
		if (MPCore.IsInLobby) {
			MPEventBusGame.NotifyPlayerDeath();
			MPMain.LogInfo($"[Patch] 玩家死亡,类型: {type}", $"[Patch] Player death,type: {type}");
		}
	}
}