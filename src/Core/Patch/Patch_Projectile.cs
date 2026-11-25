using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;
using WKMPMod.Core;
using WKMPMod.Util;
using WKMPMod.World;
using static Projectile;

[HarmonyPatch(typeof(Projectile))]
public static class Patch_Projectile {

	#region [静态反射工具]

	public static readonly AccessTools.FieldRef<Projectile, Vector3> velocityField =
		AccessTools.FieldRefAccess<Projectile, Vector3>("velocity");
	public static readonly AccessTools.FieldRef<Projectile, float> velocityMultiplierField =
		AccessTools.FieldRefAccess<Projectile, float>("velMult");
	public static readonly AccessTools.FieldRef<Projectile, int> bouncesField =
		AccessTools.FieldRefAccess<Projectile, int>("bounces");
	public static readonly AccessTools.FieldRef<Projectile, bool> hasHitSurfaceField =
		AccessTools.FieldRefAccess<Projectile, bool>("hasHitSurface");

	private static readonly Type[] InitializeArguments = {
		typeof(Vector3),typeof(GameEntity),typeof(Item)
	};

	#endregion

	/// <summary>
	/// 投射物创建
	/// </summary>
	[HarmonyPatch(nameof(Projectile.Initialize))]
	[HarmonyPostfix]
	private static void Patch_Initialize(Projectile __instance) {
		ProjectileSyncModule.Instance?.OnInitialize(__instance);
	}

	/// <summary>
	/// 是否还进行碰撞检测
	/// </summary>
	[HarmonyPatch("CollisionCheck")]
	[HarmonyPrefix]
	private static bool Patch_CollisionCheck(Projectile __instance) {
		return ProjectileSyncModule.Instance.CollisionCheck(__instance);
	}

	/// <summary>
	/// 反弹回调
	/// </summary>
	[HarmonyPatch(nameof(Projectile.OnRichochet))]
	[HarmonyPostfix]
	private static void Patch_Richochet(Projectile __instance) {
		ProjectileSyncModule.Instance?.OnRichochet(__instance);
	}

	/// <summary>
	/// 命中效果对象捕获
	/// </summary>
	[HarmonyPatch("CreateHitEffect")]
	[HarmonyTranspiler]
	static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
		var codes = new List<CodeInstruction>(instructions);
		// 查找符合特征的 Instantiate 调用位置
		int targetIndex = codes.FindIndex(c =>
			c.opcode == OpCodes.Call
			&& c.operand is MethodInfo m
			&& m.Name == "Instantiate"
			&& m.GetParameters() is { Length: 3 } p
			&& p[1].ParameterType == typeof(Vector3)
		);
		if (targetIndex == -1) {
			MPMain.LogError(Localization.GetLog("MPPatch.TranspilerError", "Projectile.CreateHitEffect"));
			return codes;
		}
		// 注入代码: 复制返回值并调用 SaveCapturedPiton
		var saveMethod = AccessTools.Method(
			typeof(Patch_Projectile),
			nameof(SaveHitEffect));
		codes.InsertRange(targetIndex + 1, new[] {
			new CodeInstruction(OpCodes.Dup),
			new CodeInstruction(OpCodes.Ldarg_0),
			new CodeInstruction(OpCodes.Call, saveMethod)
		});
		return codes;
	}

	/// <summary>
	/// 命中效果对象捕获
	/// </summary>
	private static void SaveHitEffect(GameObject go, Projectile pro) {
		ProjectileSyncModule.Instance.SaveHitEffect(pro, go);
	}

	/// <summary>
	/// 命中回调
	/// </summary>
	[HarmonyPatch("CreateHitEffect")]
	[HarmonyPrefix]
	private static void Patch_CreateHitEffect(Projectile __instance, RaycastHit hit, HitEffect effect) {
		// 已经命中过
		if (__instance.HasHitSurface()) return;
		ProjectileSyncModule.Instance?.OnCreateHitEffect(__instance, ref hit, effect);
	}

	[HarmonyPatch("ParentHitItem")]
	[HarmonyTranspiler]
	private static IEnumerable<CodeInstruction> Transpiler_ParentHitItem(IEnumerable<CodeInstruction> instructions) {
		var codes = new List<CodeInstruction>(instructions);

		// 定位 clone.GetDropObject(true).transform.rotation = rotation; 之后的 Transform.set_rotation
		int setRotIndex = codes.FindIndex(c =>
			(c.opcode == OpCodes.Callvirt || c.opcode == OpCodes.Call)
			&& c.operand is MethodInfo m
			&& m.Name == "set_rotation"
			&& m.DeclaringType == typeof(Transform)
		);

		if (setRotIndex == -1) {
			MPMain.LogError(Localization.GetLog("MPPatch.TranspilerError", "Projectile.ParentHitItem"));
			return codes;
		}

		var notifyMethod = AccessTools.Method(
			typeof(Patch_Projectile),
			nameof(OnCloneDropObjectPlaced),
			new[] { typeof(Projectile), typeof(Item_Object) }
		);

		// 在 set_rotation 之后注入：
		// OnCloneDropObjectPlaced(this, clone.GetDropObject(true))
		codes.InsertRange(setRotIndex + 1, new[] {
			new CodeInstruction(OpCodes.Ldarg_0),
			new CodeInstruction(OpCodes.Ldloc_0),
			new CodeInstruction(OpCodes.Call, notifyMethod)
		});

		return codes;
	}

	private static void OnCloneDropObjectPlaced(Projectile projectile, Item clone) {
		if (projectile == null || clone == null) return;
		ProjectileSyncModule.Instance?.OnParentHitItem(projectile, clone);
	}
}

