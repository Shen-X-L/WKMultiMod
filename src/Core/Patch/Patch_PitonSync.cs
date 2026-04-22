using HarmonyLib;
using System.Collections.Generic;
using WKMPMod.World;

namespace WKMPMod.Patch;

[HarmonyPatch(typeof(HandItem_Piton), nameof(HandItem_Piton.PitonHit))]
public class Patch_HandItem_Piton_PitonHit {
	public static void Prefix(out HashSet<int> __state) {
		__state = PitonSyncManager.CaptureExistingHandholds();
	}

	public static void Postfix(HandItem_Piton __instance, HashSet<int> __state) {
		PitonSyncManager.RegisterNewLocalPiton(__instance, __state);
	}
}

[HarmonyPatch(typeof(CL_Handhold), nameof(CL_Handhold.HammerIn))]
public class Patch_CL_Handhold_HammerIn_PitonSync {
	public static void Postfix(CL_Handhold __instance) {
		PitonSyncManager.BroadcastHammerUpdate(__instance);
	}
}

[HarmonyPatch(typeof(CL_Handhold), "FixedUpdate")]
public class Patch_CL_Handhold_FixedUpdate_PitonSync {
	public static void Postfix(CL_Handhold __instance) {
		PitonSyncManager.BroadcastPeriodicUpdate(__instance);
	}
}

[HarmonyPatch(typeof(CL_Handhold_Breakable), "Update")]
public class Patch_CL_Handhold_Breakable_Update_PitonSync {
	public static void Postfix(CL_Handhold_Breakable __instance) {
		PitonSyncManager.BroadcastPeriodicUpdate(__instance);
	}
}
