using HarmonyLib;
using WKMPMod.Core;
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

// Harmony 补丁: 物品被丢弃到世界后通知物品同步管理器
[HarmonyPatch(typeof(Inventory), nameof(Inventory.DropItemIntoWorld))]
public class Patch_Inventory_DropItemIntoWorld_ItemSync {
	public static void Postfix(Item item) {
		ItemSyncManager.NotifyLocalDrop(item);
	}
}