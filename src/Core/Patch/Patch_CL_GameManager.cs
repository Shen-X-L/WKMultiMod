using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Text;
using WKMPMod.Data;

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
		MPEventBusGame.NotifyPlayerWin();
	}

	public static void RestartHeightOffset() {
		HeightOffset = 0f;
	}
}