#region[Grapple抓钩特化]

[HarmonyPatch(typeof(HandItem_Shoot_Grapple))]
public static class Patch_HandItem_Shoot_Grapple {

	private static readonly AccessTools.FieldRef<HandItem_Shoot_Grapple, Projectile_Grapple> _grappleProjectileField =
		AccessTools.FieldRefAccess<HandItem_Shoot_Grapple, Projectile_Grapple>("grappleProjectile");

	[HarmonyPatch(nameof(HandItem_Shoot_Grapple.Update))]
	[HarmonyTranspiler]
	private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
		var codes = new List<CodeInstruction>(instructions);
		var grappleField = AccessTools.Field(
			typeof(HandItem_Shoot_Grapple), "grappleProjectile");
		var recallingField = AccessTools.Field(
			typeof(HandItem_Shoot_Grapple), "recalling");
		var hasHitMethod = AccessTools.Method(
			typeof(Patch_HandItem_Shoot_Grapple),nameof(OnGrappleHasHit));
		var removeMethod = AccessTools.Method(
			typeof(Patch_HandItem_Shoot_Grapple),nameof(OnGrappleRemove));
		var recallingMethod = AccessTools.Method(
			typeof(Patch_HandItem_Shoot_Grapple),nameof(OnGrappleRecalling));
		var dropHandMethod = AccessTools.Method(
			typeof(Patch_HandItem_Shoot_Grapple),nameof(OnGrappleDropHand));
		// 查找 vector = grappleProjectile.transform.position - transform.position
		// 对应的 Vector3.op_Subtraction, 并在 stloc 之后注入
		int hitIndex = codes.FindIndex(c =>
			c.opcode == OpCodes.Call
			&& c.operand is MethodInfo m
			&& m.Name == "op_Subtraction"
			&& m.DeclaringType == typeof(Vector3)
		);
		if (hitIndex == -1 || hitIndex + 1 >= codes.Count
			|| !IsStloc(codes[hitIndex + 1])) {
			MPMain.LogError("OnGrappleHasHit IL注入点不存在");
			return codes;
		}
		// 位移定位到IL_017c: ldarg.0之前
		hitIndex += 2;
		// 查找 recalling = true 对应的 stfld.
		int recallingIndex = codes.FindIndex(c =>
			c.opcode == OpCodes.Stfld
			&& Equals(c.operand, recallingField)
		);
		if (recallingIndex == -1) {
			MPMain.LogError("OnGrappleRecalling IL注入点不存在");
			return codes;
		}
		recallingIndex++;
		// 查找 !InputManager.GetButton(hand.fireButton).Pressed toHook.magnitude < 2f 分支中的 RemoveItem.
		// 查找 maxProjectileRange * 3f 分支中的 RemoveItem.
		// 收集所有调用 RemoveItem 的指令索引
		List<int> removeItemCallIndices = new List<int>();
		for (int i = 0; i < codes.Count; i++) {
			var instruction = codes[i];
			if ((instruction.opcode == OpCodes.Callvirt || instruction.opcode == OpCodes.Call)
				&& instruction.operand is MethodInfo method
				&& method.Name == "RemoveItem"
				&& method.GetParameters().Length == 0) {
				removeItemCallIndices.Add(i);
			}
		}
		if (removeItemCallIndices.Count < 3) {
			MPMain.LogError("RemoveItem IL注入点数量不足3个");
			return codes;
		}
		// 按照索引从后往前插入, 避免前面的插入改变后续索引.
		var injections = new List<(int index, List<CodeInstruction> instructions)> {
			// OnGrappleHasHit(grappleProjectile, this)
			(hitIndex, new List<CodeInstruction> {
				new CodeInstruction(OpCodes.Ldarg_0),
				new CodeInstruction(OpCodes.Ldfld, grappleField),
				new CodeInstruction(OpCodes.Ldarg_0),
				new CodeInstruction(OpCodes.Call, hasHitMethod)
			}),
	        // OnGrappleRecalling(grappleProjectile, this)
	        (recallingIndex, new List<CodeInstruction> {
				new CodeInstruction(OpCodes.Ldarg_0),
				new CodeInstruction(OpCodes.Ldfld, grappleField),
				new CodeInstruction(OpCodes.Ldarg_0),
				new CodeInstruction(OpCodes.Call, recallingMethod)
			}),
		};
		// 前2个 RemoveItem 前添加 OnGrappleDropHand
		for (int k = 0; k < 2; k++) {
			int callIdx = removeItemCallIndices[k];
			int insertIdx = callIdx - 1; // 对应调用前的 ldarg.0 位置
			injections.Add((insertIdx, new List<CodeInstruction> {
				new CodeInstruction(OpCodes.Ldarg_0),
				new CodeInstruction(OpCodes.Ldfld, grappleField),
				new CodeInstruction(OpCodes.Ldarg_0),
				new CodeInstruction(OpCodes.Call, dropHandMethod)
			}));
		}
		// 第3个 RemoveItem 前添加 OnGrappleRemove
		{
			int callIdx = removeItemCallIndices[2];
			int insertIdx = callIdx - 1;
			injections.Add((insertIdx, new List<CodeInstruction> {
				new CodeInstruction(OpCodes.Ldarg_0),
				new CodeInstruction(OpCodes.Ldfld, grappleField),
				new CodeInstruction(OpCodes.Ldarg_0),
				new CodeInstruction(OpCodes.Call, removeMethod)
			}));
		}
		foreach (var injection in injections.OrderByDescending(x => x.index)) {
			codes.InsertRange(injection.index, injection.instructions);
		}
		return codes;
	}

	private static bool IsStloc(CodeInstruction code) {
		return code.opcode == OpCodes.Stloc
			|| code.opcode == OpCodes.Stloc_S
			|| code.opcode == OpCodes.Stloc_0
			|| code.opcode == OpCodes.Stloc_1
			|| code.opcode == OpCodes.Stloc_2
			|| code.opcode == OpCodes.Stloc_3;
	}

	[HarmonyPatch("CreateProjectile")]
	[HarmonyPostfix]
	private static void Patch_CreateProjectile(HandItem_Shoot_Grapple __instance) {
		if(_grappleProjectileField(__instance) is { } grapple)
			ProjectileSyncModule.Instance.OnGrappleCreate(grapple, __instance);
	}

	private static void OnGrappleHasHit(Projectile_Grapple grapple, HandItem_Shoot_Grapple handItem) {
		ProjectileSyncModule.Instance.OnGrappleHasHit(grapple, handItem);
	}

	private static void OnGrappleRemove(Projectile_Grapple grapple, HandItem_Shoot_Grapple handItem) {
		UnityEngine.Object.Destroy(grapple);
	}

	private static void OnGrappleRecalling(Projectile_Grapple grapple, HandItem_Shoot_Grapple handItem) {
		ProjectileSyncModule.Instance.OnGrappleRecalling(grapple, handItem);
	}
	private static void OnGrappleDropHand(Projectile_Grapple grapple, HandItem_Shoot_Grapple handItem) {
		ProjectileSyncModule.Instance.OnGrappleDropHand(grapple, handItem);
	}

}

