using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Unity.VisualScripting;
using UnityEngine;
using WKMPMod.Components;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.World;
using static MonoMod.Cil.RuntimeILReferenceBag.FastDelegateInvokers;

namespace WKMPMod.Patch;

#region[物品初始化/拾取同步]

// Harmony 补丁: 物品被拾取后通知物品同步管理器
[HarmonyPatch(typeof(Item_Object))]
public class Patch_Item_Object {
	// 物品被拾取 先判断是p2p物品还是场景物品
	[HarmonyPatch(nameof(Item_Object.OnPickup))]
	[HarmonyPostfix]
	public static void Patch_OnPickup(Item_Object __instance) {
		NotifyLocalPickup(__instance);
	}

	/// <summary>
	/// 本地玩家拾取物品时调用 (由 Harmony 补丁 Patch_Item_Object_Pickup_ItemSync 在 Postfix 触发).
	/// 场景物品: 同队广播移除
	/// 丢弃物品
	/// </summary>
	public static void NotifyLocalPickup(Item_Object itemObject) {
		ItemSyncBridge.OnLocalPickup(itemObject);
	}

	// 物品生成时判断是否是场景物品并对比记录
	[HarmonyPatch("Start")]
	[HarmonyPostfix]
	public static void Patch_Start(Item_Object __instance) {
		// 当物品 Start 执行完毕后, 触发网络层的本地关卡物品注册/反向绑定
		SceneItemModule.Instance.OnSceneItemStarted(__instance);
	}
}

#endregion

#region[物品丢弃同步]

// 在任意可访问位置定义标志
internal static class ItemSyncSuppress {
	// 用计数器而非 bool, 防止 Floppy.Interact 内部多次触发 DropItemIntoWorld 时标志提前归零
	internal static byte FloppyInteractDepth = 0;
	internal static bool IsSuppressedByFloppy => FloppyInteractDepth > 0;

	// 用计数器而非 bool, 防止 Floppy.Interact 内部多次触发 DropItemIntoWorld 时标志提前归零
	internal static byte ItemInteractDepth = 0;
	internal static bool IsSuppressedByItemInteract => ItemInteractDepth > 0;
}

// 检查是否是软盘交互模块 是则增加标志位
[HarmonyPatch(typeof(Item_InteractionModule_Floppy), nameof(Item_InteractionModule_Floppy.Interact))]
public class Patch_Floppy_Interact_SuppressItemSync {
	public static void Prefix() => ItemSyncSuppress.FloppyInteractDepth++;
	public static void Postfix() => ItemSyncSuppress.FloppyInteractDepth--;
}

// 检查是否是通用交互模块 是则增加标志位
[HarmonyPatch(typeof(UT_ItemInteractor), nameof(UT_ItemInteractor.Interact))]
public class Patch_UT_ItemInteractor_SuppressItemSync {
	public static void Prefix() => ItemSyncSuppress.ItemInteractDepth++;
	public static void Postfix() => ItemSyncSuppress.ItemInteractDepth--;
}


// Harmony 补丁: 物品被丢弃到世界后通知物品同步管理器
[HarmonyPatch(typeof(Inventory), nameof(Inventory.DropItemIntoWorld))]
public class Patch_Inventory_DropItemIntoWorld_ItemSync {
	public static void Postfix(Item item) {
		if (ItemSyncSuppress.IsSuppressedByFloppy) return;          // 被软盘交互模块丢弃时不同步
		if (ItemSyncSuppress.IsSuppressedByItemInteract) return;    // 被交互模块丢弃时不同步
		DroppedItemModule.Instance.NotifyLocalDrop(item);
	}
}

#endregion

[HarmonyPatch(typeof(Item))]
public class Patch_Item {

	[HarmonyPatch(nameof(Item.Destroy))]
	[HarmonyPrefix]
	public static void Patch_Destroy(Item __instance) {
		if (!MPCore.CanSync) return;
		var itemObject = __instance.GetDropObject(false);
		if (itemObject != null && itemObject.TryGetComponent<NetworkedItem>(out var identity))
			DroppedItemModule.Instance.NotifyLocalRemove(identity);
	}
}

#region[非拾取物品交互]

