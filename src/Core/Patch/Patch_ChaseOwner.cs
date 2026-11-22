using HarmonyLib;
using UnityEngine;
using WKMPMod.EntitySync;

namespace WKMPMod.Patch;

/// <summary>
/// 鼠王 / 遗骸 (entitymode full): 非所有者端网络接管期间跳过原版 AI; 遗骸被别人看见时所有者取消移动
/// </summary>
[HarmonyPatch]
public class Patch_ChaseOwner {
	private static readonly AccessTools.FieldRef<DEN_Remains_Active, Rigidbody> RemainsRigidField =
		AccessTools.FieldRefAccess<DEN_Remains_Active, Rigidbody>("rigid");

	// 鼠王的 active 是全体共用的静态开关, 不能按实例关, 只能跳过 Update
	[HarmonyPatch(typeof(DEN_RatKing), nameof(DEN_RatKing.Update))]
	[HarmonyPrefix]
	public static bool RatKing_Update(DEN_RatKing __instance) => !ChaseOwnerFeature.IsDrivenRemotely(__instance);

	[HarmonyPatch(typeof(DEN_Remains_Active), nameof(DEN_Remains_Active.Update))]
	[HarmonyPrefix]
	public static bool Remains_Update(DEN_Remains_Active __instance) => !ChaseOwnerFeature.IsDrivenRemotely(__instance);

	[HarmonyPatch(typeof(DEN_Remains_Active), nameof(DEN_Remains_Active.TickUpdate))]
	[HarmonyPrefix]
	public static bool Remains_TickUpdate(DEN_Remains_Active __instance) => !ChaseOwnerFeature.IsDrivenRemotely(__instance);

	// 所有者: 原版 TickUpdate 刚朝本机玩家 MovePosition; 别人正看着它时 (收到 "开始看" 且心跳未超时) 用原地 MovePosition 覆盖 (同一物理步内后调用的生效)
	[HarmonyPatch(typeof(DEN_Remains_Active), nameof(DEN_Remains_Active.TickUpdate))]
	[HarmonyPostfix]
	public static void Remains_TickUpdate_Post(DEN_Remains_Active __instance) {
		if (EntityMode.Current != EntityMode.Mode.Full || !__instance.active) return;
		var chase = ChaseOwnerFeature.Find(__instance);
		if (chase == null || !chase.RemoteWatched) return;
		var rigid = RemainsRigidField(__instance);
		if (rigid != null && !rigid.isKinematic) rigid.MovePosition(__instance.transform.position);
	}
}
