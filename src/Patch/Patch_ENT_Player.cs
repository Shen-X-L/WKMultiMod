using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Text;
using WKMultiMod.src.Core;

namespace WKMultiMod.src.Patch;

[HarmonyPatch(typeof(ENT_Player))]
public class Patch_ENT_Player {
	[HarmonyPostfix]
	[HarmonyPatch("Kill")]
	public static void Postfix(ENT_Player __instance, string type) {
		if (MPCore.IsMultiplayerActive) {
			MPEventBus.Game.NotifyPlayerDeath();
			MPMain.LogInfo($"[Patch] 玩家死亡,类型: {type}", $"[Patch] Player death,type: {type}");
		}
	}
}