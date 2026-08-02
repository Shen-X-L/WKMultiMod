using HarmonyLib;
using WKMPMod.World;

namespace WKMPMod.Patch;

[HarmonyPatch(typeof(GameEntity), "Damage")]
public class Patch_GameEntity_Damage_EnemySync {
	public static void Prefix(GameEntity __instance, Damageable.DamageInfo info) {
		EnemySyncManager.NotifyLocalEnemyDamage(__instance, info);
	}
}
