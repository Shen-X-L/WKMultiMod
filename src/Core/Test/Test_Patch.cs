using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Text;
using WKMPMod.Core;

namespace WKMPMod.Test;

public class Test_Patch {
	[HarmonyPatch(typeof(ENT_Player), nameof(ENT_Player.Damage))]
	public class Test_Patch_ENT_Player_Damage {
		public static void Prefix(Damageable.DamageInfo info) {
			if (info == null)
				return;

			MPMain.LogWarning(
				$"[MP Debug] " +
				$"伤害量:{info.amount} " +
				$"伤害类型:{info.type} " +
				$"伤害位置:{info.position} " +
				$"冲击力:{info.force}");

			// sourceObject 可能为空，最好判空
			if (info.sourceObject != null) MPMain.LogWarning($"伤害来源:{info.sourceObject.name}");


			// tags 也可能为空
			if (info.tags != null)
				foreach (var tag in info.tags) MPMain.LogWarning($"伤害标签:{tag}");
		}
	}
}