[HarmonyPatch(typeof(HandItem_Crafter))]
public class Patch_HandItem_Crafter {

	private static readonly AccessTools.FieldRef<HandItem_Crafter, float> _curCraftTimeField =
		AccessTools.FieldRefAccess<HandItem_Crafter, float>("curCraftTime");
	private static readonly AccessTools.FieldRef<HandItem_Crafter, List<GameEntity>> _pickedupEntitiesField =
		AccessTools.FieldRefAccess<HandItem_Crafter, List<GameEntity>>("pickedupEntities");

	#region[扫描候选替换]

	[HarmonyPatch(nameof(HandItem_Crafter.Update))]
	[HarmonyTranspiler]
	public static IEnumerable<CodeInstruction> Transpiler_Update(IEnumerable<CodeInstruction> instructions) {
		var codes = new List<CodeInstruction>(instructions);

		// 定位 RaycastHit[] 保存位置 (stloc.2)
		int hitsStore = codes.FindIndex(c => c.opcode == OpCodes.Stloc_2);
		if (hitsStore < 0) throw new InvalidOperationException("[MP IL Transpiler] 找不到 stloc.2");

		// 定位 foreach 循环结束后的 ldloc.0
		int foreachEnd = codes.FindIndex(hitsStore + 1, c => c.opcode == OpCodes.Ldloc_0);
		if (foreachEnd < 0) throw new InvalidOperationException("[MP IL Transpiler] 找不到 foreach 结尾 ldloc.0");

		// 删除整个 foreach 循环体指令
		codes.RemoveRange(hitsStore + 1, foreachEnd - hitsStore - 1);

		// 在原位置直接插入 3 条替换指令
		var filterMethod = AccessTools.Method(typeof(Patch_HandItem_Crafter), nameof(FilterAndProcessHits));
		codes.InsertRange(
			hitsStore + 1,
			new[] {
				new CodeInstruction(OpCodes.Ldarg_0),
				new CodeInstruction(OpCodes.Ldloc_2),
				new CodeInstruction(OpCodes.Call, filterMethod)
			});

		return codes;
	}

	/// <summary>
	/// 替代的扫描判断函数
	/// </summary>
	public static void FilterAndProcessHits(HandItem_Crafter instance, RaycastHit[] hits) {
		foreach (RaycastHit hit in hits) {
			GameEntity entity = hit.collider.GetComponentInParent<GameEntity>();

			if (entity == null || _pickedupEntitiesField(instance).Contains(entity)) continue;

			ObjectTagger tagger = entity.GetTagger();

			// 检查标签和类型过滤条件
			if (tagger != null
				&& (instance.tagWhitelist.Count == 0 || tagger.HasTagInList(instance.tagWhitelist.ToArray()))
				&& (instance.tagBlacklist.Count == 0 || !tagger.HasTagInList(instance.tagBlacklist.ToArray()))
				&& (!instance.limitToItems || (bool)entity.GetComponent<Item_Object>())) {

				if (entity.TryGetComponent<Item_Object>(out var itemObj)) {
					// 没有本地物品权限
					if (itemObj.itemData.HasTag(MPKeys.OTHER_PLAYER_ITEM) && !ItemSyncBridge.OnLocalMove(itemObj))
						continue;
					// 游戏本体默认锁定
					if (itemObj.itemData.HasTag("inLocker") || itemObj.itemData.HasTag("unpurchased")) continue;
				}
				_pickedupEntitiesField(instance).Add(entity);
				instance.clipHandler.PlaySound("item:pickupitem"); // 物品:拾取物品
				_curCraftTimeField(instance) = 0f;
				instance.anim.SetTrigger("Use");
			}
		}
	}

	#endregion

	#region[合成创建替换]

	[HarmonyPatch("TryCraft")]
	[HarmonyTranspiler]
	public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
		var codes = new List<CodeInstruction>(instructions);

		int instantiateIndex = codes.FindIndex(c =>
			c.opcode == OpCodes.Call
			&& c.operand is MethodInfo m
			&& m.Name == nameof(UnityEngine.Object.Instantiate)
		);

		if (instantiateIndex < 0) return codes;

