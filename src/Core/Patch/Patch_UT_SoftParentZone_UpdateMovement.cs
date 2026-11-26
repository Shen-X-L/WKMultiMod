using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using WKMPMod.Components;
using WKMPMod.Core;

namespace WKMPMod.Patch;



[HarmonyPatch(typeof(UT_SoftParentZone), nameof(UT_SoftParentZone.UpdateMovement))]
public static class Patch_UT_SoftParentZone_UpdateMovement {

	[HarmonyTranspiler]
	private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator il) {
		var codes = new List<CodeInstruction>(instructions);
		// 定位 HashSet<GameEntity>.Contains(GameEntity)
		var containsMethod = AccessTools.Method(
			typeof(HashSet<GameEntity>),
			nameof(HashSet<GameEntity>.Contains),
			new[] { typeof(GameEntity) }
		);
		// 定位 HashSet<GameEntity>.Add(GameEntity)
		var addMethod = AccessTools.Method(
			typeof(HashSet<GameEntity>),
			nameof(HashSet<GameEntity>.Add),
			new[] { typeof(GameEntity) }
		);
		// 注入点查找
		int containsIndex = codes.FindIndex(c => c.Calls(containsMethod));
		int addIndex = codes.FindIndex(c => c.Calls(addMethod));
		if (containsIndex < 0 || addIndex < 0) { 
			MPMain.LogError("UT_SoftParentZone.UpdateMovement注入点不存在");
			return codes;
		}
		// 获取 Contains 后面的 brtrue 跳转目标。
		// 原始 IL: IL_013a: brtrue IL_02fa
		var containsBranch = codes[containsIndex + 1];
		if (containsBranch.opcode != OpCodes.Brtrue	&& containsBranch.opcode != OpCodes.Brtrue_S) {
			MPMain.LogError("UT_SoftParentZone.UpdateMovement 跳转点不存在");
			return codes;
		}
		Label continueLabel = (Label)containsBranch.operand;
		// Add 前面的两个指令：
		// ldloc.s 4  -> HashSet<GameEntity>
		// ldloc.s 8  -> GameEntity
		// 复制 entity 的局部变量加载指令。
		CodeInstruction loadEntity = codes[addIndex - 1].Clone();
		// 在 HashSet.Add 前插入：
		//
		// ldloc.s 8
		// isinst RemoteProp
		// brtrue IL_02fa
		codes.InsertRange(addIndex - 2, new[] {
			loadEntity,
			new CodeInstruction(OpCodes.Isinst, typeof(RemoteProp)),
			new CodeInstruction(OpCodes.Brtrue, continueLabel)
		});
		return codes;
	}
}