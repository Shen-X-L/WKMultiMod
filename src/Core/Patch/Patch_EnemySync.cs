using DG.Tweening.Plugins.Core;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Unity.Entities.UniversalDelegates;
using UnityEngine;
using WKMPMod.Components;
using WKMPMod.Core;
using WKMPMod.World;

namespace WKMPMod.Patch;

[HarmonyPatch(typeof(GameEntity))]
public class Patch_GameEntity {
	#region[伤害同步]

	// DEN_Mother 没调用父类Damage
	[HarmonyPatch(nameof(GameEntity.Damage))]
	[HarmonyPrefix]
	public static void Patch_Damage(GameEntity __instance, Damageable.DamageInfo info) {
		EnemySyncModule.Instance.BroadcastEnemyDamage(__instance, info);
	}

	#endregion

	#region[实体生命周期拦截]

	// DEN_Teeth DEN_EngravedDoor 没调用父类OnDisable OnEnable
	// DEN_Hunter 没调用父类OnDisable

	/// <summary>
	/// 监听 GameEntity 启用/生成,提供增量扫描数据源
	/// </summary>
	[HarmonyPatch(nameof(GameEntity.OnEnable))]
	[HarmonyPostfix]
	public static void Patch_OnEnable(GameEntity __instance) {
		EnemySyncModule.Instance.OnEntityEnabled(__instance);
	}

	/// <summary>
	/// 监听 GameEntity 禁用/销毁,立即通知注销记录
	/// </summary>
	[HarmonyPatch(nameof(GameEntity.OnDisable))]
	[HarmonyPrefix]
	public static void Patch_OnDisable(GameEntity __instance) {
		EnemySyncModule.Instance.OnEntityDisabled(__instance);
	}

	#endregion

	#region[生物死亡同步]

	/// <summary>
	/// 监听 GameEntity 死亡,立即通知注销记录
	/// </summary>
	[HarmonyPatch(nameof(GameEntity.Kill), new[] { typeof(string), typeof(Damageable.DamageInfo) })]
	[HarmonyPrefix]
	public static void Patch_Prefix_Kill(GameEntity __instance, out bool __state) {
		__state = __instance.dead;
	}
	[HarmonyPatch(nameof(GameEntity.Kill), new[] { typeof(string), typeof(Damageable.DamageInfo) })]
	[HarmonyPostfix]
	public static void Patch_Postfix_Kill(GameEntity __instance, string type, bool __state) {
		// 执行前未死亡,执行后死亡 视为第一次死亡
		if (!__state && __instance.dead) EnemySyncModule.Instance.OnEntityKill(__instance, type);
	}

	#endregion
}

[HarmonyPatch(typeof(DEN_Bloodbug))]
public class Patch_DEN_Bloodbug {
	#region[血虫死亡同步]

	/// <summary>
	/// 监听 GameEntity 死亡,立即通知注销记录
	/// </summary>
	[HarmonyPatch(nameof(DEN_Bloodbug.Kill))]
	[HarmonyPrefix]
	public static void Patch_Prefix_Kill(DEN_Bloodbug __instance, out bool __state) {
		__state = __instance.dead;
	}

	[HarmonyPatch(nameof(DEN_Bloodbug.Kill))]
	[HarmonyPostfix]
	public static void Patch_Postfix_Kill(DEN_Bloodbug __instance, string type, bool __state) {
		// 执行前未死亡,执行后死亡 视为第一次死亡
		if (!__state && __instance.dead) EnemySyncModule.Instance.OnEntityKill(__instance, type);
	}

	#endregion
}

[HarmonyPatch(typeof(DEN_VentThing))]
public class Patch_DEN_VentThing {
	[HarmonyPatch(nameof(DEN_VentThing.Damage))]
	[HarmonyPrefix]
	public static void Patch_Damage(DEN_VentThing __instance, Damageable.DamageInfo info) {
		EnemySyncModule.Instance.BroadcastEnemyDamage(__instance, info);
	}
}

[HarmonyPatch(typeof(DEN_Drone))]
public static class Patch_DEN_Drone {

	/// <summary>
	/// 将 DEN_Drone.OnCollisionEnter 中旧版的
	/// CreateDamageInfo(float, string)
	/// 替换为 Mod 提供的带 Drone Source 的版本
	/// </summary>
	[HarmonyTranspiler]
	[HarmonyPatch(typeof(DEN_Drone), "OnCollisionEnter")]
	public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
		var codes = new List<CodeInstruction>(instructions);

		// 定义目标方法与 Helper 方法
		var originalMethod = AccessTools.Method(
			typeof(Damageable.DamageInfo),
			nameof(Damageable.DamageInfo.CreateDamageInfo),
			new[] { typeof(float), typeof(string) }
		);
		var helperMethod = AccessTools.Method(typeof(Patch_DEN_Drone), nameof(Patch_DEN_Drone.CreateDamageInfo));

		// 方法存在性校验
		if (originalMethod == null) {
			MPMain.LogWarning("[Transpiler] DamageInfo.CreateDamageInfo(float,string) 不存在");
			return codes;
		}

		// 寻找调用 CreateDamageInfo 的 IL 指令索引
		int targetIndex = codes.FindIndex(c => c.Calls(originalMethod));
		if (targetIndex == -1) {
			MPMain.LogWarning("[Transpiler] DEN_Drone.OnCollisionEnter -> DamageInfo.CreateDamageInfo(float,string) 不存在");
			return codes;
		}

		// 直接把原 Call 指令修改为 Ldarg_0
		codes[targetIndex].opcode = OpCodes.Ldarg_0;
		codes[targetIndex].operand = null;

		// 在 targetIndex + 1 处追加调用 Helper 方法
		codes.Insert(targetIndex + 1, new CodeInstruction(OpCodes.Call, helperMethod));
		return codes;
	}

	/// <summary>
	/// 实际由 IL 调用的辅助函数。
	/// </summary>
	public static Damageable.DamageInfo CreateDamageInfo(float amount, string objectType, DEN_Drone source) {
		return Damageable.DamageInfo.CreateDamageInfo(amount, objectType, new List<string> { "drone" }, source);
	}
}