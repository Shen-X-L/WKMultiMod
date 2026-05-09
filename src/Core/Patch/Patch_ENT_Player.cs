using HarmonyLib;
using System.Reflection;
using UnityEngine;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.Util;

namespace WKMPMod.Patch;

[HarmonyPatch(typeof(ENT_Player))]
public class Patch_ENT_Player {
	[HarmonyPatch(nameof(ENT_Player.Kill))]
	[HarmonyPrefix]
	public static void Kill_NotifyPlayerDeath(ENT_Player __instance, string type, Damageable.DamageInfo damageInfo) {
		// 死亡切换发生前通知总线
		// 避免死亡后重复通知
		FieldInfo field = typeof(ENT_Player).GetField("godmode", BindingFlags.NonPublic | BindingFlags.Instance);
		var godmode = (bool)field.GetValue(__instance);
		if (MPCore.IsInLobby && !__instance.dead && !CL_GameManager.gMan.IsReviving() && !godmode) {
			MPEventBusGame.NotifyPlayerDeath(type);
			MPMain.LogInfo(Localization.Get("Patch.PlayerDeath", type));
		}
	}
	[HarmonyPatch("Awake")]
	[HarmonyPostfix]
	public static void Awake_ResetFireMult(ENT_Player __instance) {
		if (MPCore.IsInLobby) {
			__instance.fireTimeMult = MPCore.damageRules.FireTime;
			__instance.fireDamageMult = MPCore.damageRules.FireDamage;
		}
	}
}