using HarmonyLib;
using System;
using System.Diagnostics;
using Unity.VisualScripting;
using WKMPMod.Core;
//using WKMPMod.World;
using WKMPMod.World;

namespace WKMPMod.Patch;

// Harmony 补丁: 物品被拾取后通知物品同步管理器
[HarmonyPatch(typeof(Item_Object))]
public class Patch_Item_Object {
	// 物品被拾取 先判断是p2p物品还是场景物品
	[HarmonyPatch(nameof(Item_Object.Pickup))]
	[HarmonyPostfix]
	public static void Patch_Pickup(Item_Object __instance) {
		ItemSyncManager.NotifyLocalPickup(__instance);
	}

	// 物品生成时判断是否是场景物品并对比记录
	[HarmonyPatch("Start")]
	[HarmonyPostfix]
	public static void Patch_Start(Item_Object __instance) {
		// 当物品 Start 执行完毕后, 触发网络层的本地关卡物品注册/反向绑定
		SceneItemManager.OnSceneItemStarted(__instance);
	}
}

#region[丢弃物品同步管理器]

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
		if (ItemSyncSuppress.IsSuppressedByFloppy) return;			// 被软盘交互模块丢弃时不同步
		if (ItemSyncSuppress.IsSuppressedByItemInteract) return;    // 被交互模块丢弃时不同步
		DroppedItemManager.NotifyLocalDrop(item);
	}
}
#endregion
