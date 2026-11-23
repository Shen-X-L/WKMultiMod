using DG.Tweening.Plugins.Core;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Text;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.Util;
using static Damageable;

namespace WKMPMod.Patch;

[HarmonyPatch(typeof(CL_GameManager))]
public class Patch_CL_GameManager {
	// 累计的 TP 带来的高度跳变
	public static float HeightOffset = 0f;

	[HarmonyPatch(nameof(CL_GameManager.GetPlayerTravelDistance))]
	[HarmonyPostfix]
	public static void GetDistance(ref float __result) {
		// 在原始计算结果基础上, 减去偏移量
		__result -= HeightOffset;
	}

	[HarmonyPatch(nameof(CL_GameManager.GetPlayerCorrectedHeight))]
	[HarmonyPostfix]
	public static void GetHeight(ref float __result) {
		__result -= HeightOffset;
	}

	[HarmonyPatch(nameof(CL_GameManager.Win))]
	[HarmonyPrefix]
	public static void Win() {
		EventBusGame.NotifyPlayerWin();
	}

	// 死亡信息总线调用
	[HarmonyPatch(nameof(CL_GameManager.Die))]
	[HarmonyPrefix]
	public static void Die(string type) {
		try {
			// 死亡切换发生前通知总线
			if (MPCore.IsInLobby && !CL_GameManager.gMan.IsReviving()) {
				EventBusGame.NotifyPlayerDeath(type, Patch_ENT_Player.killDamageInfo);
				MPMain.LogInfo(Localization.GetLog("Patch.PlayerDeath", type));
				Patch_ENT_Player.killDamageInfo = null;
			}
		} catch (Exception ex) {
			MPMain.LogError($"[{ex.GetType().Name}] {ex.Message}\n{ex.StackTrace}");
		}
	}

	public static void RestartHeightOffset() {
		HeightOffset = 0f;
	}

	#region[记录生成标记]	

	[HarmonyPatch(nameof(CL_GameManager.HasActiveFlag))]
	[HarmonyPrefix]
	private static bool Patch_HasActiveFlag_Prefix(string name, ref bool __result) {
		if (!MapSync.TryHostFlag(name, out bool value)) return true;
		__result = value;
		return false;
	}

	[HarmonyPatch(nameof(CL_GameManager.HasActiveFlag))]
	[HarmonyPostfix]
	private static void Patch_HasActiveFlag_Postfix(string name, bool __result) => MapSync.RecordFlag(name, __result);

	#endregion
}
