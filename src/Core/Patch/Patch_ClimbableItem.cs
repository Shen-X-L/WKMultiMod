using HarmonyLib;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;
using WKMPMod.Component;
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

	/// <summary>
	/// 通过修改IL代码 调用ClimbableItemSyncManager.SaveCapturedPiton来捕获生成的对象
	/// </summary>
	[HarmonyTranspiler]
	public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
		var codes = new List<CodeInstruction>(instructions);

		// 存在两个Instantiate 靠调用字段先定位
		// 定义目标字段 定位 GameObject gameObject = Object.Instantiate(pitonWorldObject, 
		var pitonField = AccessTools.Field(typeof(HandItem_Piton), nameof(HandItem_Piton.pitonWorldObject));

		// 寻找 ldfld HandItem_Piton::pitonWorldObject 访问
		int fieldIndex = codes.FindIndex(c => c.opcode == OpCodes.Ldfld && c.operand is FieldInfo f && f == pitonField);
		if (fieldIndex == -1) {
			MPMain.LogError(Localization.Get("MPPatch.TranspilerError", "Piton.PitonHit"));
			return codes;
		}

		// 向后找第一个名为 Instantiate 且有 3 个参数的方法调用
		int instantiateIndex = codes.FindIndex(fieldIndex, c =>
			c.opcode == OpCodes.Call &&
			c.operand is MethodInfo m &&
			m.Name == "Instantiate" &&
			m.GetParameters() is { Length: 3 } p &&
			p[1].ParameterType == typeof(Vector3)
		);
		if (instantiateIndex == -1) {
			MPMain.LogError(Localization.Get("MPPatch.TranspilerError", "Piton.PitonHit"));
			return codes;
		}

		// 注入代码: 复制返回值并调用 SaveCapturedPiton
		var saveMethod = AccessTools.Method(typeof(ClimbableSyncModule), nameof(ClimbableSyncModule.SaveCapturedPiton));
		codes.InsertRange(instantiateIndex + 1, new[] {
			new CodeInstruction(OpCodes.Dup),
			new CodeInstruction(OpCodes.Call, saveMethod)
		});

		return codes;
	}

	/// <summary>
	/// 调用ClimbableItemSyncManager.RegisterNewLocalPiton表示物体已经生成
	/// </summary>
	public static void Postfix(HandItem_Piton __instance) {
		ClimbableSyncModule.Instance.RegisterNewLocalPiton();
	}

	#region[旧代码]

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

	#endregion
}

[HarmonyPatch(typeof(Projectile))]
public class Patch_Projectile_ClimbableSync {
	/// <summary>
	/// Patch: 投射物碰撞命中时
	/// - 用于处理由投射物生成的可攀爬对象 (如射钉, 钩点等) 
	///
	/// Patch: Projectile collision hit
	/// - Handles climbable objects spawned by projectiles (e.g. shootable pitons)
	/// </summary>
	[HarmonyPatch("OnCollisionHit")]
	[HarmonyPostfix]
	public static void Patch_OnCollisionHit_Postfix(Projectile __instance, RaycastHit hit) {
		ClimbableSyncModule.Instance.RegisterNewLocalProjectileClimbable(__instance, hit);
	}

	[HarmonyPatch("CreateHitEffect")]
	[HarmonyTranspiler]
	static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
		var codes = new List<CodeInstruction>(instructions);

		// 查找符合特征的 Instantiate 调用位置
		int targetIndex = codes.FindIndex(c =>
			c.opcode == OpCodes.Call &&
			c.operand is MethodInfo m &&
			m.Name == "Instantiate" &&
			m.GetParameters() is { Length: 3 } p &&
			p[1].ParameterType == typeof(Vector3)
		);
		if (targetIndex == -1) {
			MPMain.LogError(Localization.Get("MPPatch.TranspilerError", "Projectile.CreateHitEffect"));
			return codes;
		}

		// 注入代码: 复制返回值并调用 SaveCapturedPiton
		var saveMethod = AccessTools.Method(typeof(ClimbableSyncModule), nameof(ClimbableSyncModule.SaveCapturedPiton));
		codes.InsertRange(targetIndex + 1, new[] {
			new CodeInstruction(OpCodes.Dup),
			new CodeInstruction(OpCodes.Call, saveMethod)
		});

		return codes;
	}

	#region[旧代码]
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
	#endregion
}

[HarmonyPatch(typeof(CL_Handhold))]
public class Patch_CL_Handhold_PitonSync {
	/// <summary>
	/// Hook 抓握开始
	/// </summary>
	[HarmonyPatch(nameof(CL_Handhold.Interact))]
	[HarmonyPostfix]
	public static void Postfix_Interact(CL_Handhold __instance, Clickable.InteractionInfo info) {
		if (!ClimbableSyncModule.TryGetNetworkIdentity(__instance, out var identity)) return;
		if (!identity.IsValid) return;
		ClimbableSyncModule.Instance.OnLocalHandholdGrabbed(identity);
	}

	/// <summary>
	/// Hook 松手结束
	/// </summary>
	[HarmonyPatch(nameof(CL_Handhold.StopInteract))]
	[HarmonyPostfix]
	public static void Postfix_StopInteract(CL_Handhold __instance, ENT_Player p, ENT_Player.Hand dropHand) {
		if (!ClimbableSyncModule.TryGetNetworkIdentity(__instance, out var identity)) return;
		if (!identity.IsValid) return;
		ClimbableSyncModule.Instance.OnLocalHandholdReleased(identity);
	}

	/// <summary>
	/// Patch: Handhold被锤击 (加固) 时 - 触发一次强制同步更新
	/// Patch: Handhold hammered (secured)- Triggers a forced sync update
	/// </summary>
	[HarmonyPatch(nameof(CL_Handhold.HammerIn))]
	[HarmonyPrefix]
	public static void Prefix_HammerIn(CL_Handhold __instance, float amount) {
		// 已经锤入 不需要广播
		if (__instance.secure) return;
		ClimbableSyncModule.Instance.BroadcastHammerIn(__instance,amount);
	}

	[HarmonyPatch("FixedUpdate")]
	[HarmonyTranspiler]
	static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
		var codes = new List<CodeInstruction>(instructions);

		// 查找符合特征的 Instantiate 调用位置
		int targetIndex = codes.FindIndex(c =>
			c.opcode == OpCodes.Call &&
			c.operand is MethodInfo m &&
			m.Name == "Instantiate" &&
			m.GetParameters() is { Length: 3 } p &&
			p[1].ParameterType == typeof(Vector3)
		);

		if (targetIndex == -1) {
			MPMain.LogError(Localization.Get("MPPatch.TranspilerError", "Projectile.CreateHitEffect"));
			return codes;
		}

		// 注入代码: 复制返回值并调用 BroadcastBreakObject
		var saveMethod = AccessTools.Method(typeof(ClimbableSyncModule), nameof(ClimbableSyncModule.CreateBreakObject));
		codes.InsertRange(targetIndex + 1, new[] {
			new CodeInstruction(OpCodes.Dup),
			new CodeInstruction(OpCodes.Ldarg_0),
			new CodeInstruction(OpCodes.Call, saveMethod)
		});

		return codes;
	}
}