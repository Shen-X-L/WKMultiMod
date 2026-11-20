using HarmonyLib;
using WKMPMod.EntitySync;

namespace WKMPMod.Patch;

/// <summary>
/// 可拖动物体同步 (entitymode full) 的入口: 本机抓起 / 松手, 别人持有时不能抓, 网络驱动时的插值与远距离锁
/// </summary>
[HarmonyPatch(typeof(CL_Prop))]
public class Patch_PropSync {
	[HarmonyPatch(nameof(CL_Prop.Interact))]
	[HarmonyPostfix]
	public static void Interact_Postfix(CL_Prop __instance, Clickable.InteractionInfo info) {
		if (info?.player != null && info.player == ENT_Player.playerObject) PropSyncModule.Instance.OnLocalGrab(__instance);
	}

	[HarmonyPatch(nameof(CL_Prop.StopInteract))]
	[HarmonyPostfix]
	public static void StopInteract_Postfix(CL_Prop __instance, ENT_Player p) {
		if (p != null && p == ENT_Player.playerObject) PropSyncModule.Instance.OnLocalDrop(__instance);
	}

	[HarmonyPatch(nameof(CL_Prop.CanInteract))]
	[HarmonyPostfix]
	public static void CanInteract_Postfix(CL_Prop __instance, ref bool __result) {
		if (__result && !PropSyncModule.Instance.CanLocalGrab(__instance)) __result = false;
	}

	// 原版远距离锁会改写 isKinematic, 网络驱动期间跳过
	[HarmonyPatch(nameof(CL_Prop.TickUpdate))]
	[HarmonyPrefix]
	public static bool TickUpdate_Prefix(CL_Prop __instance) => !PropSyncModule.Instance.IsControlled(__instance);

	[HarmonyPatch(nameof(CL_Prop.Update))]
	[HarmonyPostfix]
	public static void Update_Postfix(CL_Prop __instance) => PropSyncModule.Instance.ApplyMotion(__instance);
}
