using HarmonyLib;
using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.XR;
using WKMPMod.Component;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.Util;
using static ENT_Player;
using static PlayerData;

namespace WKMPMod.Patch;

[HarmonyPatch(typeof(ENT_Player))]
public class Patch_ENT_Player {
	private static readonly AccessTools.FieldRef<ENT_Player, bool> CamLockedField =
		AccessTools.FieldRefAccess<ENT_Player, bool>("camLocked");
	private static readonly AccessTools.FieldRef<ENT_Player, float> CamSpeedField =
		AccessTools.FieldRefAccess<ENT_Player, float>("camSpeed");

	// 死亡信息总线调用
	[HarmonyPatch(nameof(ENT_Player.Kill))]
	[HarmonyPrefix]
	public static void Kill_NotifyPlayerDeath(ENT_Player __instance, string type, Damageable.DamageInfo damageInfo) {
		// 死亡切换发生前通知总线
		// 避免死亡后重复通知
		FieldInfo field = typeof(ENT_Player).GetField("godmode", BindingFlags.NonPublic | BindingFlags.Instance);
		var godmode = (bool)field.GetValue(__instance);
		if (MPCore.IsInLobby && !__instance.dead && !CL_GameManager.gMan.IsReviving() && !godmode) {
			MPEventBusGame.NotifyPlayerDeath(type);
			MPMain.LogInfo(Localization.Get("Patch.PlayerDeath", type));
		}
	}

	// 同步火焰伤害倍率
	[HarmonyPatch("Awake")]
	[HarmonyPostfix]
	public static void Awake_ResetFireMult(ENT_Player __instance) {
		if (MPCore.IsInLobby) {
			__instance.fireTimeMult = MPCore.damageRules.FireTime;
			__instance.fireDamageMult = MPCore.damageRules.FireDamage;
		}
	}

	// 抓取其他玩家
	[HarmonyPatch(nameof(ENT_Player.GrabPropUpdate))]
	[HarmonyPrefix]
	public static bool GrabPropUpdate_Prefix(int hand, ENT_Player __instance) {
		var handObj = __instance.hands[hand];
		if (handObj.grabTarget == null || !(handObj.grabTarget is RemoteEntity remoteEntity))
			return true;
		if (handObj.interactState != InteractType.grab) return false;

		// 控制相机转速
		CamSpeedField(__instance) = Mathf.Clamp(1f - remoteEntity.GetRigidbody().mass * 0.25f, 0.1f, 1f);

		// 松开条件检查
		float distanceToAnchor = Vector3.Distance(__instance.transform.position,
						handObj.grabTarget.transform.TransformPoint(handObj.grabTarget.anchorPoint));

		// 投掷: 按下投掷键, 施加向前力后松开
		if (InputManager.GetButton(handObj.throwButton).Down) {
			MPEventBusGame.NotifyPlayerAddForce(remoteEntity.playerId, __instance.camTransform.forward * 150f);
			__instance.StopInteraction(hand);
		}
		// 松开: 释放抓取键 或 物体距离过远
		else if (InputManager.GetButton(handObj.fireButton).Up || distanceToAnchor > __instance.interactDistance * 2f) {
			InternalDropLogic(hand, __instance);
		}

		return false;
	}

	// 放下其他玩家
	[HarmonyPatch("DropGrabbedProp")]
	[HarmonyPrefix]
	public static bool DropGrabbedProp_Prefix(int hand, ENT_Player __instance) {
		var handObj = __instance.hands[hand];
		if (handObj.grabTarget == null || !(handObj.grabTarget is RemoteEntity))
			return true;

		InternalDropLogic(hand, __instance);
		return false;
	}

	private static void InternalDropLogic(int hand, ENT_Player __instance) {
		var handObj = __instance.hands[hand];
		// 执行清理
		__instance.hands[hand].interactState = InteractType.none;
		__instance.hands[hand].grabTarget = null;
		__instance.hands[hand].currentInteract = null;
		__instance.hands[hand].holdTarget = null;

		// 使用访问器重置私有字段
		CamLockedField(__instance) = false;
		CamSpeedField(__instance) = 1f;
	}
}