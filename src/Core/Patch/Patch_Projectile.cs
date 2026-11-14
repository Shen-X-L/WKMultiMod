using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
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
	private static void CreateHitEffectPrefix(Projectile __instance, RaycastHit hit) {
		if (__instance.HasHitSurface()) return;
		ProjectileSyncModule.Instance?.OnCreateHitEffect(__instance,ref hit);
	}
}