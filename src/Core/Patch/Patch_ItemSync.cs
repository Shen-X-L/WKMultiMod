using HarmonyLib;
using System;
using System.Diagnostics;
using Unity.VisualScripting;
using WKMPMod.Core;
//using WKMPMod.World;
using WKMPMod.World;

namespace WKMPMod.Patch;

// Harmony 补丁: 世界初始化完成后通知物品同步管理器
[HarmonyPatch(typeof(WorldLoader), nameof(WorldLoader.Initialize))]
public class Patch_WorldLoader_Initialize_ItemSync {
	public static void Postfix() {
		ItemSyncManager.NotifyWorldInitialized();
	}
}

// Harmony 补丁: 物品被拾取后通知物品同步管理器
[HarmonyPatch(typeof(Item_Object), nameof(Item_Object.Pickup))]
public class Patch_Item_Object_Pickup_ItemSync {
	public static void Postfix(Item_Object __instance) {
		ItemSyncManager.NotifyLocalPickup(__instance);
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
		if (ItemSyncSuppress.IsSuppressedByItemInteract) return;	// 被交互模块丢弃时不同步
		ItemSyncManager.NotifyLocalDrop(item);
	}
}
#endregion