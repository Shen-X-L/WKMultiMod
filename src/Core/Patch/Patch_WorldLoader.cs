using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Text;
using WKMPMod.Core;
using static WorldLoader;

namespace WKMultiPlayerMod.Patch;

[HarmonyPatch(typeof(WorldLoader))]
internal class Patch_WorldLoader {
	// 补丁类: 在联机模式下默认是固定种子,不上传成绩
	[HarmonyPatch(nameof(WorldLoader.Initialize))]
	[HarmonyPostfix]
	public static void Postfix() {
		if (MPCore.IsInLobby)
			WorldLoader.customSeed = true;
	}

	// 补丁类: 关闭种子偏移, 使复活时种子同步
	[HarmonyPatch(("IncrementSeed"))]
	[HarmonyPrefix]
	public static bool Prefix() {
		if (MPCore.IsInLobby)
			return false;
		return true;
	}

	// 补丁类: 关闭生成器的种子偏移, 使复活时种子同步
	[HarmonyPatch(("GenerateLevels"))]
	[HarmonyPrefix]
	public static void Prefix(GenerationParameters genParams) {
		if (MPCore.IsInLobby && genParams != null && CL_GameManager.GetBaseGamemode().gamemodeName == "Campaign") {
			genParams.seedOffset = 0; // 禁用种子偏移
		}
	}

}
