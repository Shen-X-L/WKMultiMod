using HarmonyLib;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;
using WKMPMod.Core;
using WKMPMod.Util;
using WKMPMod.World;
using static Projectile;

namespace WKMPMod.Patch;

/// <summary>
/// Patch: Piton命中时 (放置钉子) 
/// - Prefix: 记录当前已有的Handhold列表
/// - Postfix: 检测新生成的Piton并进行同步
///
/// Patch: Piton hit (placing a piton)
/// - Prefix: captures current existing handholds
/// - Postfix: detects newly spawned piton and syncs it
/// </summary>
[HarmonyPatch(typeof(HandItem_Piton), nameof(HandItem_Piton.PitonHit))]
public class Patch_HandItem_Piton_PitonHit {

	[HarmonyTranspiler]
	static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
		var codes = new List<CodeInstruction>(instructions);

		// 定义目标字段, 作为定位起点的锚点 
		var pitonField = AccessTools.Field(typeof(HandItem_Piton), nameof(HandItem_Piton.pitonWorldObject));

		bool found = false;
		for (int i = 0; i < codes.Count; i++) {
			// 第一步: 找到 ldfld HandItem_Piton::pitonWorldObject访问
			if (codes[i].opcode == OpCodes.Ldfld && codes[i].operand is FieldInfo f && f == pitonField) {
				// 第二步: 从这里开始向后找第一个名为 Instantiate 且有 3 个参数的方法调用
				for (int j = i; j < codes.Count; j++) {
					if (codes[j].opcode == OpCodes.Call && codes[j].operand is MethodInfo m && m.Name == "Instantiate") {

						var parameters = m.GetParameters();
						// 特征检查: 3个参数, 且第二个参数是 Vector3
						if (parameters.Length == 3 && parameters[1].ParameterType == typeof(Vector3)) {

							// 注入代码复制返回值并使用存入静态字段的方法调用
							codes.Insert(j + 1, new CodeInstruction(OpCodes.Dup));
							codes.Insert(j + 2, new CodeInstruction(OpCodes.Call,
								AccessTools.Method(typeof(ClimbableItemSyncManager), nameof(ClimbableItemSyncManager.SaveCapturedPiton))));

							found = true;
							break;
						}
					}
				}
			}
			if (found) break;
		}

		if (!found) {
			MPMain.LogError(Localization.Get("MPPatch.TranspilerError", "Piton.PitonHit"));
		} 
		return codes;
	}

	public static void Postfix(HandItem_Piton __instance) {
		ClimbableItemSyncManager.RegisterNewLocalPiton(__instance);
	}

	/// <summary>
	/// 在Piton生成前执行
	/// 保存当前Handhold状态, 用于之后检测新增对象
	///
	/// Runs before piton creation
	/// Stores current handhold state to detect newly created objects later
	/// </summary>
	//public static void Prefix(out HashSet<int> __state) {
	//	__state = ClimbableItemSyncManager.CaptureExistingHandholds();
	//}

	/// <summary>
	/// 在Piton生成后执行
	/// 查找新生成的可攀爬物体并注册同步
	///
	/// Runs after piton creation
	/// Finds newly created climbable object and registers it for sync
	/// </summary>
	//public static void Postfix(HandItem_Piton __instance, HashSet<int> __state) {
	//	ClimbableItemSyncManager.RegisterNewLocalPiton(__instance, __state);
	//}
}

/// <summary>
/// Patch: 投射物碰撞命中时
/// - 用于处理由投射物生成的可攀爬对象 (如射钉、钩点等) 
///
/// Patch: Projectile collision hit
/// - Handles climbable objects spawned by projectiles (e.g. shootable pitons)
/// </summary>
[HarmonyPatch(typeof(Projectile), "OnCollisionHit")]
public class Patch_Projectile_OnCollisionHit_ClimbableSync {

	/// <summary>
	/// 碰撞前记录已有Handhold
	///
	/// Captures existing handholds before collision
	/// </summary>
	//	public static void Prefix(out HashSet<int> __state) {
	//		__state = ClimbableItemSyncManager.CaptureExistingHandholds();
	//	}

	/// <summary>
	/// 碰撞后检测新生成的可攀爬对象并同步
	///
	/// After collision, detects newly created climbable and syncs it
	/// </summary>
	//	public static void Postfix(Projectile __instance, RaycastHit hit, HashSet<int> __state) {
	//		ClimbableItemSyncManager.RegisterNewLocalProjectileClimbable(__instance, hit, __state);
	//	}

