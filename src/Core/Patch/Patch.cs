using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using WKMPMod.Core;
using Object = UnityEngine.Object;

namespace WKMPMod.Patch;

// 补丁类: 强制解锁所有进度
[HarmonyPatch(typeof(CL_ProgressionManager), "HasProgressionUnlock")]
class Patch_Progression_ForceUnlock {
	//bool 类型: 控制是否执行原方法 true=执行 false=跳过
	static bool Prefix(ref bool __result) {
		if (MPCore.IsInLobby) {
			__result = true; // 强制所有解锁检查通过
			return false;    // 跳过原始的解锁检查逻辑
		}
		return true; // 非联机模式,执行原始的解锁检查
	}
}

// 补丁类: 禁用关卡翻转功能
// Copy自WK_IShowSeed Mod GitHub仓库地址: https://github.com/shishyando/WK_IShowSeed
[HarmonyPatch(typeof(M_Level), "Awake")]
public static class Patch_M_Level_Awake {
	public static void Prefix(M_Level __instance) {
		// 仅在联机模式下禁用关卡翻转
		if (MPCore.IsInLobby) {
			// 禁用关卡翻转功能
			__instance.canFlip = false;
		}
	}
}

//补丁类: 标准化关卡生成几率
//会一直生成稀有事件和稀有关卡 过于搞笑 所以注释掉了
//做成了一个搞笑的混乱模式选项
[HarmonyPatch(typeof(SpawnTable.SpawnSettings), "GetEffectiveSpawnChance")]
class Patch_SpawnSettings_StandardizeChance {
	public static bool Prefix(SpawnTable.SpawnSettings __instance, ref float __result) {
		// 混乱模式下, 强制事件生成几率为 1.0f (100%)
		if (MPCore.IsChaosMod) {
			// 其他情况, 强制 1.0f
			__result = 1f;
			return false; // 跳过原始复杂的计算和过滤
		}
		return true; // 非混乱模式, 执行原始方法
	}
}