[HarmonyPatch(typeof(Projectile_Grapple))]
public static class Patch_Projectile_Grapple {

	[HarmonyPatch("LateUpdate")]
	[HarmonyTranspiler]
	private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
		var codes = new List<CodeInstruction>(instructions);

		var latchedField = AccessTools.Field(typeof(Projectile_Grapple), "latched");
		var onLatchedMethod = AccessTools.Method(
			typeof(Patch_Projectile_Grapple),
			nameof(OnGrappleLatched),
			new[] { typeof(Projectile_Grapple) }
		);

		if (latchedField == null || onLatchedMethod == null) {
			MPMain.LogError(Localization.GetLog("MPPatch.TranspilerError", "Projectile_Grapple.LateUpdate"));
			return codes;
		}

		// 查找 latched = true 对应的 stfld bool Projectile_Grapple::latched
		int latchStoreIndex = codes.FindIndex(c =>
			c.opcode == OpCodes.Stfld
			&& Equals(c.operand, latchedField)
		);

		if (latchStoreIndex == -1) {
			MPMain.LogError("OnGrappleLatched IL注入点不存在");
			return codes;
		}

		// 在 stfld latched 之后注入 OnGrappleLatched(this)
		codes.InsertRange(latchStoreIndex + 1, new[] {
			new CodeInstruction(OpCodes.Ldarg_0),
			new CodeInstruction(OpCodes.Call, onLatchedMethod)
		});

