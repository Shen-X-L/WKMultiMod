using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.NetWork;
using WKMPMod.Util;
using static WorldLoader;
using Object = UnityEngine.Object;

namespace WKMPMod.Patch;

// 补丁类: 强制解锁所有进度
// HarmonyPatch(类型名,函数名(nameof()或字符串),重载参数(Type[]{}))
[HarmonyPatch(typeof(CL_ProgressionManager), nameof(CL_ProgressionManager.HasProgressionUnlock))]
public class Patch_CL_ProgressionManager_HasProgressionUnlock {
	//bool 类型: 控制是否执行原方法 true=执行 false=跳过
	public static bool Prefix(ref bool __result) {
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
public class Patch_M_Level_Awake {
	public static void Prefix(M_Level __instance) {
		// 仅在联机模式下禁用关卡翻转
		if (MPCore.IsInLobby) {
			// 禁用关卡翻转功能
			__instance.canFlip = false;
		}
	}
}

// 补丁类: 在联机模式下重开时重置游戏状态控制器的状态
[HarmonyPatch(typeof(UT_GameStateController), nameof(UT_GameStateController.RestartScene))]
public class Patch_UT_GameStateController_RestartScene {
	public static bool Prefix() {
		// 重置偏移高度
		Patch_CL_GameManager.RestartHeightOffset();

		if (MPCore.IsInLobby) {
			if (MPGameModeManager.CurrentData != null) {
				MPGameModeManager.RestartGameMode();
				return false;
			}
			// 没有游戏模式数据,重置联机状态以防止潜在问题
			MPCore.SetStatus(MPStatus.INIT_MASK, MPStatus.NotInitialized); 
		}
		return true; // 非联机模式,执行原始的重开逻辑
	}
}

// 补丁类: 负责初始化游戏模式管理器
[HarmonyPatch(typeof(CL_AssetManager), nameof(CL_AssetManager.Initialize))]
public class Patch_CL_AssetManager_Initialize {
	public static void Postfix() {
		MPGameModeManager.Initialize();
	}
}


//[HarmonyPatch(typeof(CL_GameManager), nameof(CL_GameManager.Die))]
//public class  Patch_CL_GameManager_Die {
//	public static void Prefix(string type) {
//		if (CL_GameManager.isDead()) return;
//		if (MPCore.IsInLobby) {
//			MPEventBusGame.NotifyPlayerDeath(type);
//			MPMain.LogInfo(Localization.Get("Patch.PlayerDeath", type));
//		}
//	}
//}


