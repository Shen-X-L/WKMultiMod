using HarmonyLib;
using System;
using WKMPMod.Core;
using WKMPMod.Util;

namespace WKMPMod.Patch;

[HarmonyPatch(typeof(CommandConsole))]
public class Patch_CommandConsole {
	// 补丁类: 修复字符串逻辑
	[HarmonyPatch("CommandValueAsString")]
	[HarmonyPrefix]
	public static bool CommandValueAsString_FixStringDisplay(Func<object> functor, ref string __result) {
		object obj = functor();

		if (obj is string str) {
			__result = $"Value: {str}";
			return false; // 跳过原方法的执行
		}

		return true; // 其他类型 继续执行原方法
	}

	// 启用时注册命令
	[HarmonyPatch("Awake")]
	[HarmonyPostfix]
	public static void Awake_RegisterCommands() {
		MPCore.Instance.RegisterCommands();
		return;
	}

	// 在allowCheats为false时禁止作弊
	[HarmonyPatch("EnableCheatsCommand")]
	[HarmonyPrefix]
	public static bool EnableCheatsCommand_BlockIfNotAllowed() {
		// 在大厅且不允许作弊
		if (MPCore.IsInLobby && !MPCore.IsAllowCheats) {
			// 当前大厅不允许作弊
			CommandConsole.LogError(Localization.Get("CommandConsole.CheatsNotAllowed"));
			return false;
		} 
		else return true;
	}
}
