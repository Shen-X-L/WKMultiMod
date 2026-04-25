using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Text;
using WKMPMod.Core;
using WKMPMod.NetWork;

namespace WKMPMod.Patch;

[HarmonyPatch(typeof(CommandConsole))]
public class Patch_CommandConsole {
	// 补丁类: 修复字符串逻辑
	[HarmonyPatch("CommandValueAsString")]
	public static bool Prefix(Func<object> functor, ref string __result) {
		object obj = functor();

		if (obj is string str) {
			__result = $"Value: {str}";
			return false; // 跳过原方法的执行
		}

		return true; // 其他类型 继续执行原方法
	}

	// 启用时注册命令
	[HarmonyPatch("Awake")]
	public static void Postfix() {
		MPCore.Instance.RegisterCommands();
		return;
	}

	// 在allowCheats为false时禁止作弊
	[HarmonyPatch("EnableCheatsCommand")]
	public static bool Prefix() {
		// 在大厅且不允许作弊
		if (MPCore.IsInLobby && !MPCore.IsAllowCheats) {
			CommandConsole.LogError("[MP Debug] Cheats are not allowed in the current lobby. Please ask the host to use allowcheats true.");
			return false;
		} 
		else return true;
	}
}