	public static void Postfix(Projectile __instance, RaycastHit hit) {
		ClimbableItemSyncManager.RegisterNewLocalProjectileClimbable(__instance, hit);
	}

}


[HarmonyPatch(typeof(Projectile), "CreateHitEffect")]
public class Patch_Projectile_CreateHitEffect_ClimbableSync {
	static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
		var codes = new List<CodeInstruction>(instructions);

		for (int i = 0; i < codes.Count; i++) {
			if (codes[i].opcode == OpCodes.Call && codes[i].operand is MethodInfo m && m.Name == "Instantiate") {
				var parameters = m.GetParameters();
				// 特征检查: 3个参数, 且第二个参数是 Vector3
				if (parameters.Length == 3 && parameters[1].ParameterType == typeof(Vector3)) {

					// 注入代码复制返回值并使用存入静态字段的方法调用
					codes.Insert(i + 1, new CodeInstruction(OpCodes.Dup));
					codes.Insert(i + 2, new CodeInstruction(OpCodes.Call,
						AccessTools.Method(typeof(ClimbableItemSyncManager), nameof(ClimbableItemSyncManager.SaveCapturedPiton))));
					return codes;
				}
			}
		}
		MPMain.LogError(Localization.Get("MPPatch.TranspilerError", "Projectile.CreateHitEffect"));
		return codes;
	}
}

/// <summary>
/// Patch: Handhold被锤击 (加固) 时
/// - 触发一次强制同步更新
///
/// Patch: Handhold hammered (secured)
/// - Triggers a forced sync update
/// </summary>
[HarmonyPatch(typeof(CL_Handhold), nameof(CL_Handhold.HammerIn))]
public class Patch_CL_Handhold_HammerIn_PitonSync {

	/// <summary>
	/// 锤击后广播更新
	///
	/// Broadcast update after hammering
	/// </summary>
	public static void Postfix(CL_Handhold __instance) {
		ClimbableItemSyncManager.BroadcastHammerUpdate(__instance);
	}
}

/// <summary>
/// Patch: Handhold FixedUpdate
/// - 定期同步Handhold状态 (位置、旋转、secure状态等) 
///
/// Patch: Handhold FixedUpdate
/// - Periodically syncs handhold state (position, rotation, secure state, etc.)
/// </summary>
[HarmonyPatch(typeof(CL_Handhold), "FixedUpdate")]
public class Patch_CL_Handhold_FixedUpdate_PitonSync {

	/// <summary>
	/// 每帧物理更新后尝试同步
	///
	/// Attempts to sync after each physics update
	/// </summary>
	public static void Postfix(CL_Handhold __instance) {
		ClimbableItemSyncManager.BroadcastPeriodicUpdate(__instance);
	}
}

/// <summary>
/// Patch: Rope类型Handhold FixedUpdate
/// - 与普通Handhold一样进行周期同步
///
/// Patch: Rope handhold FixedUpdate
/// - Performs periodic sync similar to normal handholds
/// </summary>
[HarmonyPatch(typeof(CL_Handhold_Rope), "FixedUpdate")]
public class Patch_CL_Handhold_Rope_FixedUpdate_PitonSync {

	/// <summary>
	/// Rope Handhold同步
	///
	/// Sync rope handhold
	/// </summary>
	public static void Postfix(CL_Handhold_Rope __instance) {
		ClimbableItemSyncManager.BroadcastPeriodicUpdate(__instance);
	}
}

/// <summary>
/// Patch: 可破坏Handhold Update
/// - 使用Update而不是FixedUpdate, 因为其状态变化可能不是物理驱动
///
/// Patch: Breakable handhold Update
/// - Uses Update instead of FixedUpdate because changes may not be physics-driven
/// </summary>
[HarmonyPatch(typeof(CL_Handhold_Breakable), "Update")]
public class Patch_CL_Handhold_Breakable_Update_PitonSync {

	/// <summary>
	/// 每帧检查并同步破坏类Handhold状态
	///
	/// Checks and syncs breakable handhold state every frame
	/// </summary>
	public static void Postfix(CL_Handhold_Breakable __instance) {
		ClimbableItemSyncManager.BroadcastPeriodicUpdate(__instance);
	}
}

// [MP Debug] 仅当需要让断裂的岩钉生成同步的世界掉落物时才保留此桥接代码
//Vector3 velocity = rotation * Vector3.forward * BrokenPitonForwardVelocity + Vector3.down * BrokenPitonDownVelocity;
//ItemSyncManager.SpawnSyncedWorldDrop(dropPrefabKey, position, rotation, velocity);