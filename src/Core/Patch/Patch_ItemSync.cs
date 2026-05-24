using HarmonyLib;
using WKMPMod.Core;
using WKMPMod.World;

namespace WKMPMod.Patch;

[HarmonyPatch(typeof(WorldLoader), nameof(WorldLoader.Initialize))]
public class Patch_WorldLoader_Initialize_ItemSync {
    public static void Postfix() {
        ItemSyncManager.NotifyWorldInitialized();
    }
}

[HarmonyPatch(typeof(Item_Object), nameof(Item_Object.Pickup))]
public class Patch_Item_Object_Pickup_ItemSync {
    public static void Postfix(Item_Object __instance) {
        ItemSyncManager.NotifyLocalPickup(__instance);
    }
}

[HarmonyPatch(typeof(Inventory), nameof(Inventory.DropItemIntoWorld))]
public class Patch_Inventory_DropItemIntoWorld_ItemSync {
    public static void Postfix(Item item) {
        ItemSyncManager.NotifyLocalDrop(item);
    }
}