		// Instantiate 后面应该是 stloc.s
		var stlocIndex = instantiateIndex + 1;
		if (stlocIndex >= codes.Count ||
			codes[stlocIndex].opcode != OpCodes.Stloc_S) {
			throw new InvalidOperationException("Instantiate 后没有找到预期的 stloc.s");
		}

		var local = codes[stlocIndex].operand;

		var saveMethod = AccessTools.Method(typeof(Patch_HandItem_Crafter), nameof(LocalCreate));
		codes.InsertRange(stlocIndex + 1, new[]{
			new CodeInstruction(OpCodes.Ldloc_S, local),
			new CodeInstruction(OpCodes.Call, saveMethod)
		});

		return codes;
	}

	public static void LocalCreate(GameEntity gameEntity) {
		if (!MPCore.CanSync || !gameEntity.TryGetComponent<Item_Object>(out var itemObject)) return;
		DroppedItemModule.Instance.NotifyLocalDrop(itemObject);
	}

	#endregion

}

[HarmonyPatch(typeof(HandItem_GrapplingHook))]
public class Patch_HandItem_GrapplingHook {

	[HarmonyPatch("Grapple")]
	[HarmonyTranspiler]
	public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
		var codes = new List<CodeInstruction>(instructions);

		// 修改 1：替换 HasTag 为 CanInteract
		// IL_009f: ldloc.2
		// IL_00a0: ldfld class Item Item_Object::itemData <-删除起点 校验
		// IL_00a5: ldstr "inLocker" <- 定位点
		// IL_00aa: callvirt instance bool Item::HasTag(string)
		// IL_00af: brfalse.s IL_00b4
		//	IL_00b1: ldc.i4.0 <- 删除终点
		//	IL_00b2: stloc.s 6 <- 校验
		var myFunc = AccessTools.Method(typeof(Patch_HandItem_GrapplingHook), nameof(CanInteract));

		int tagIndex = codes.FindIndex(c =>
			c.opcode == OpCodes.Ldstr && c.operand is string s && s == "inLocker");

		if (tagIndex < 0) throw new InvalidOperationException("Grapple: inLocker not found.");

		int start = tagIndex - 1;

		// 校验起点与终点
		if (codes[start].opcode != OpCodes.Ldfld || codes[start + 4].opcode != OpCodes.Ldc_I4_0) 
			throw new InvalidOperationException("Grapple: unexpected IL layout for inLocker.");
		
		// 只删除中间 5 条指令, 保留首尾 ldloc.2 和 stloc.s
		codes.RemoveRange(start, 5);
		codes.InsertRange(start, new[] { new CodeInstruction(OpCodes.Call, myFunc) });

		// 修改 2：在 Destroy 之前插入 BeforeDestroy
		// IL_0199: ldloc.1
		// IL_019a: callvirt instance class UnityEngine.GameObject::get_gameObject()
		// IL_019f: call void UnityEngine.Object::Destroy(class UnityEngine.Object)

		var beforeDestroy = AccessTools.Method(typeof(Patch_HandItem_GrapplingHook), nameof(BeforeDestroy));

		int destroyIndex = codes.FindIndex(c =>
			c.opcode == OpCodes.Call && c.operand is MethodInfo m
			&& m.DeclaringType == typeof(UnityEngine.Object) && m.Name == nameof(UnityEngine.Object.Destroy));

		if (destroyIndex < 0) throw new InvalidOperationException("Grapple: Object.Destroy not found.");

		codes.InsertRange(destroyIndex, new[] {new CodeInstruction(OpCodes.Dup),new CodeInstruction(OpCodes.Call, beforeDestroy)});

		return codes;
	}

	public static bool CanInteract(Item_Object itemObject) {
		if (itemObject.itemData.HasTag("inLocker")) return false;
		if (!MPCore.CanSync || !itemObject.itemData.HasTag(MPKeys.OTHER_PLAYER_ITEM)) return true;
		return ItemSyncBridge.OnLocalMove(itemObject);
	}

	public static void BeforeDestroy(GameObject obj) {
		// 判断是不是联机物品
		if (!MPCore.CanSync || !obj.TryGetComponent<Item_Object>(out var itemObject)) return;
		// 广播销毁
		ItemSyncBridge.OnLocalPickup(itemObject);
	}
}

#endregion