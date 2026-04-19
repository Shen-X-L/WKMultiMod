using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Text;
using WKMPMod.Core;

namespace WKMPMod.Patch;

[HarmonyPatch(typeof(CommandConsole))]
public class Patch_CommandConsole {
	// 补丁类: 修复字符串逻辑
	[HarmonyPatch("CommandValueAsString")]
		static bool Prefix(Func<object> functor, ref string __result) {
			object obj = functor();

			if (obj is string str) {
				__result = $"Value: {str}";
				return false; // 跳过原方法的执行
			}

			return true; // 其他类型 继续执行原方法
		}

	[HarmonyPatch("Awake")]
	static void Postfix() {
		MPCore.Instance.RegisterCommands();
		return;
	}
}
