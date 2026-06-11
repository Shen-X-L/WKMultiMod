using HarmonyLib;
using System;
using System.Diagnostics;
using Unity.VisualScripting;
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


#region[丢弃物品同步管理器]
// 在任意可访问位置定义标志
internal static class ItemSyncSuppress {
	// 用计数器而非 bool，防止 Floppy.Interact 内部多次触发 DropItemIntoWorld 时标志提前归零
	internal static int FloppyInteractDepth = 0;
	internal static bool IsSuppressedByFloppy => FloppyInteractDepth > 0;
}

// 检查是否是软盘交互模块 是则增加标志位
[HarmonyPatch(typeof(Item_InteractionModule_Floppy), nameof(Item_InteractionModule_Floppy.Interact))]
public class Patch_Floppy_Interact_SuppressItemSync {
	public static void Prefix() => ItemSyncSuppress.FloppyInteractDepth++;
	public static void Postfix() => ItemSyncSuppress.FloppyInteractDepth--;
}

// Harmony 补丁: 物品被丢弃到世界后通知物品同步管理器
[HarmonyPatch(typeof(Inventory), nameof(Inventory.DropItemIntoWorld))]
public class Patch_Inventory_DropItemIntoWorld_ItemSync {
	public static void Postfix(Item item) {
		if (ItemSyncSuppress.IsSuppressedByFloppy) return; // 被交互模块丢弃时不同步
		ItemSyncManager.NotifyLocalDrop(item);
	}
}
#endregion

// Harmony 补丁: 物品被放下时0.3秒内不可重复捡起,留给网络同步时间
[HarmonyPatch(typeof(Item_Object),nameof(Item_Object.OnDrop))]
public class Patch_Item_Object_OnDrop_ItemSync {

	private static readonly AccessTools.FieldRef<Item_Object, float> _dropTimeField =
		AccessTools.FieldRefAccess<Item_Object, float>("dropTime");

	private const float DROP_TIME = 0.3f;

	public static void Postfix(Item_Object __instance) {
		if(MPCore.CanSync)
			_dropTimeField(__instance) = DROP_TIME;
	}
}