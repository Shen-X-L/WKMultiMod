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

[HarmonyPatch(typeof(Projectile))]
public static class Patch_Projectile {

	#region [静态反射工具]

	public static readonly AccessTools.FieldRef<Projectile, Vector3> velocityField =
		AccessTools.FieldRefAccess<Projectile, Vector3>("velocity");

	public static readonly AccessTools.FieldRef<Projectile, Vector3> startPosField =
	AccessTools.FieldRefAccess<Projectile, Vector3>("startPos");

	public static readonly AccessTools.FieldRef<Projectile, float> velocityMultiplierField =
		AccessTools.FieldRefAccess<Projectile, float>("velMult");

	public static readonly AccessTools.FieldRef<Projectile, int> bouncesField =
		AccessTools.FieldRefAccess<Projectile, int>("bounces");

	public static readonly AccessTools.FieldRef<Projectile, bool> hasHitSurfaceField =
		AccessTools.FieldRefAccess<Projectile, bool>("hasHitSurface");

	public static readonly AccessTools.FieldRef<Projectile,bool> hasSpawnedTracerField =
		AccessTools.FieldRefAccess<Projectile, bool>("hasSpawnedTracer");



	private static readonly Type[] InitializeArguments = {
		typeof(Vector3),typeof(GameEntity),typeof(Item)
	};

	#endregion


	[HarmonyPatch(nameof(Projectile.Initialize))]
	[HarmonyPostfix]
	private static void Patch_Initialize(Projectile __instance) {
		ProjectileSyncModule.Instance?.OnInitialize(__instance);
	}


	[HarmonyPatch("CollisionCheck")]
	[HarmonyPrefix]
	private static bool Patch_CollisionCheck(Projectile __instance) {
		return ProjectileSyncModule.Instance.CollisionCheck(__instance);
	}


	[HarmonyPatch(nameof(Projectile.OnRichochet))]
	[HarmonyPostfix]
	private static void Patch_Richochet(Projectile __instance) {
		ProjectileSyncModule.Instance?.OnRichochet(__instance);
	}


	[HarmonyPatch("CreateHitEffect")]
	[HarmonyPrefix]
	private static void Patch_CreateHitEffect(Projectile __instance, RaycastHit hit) {
		// 已经命中过
		if (__instance.HasHitSurface()) return;
		ProjectileSyncModule.Instance?.OnCreateHitEffect(__instance,ref hit);
	}

	#region[投射物碰撞命中时生成的可攀爬对象]

	/// <summary>
	/// Patch: 投射物碰撞命中时
	/// - 用于处理由投射物生成的可攀爬对象 (如钢筋, 冰枪点等) 
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
			nameof(SaveCapturedPiton));
		codes.InsertRange(targetIndex + 1, new[] {
			new CodeInstruction(OpCodes.Dup),
			new CodeInstruction(OpCodes.Call, saveMethod)
		});

		return codes;
	}

	private static void SaveCapturedPiton(GameObject go) {
		ClimbableSyncModule.SaveCapturedPiton(go);
	}

	#endregion
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
		return ProjectileSyncModule.Instance.ReturnUpdate(__instance);
	}

	[HarmonyPatch("StartReturn")]
	[HarmonyPostfix]
	private static void Patch_StartReturn(Projectile_ReturnRebar __instance) {
		ProjectileSyncModule.Instance.OnStartReturn(__instance);
	}

	[HarmonyPatch("CreateHitEffect")]
	[HarmonyPrefix]
	private static void Patch_CreateHitEffect(Projectile_ReturnRebar __instance, RaycastHit hit) {
		// 已经命中过
		if (__instance.returning||__instance.HasHitSurface()) return;
		ProjectileSyncModule.Instance?.OnCreateHitEffect(__instance, ref hit);
	}
}

#endregion
