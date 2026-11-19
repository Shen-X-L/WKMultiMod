using HarmonyLib;
using System;
using WKMPMod.Components;

namespace WKMPMod.Patch;

/// <summary>
/// 藤壶触手同步的补丁入口, 逻辑见 <see cref="BarnacleTongueSyncFeature"/>
/// </summary>
[HarmonyPatch(typeof(Barnacle_Tongue_Basic))]
public class Patch_BarnacleTongue {
	private static bool _cutting;

	[HarmonyPatch("AI")]
	[HarmonyPrefix]
	static bool AI_Prefix(Barnacle_Tongue_Basic __instance) =>
		!BarnacleTongueSyncFeature.TryGet(__instance, out var feature, out byte index) || feature.AllowAI(__instance, index);

	[HarmonyPatch(nameof(Barnacle_Tongue_Basic.Update))]
	[HarmonyPrefix]
	static void Update_Prefix(Barnacle_Tongue_Basic __instance) {
		if (BarnacleTongueSyncFeature.TryGet(__instance, out _, out _)) BarnacleTongueSyncFeature.SnapResting(__instance);
	}

	[HarmonyPatch(nameof(Barnacle_Tongue_Basic.Update))]
	[HarmonyPostfix]
	static void Update_Postfix(Barnacle_Tongue_Basic __instance) {
		if (BarnacleTongueSyncFeature.TryGet(__instance, out var feature, out byte index)) feature.AfterUpdate(__instance, index);
	}

	[HarmonyPatch(nameof(Barnacle_Tongue_Basic.Extend))]
	[HarmonyPostfix]
	static void Extend_Postfix(Barnacle_Tongue_Basic __instance) {
		if (BarnacleTongueSyncFeature.TryGet(__instance, out var feature, out byte index)) feature.OnExtend(__instance, index);
	}

	[HarmonyPatch(nameof(Barnacle_Tongue_Basic.Retract))]
	[HarmonyPrefix]
	static void Retract_Prefix(Barnacle_Tongue_Basic __instance, out bool __state) {
		var grabbed = BarnacleTongueSyncFeature.Grabbed(__instance);
		__state = grabbed != null && grabbed == ENT_Player.GetPlayer();
	}

	[HarmonyPatch(nameof(Barnacle_Tongue_Basic.Retract))]
	[HarmonyPostfix]
	static void Retract_Postfix(Barnacle_Tongue_Basic __instance, bool __state) {
		if (BarnacleTongueSyncFeature.TryGet(__instance, out var feature, out byte index)) feature.OnRetract(index, __state, _cutting);
	}

	// 触手被打断 (打到触手 / 藤壶受伤松开): 对正被抓的人也生效
	[HarmonyPatch(nameof(Barnacle_Tongue_Basic.KillTongue))]
	[HarmonyPrefix]
	static void KillTongue_Prefix() => _cutting = true;

	[HarmonyPatch(nameof(Barnacle_Tongue_Basic.KillTongue))]
	[HarmonyFinalizer]
	static Exception KillTongue_Finalizer(Exception __exception) {
		_cutting = false;
		return __exception;
	}

	[HarmonyPatch("ScanForHitEntity")]
	[HarmonyPrefix]
	static bool Scan_Prefix(Barnacle_Tongue_Basic __instance) =>
		!BarnacleTongueSyncFeature.TryGet(__instance, out var feature, out byte index) || feature.AllowScan(__instance, index);

	[HarmonyPatch("ScanForHitEntity")]
	[HarmonyPostfix]
	static void Scan_Postfix(Barnacle_Tongue_Basic __instance) {
		if (BarnacleTongueSyncFeature.TryGet(__instance, out var feature, out byte index)) feature.AfterScan(__instance, index);
	}

	[HarmonyPatch(nameof(Barnacle_Tongue_Basic.FindTarget))]
	[HarmonyPostfix]
	static void FindTarget_Postfix(Barnacle_Tongue_Basic __instance) {
		if (BarnacleTongueSyncFeature.TryGet(__instance, out var feature, out _)) feature.AfterFindTarget(__instance);
	}
}