		return codes;
	}

	private static void OnGrappleLatched(Projectile_Grapple grapple) {
		ProjectileSyncModule.Instance?.OnGrappleLatched(grapple);
	}
}

#endregion

#region[Rebar矛特化]

[HarmonyPatch(typeof(Projectile_ReturnRebar))]
public static class Patch_Projectile_ReturnRebar {
	public static readonly AccessTools.FieldRef<Projectile_ReturnRebar, bool> hasHitSurfaceField =
		AccessTools.FieldRefAccess<Projectile_ReturnRebar, bool>("hasHitSurface");

	[HarmonyPatch("ReturnUpdate")]
	[HarmonyPrefix]
	private static bool Patch_ReturnUpdate(Projectile_ReturnRebar __instance) {
		return ProjectileSyncModule.Instance.RebarReturnUpdate(__instance);
	}

	[HarmonyPatch("StartReturn")]
	[HarmonyPostfix]
	private static void Patch_StartReturn(Projectile_ReturnRebar __instance) {
		ProjectileSyncModule.Instance.OnRebarStartReturn(__instance);
	}

	[HarmonyPatch("CreateHitEffect")]
	[HarmonyPrefix]
	private static void Patch_CreateHitEffect(Projectile_ReturnRebar __instance, RaycastHit hit, HitEffect effect) {
		// 已经命中过
		if (__instance.returning||__instance.HasHitSurface()) return;
		ProjectileSyncModule.Instance?.OnCreateHitEffect(__instance, ref hit, effect);
	}
}

#endregion

#region[Boomerang剪刀回旋镖特化]

[HarmonyPatch(typeof(Projectile_Boomerang))]
public static class Patch_Projectile_Boomerang {
	public static readonly AccessTools.FieldRef<Projectile_Boomerang, bool> hasHitSurfaceField =
		AccessTools.FieldRefAccess<Projectile_Boomerang, bool>("hasHitSurface");

	[HarmonyPatch("ReturnUpdate")]
	[HarmonyPrefix]
	private static bool Patch_ReturnUpdate(Projectile_Boomerang __instance) {
		return ProjectileSyncModule.Instance.BoomerangReturnUpdate(__instance);
	}

	[HarmonyPatch("StartReturn")]
	[HarmonyPostfix]
	private static void Patch_StartReturn(Projectile_Boomerang __instance) {
		ProjectileSyncModule.Instance.OnBoomerangStartReturn(__instance);
	}

	[HarmonyPatch("CreateHitEffect")]
	[HarmonyPrefix]
	private static void Patch_CreateHitEffect(Projectile_Boomerang __instance, RaycastHit hit, HitEffect effect) {
		MPMain.LogDebug("=========");
		if (__instance.returning || hasHitSurfaceField(__instance)) return;
		ProjectileSyncModule.Instance?.OnCreateHitEffect(__instance, ref hit, effect);
	}
}

#endregion
