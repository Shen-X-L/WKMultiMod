using HarmonyLib;
using System.Collections.Generic;
using WKMPMod.Core;
using WKMPMod.Util;

namespace WKMPMod.Patch;

/// <summary>
/// 修复: 抓点被销毁时强制松手死循环 (玩家离开时游戏卡死).
/// CL_Handhold.OnDestroy -> ForceDropHands 执行 while (hands.Count > 0) hands[0].DropHand();
/// 依赖 DropHand -> ENT_Player.StopInteraction -> currentInteract.StopInteract -> OnLetGo 把手从列表移除.
/// 但销毁过程中抓点本身已被视为 destroyed, StopInteraction 中 IsNullOrDestroyed(currentInteract) 为真,
/// 跳过 StopInteract, 手永远不会被移除 → 主线程死循环.
/// 远程玩家模型带有可攀爬抓点 (RPPrefabProcessor 添加 CL_Handhold), 本地玩家正挂在对方身上时对方离开,
/// RPContainer.Destroy 销毁模型即触发. 这里改为: 每只手松手后若仍在列表中则强制移除.
/// </summary>
[HarmonyPatch(typeof(CL_Handhold), nameof(CL_Handhold.ForceDropHands))]
public class Patch_CL_Handhold_ForceDropHands {
	private static readonly AccessTools.FieldRef<CL_Handhold, List<ENT_Player.Hand>> HandsField =
		AccessTools.FieldRefAccess<CL_Handhold, List<ENT_Player.Hand>>("hands");

	static bool Prefix(CL_Handhold __instance) {
		var hands = HandsField(__instance);
		if (hands == null) return false;
		int forced = 0;
		foreach (var hand in hands.ToArray()) {
			if (hand != null) hand.DropHand();
			if (hands.Remove(hand)) forced++;
		}
		hands.Clear();
		if (forced > 0) MPMain.LogInfo(Localization.GetLog("Patch.ForceDropHandsCleared", __instance.name, forced));
		return false;
	}
}
