using HarmonyLib;
using WKMPMod.World;

namespace WKMPMod.Patch;

[HarmonyPatch(typeof(GameEntity))]
public class Patch_GameEntity {
	#region[伤害同步]

	[HarmonyPatch(nameof(GameEntity.Damage))]
	[HarmonyPrefix]
	public static void Patch_Damage(GameEntity __instance, Damageable.DamageInfo info) {
		EnemySyncManager.NotifyLocalEnemyDamage(__instance, info);
	}

	#endregion

	#region[Harmony Patches - 实体生命周期拦截]
	// 监听 GameEntity 启用/生成,提供增量扫描数据源
	[HarmonyPatch(nameof(GameEntity.OnEnable))]
	[HarmonyPostfix]
	public static void Patch_OnEnable(GameEntity __instance) {
		EnemySyncManager.OnEntityEnabled(__instance);
	}

	/// <summary>
	/// 监听 GameEntity 禁用/销毁,立即通知注销记录
	/// </summary>
	[HarmonyPatch(nameof(GameEntity.OnDisable))]
	[HarmonyPrefix]
	public static void Patch_OnDisable(GameEntity __instance) {
		EnemySyncManager.OnEntityDisabled(__instance);
	}
}

#endregion