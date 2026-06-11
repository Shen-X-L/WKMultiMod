using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using WKMPMod.Util;

namespace WKMPMod.Core;

public static class InventoryManager {

	public const string SAVE_WITH_DISK = "savewithdisk";	// 死亡后依然保留
	public const string ARTIFACT = "artifact";              // 神器
	public const string CONTRABAND = "contraband";          // 不可放入保险柜
	public const string TRINKET = "trinket";				// 饰品

	/// <summary>
	/// 获取物品清单字典
	/// </summary>
	public static Dictionary<string, byte> GetInventoryItems() {
		var inventory = Inventory.instance;
		var itemsDict = new Dictionary<string, byte>();

		if (inventory == null) {
			MPMain.LogWarning(Localization.Get("MPCore.InventoryDoesNotExist"));
			return itemsDict;
		}
		// 获取库存中的物品列表
		var items = inventory.GetItems();
		foreach (var item in items) {
			itemsDict.TryAdd(item.prefabName, 0);
			itemsDict[item.prefabName]++;
		}

		return itemsDict;
	}

	/// <summary>
	/// 获取死亡掉落物品清单字典
	/// </summary>
	public static Dictionary<string, byte> GetDeathInventoryItems() {
		CL_SaveManager.SaveState save = CL_SaveManager.GetMostRecentSaveStateByType(CL_SaveManager.SaveState.SaveType.disk);
		if (save != null) {
			if (ENT_Player.GetPlayer().HasPerk("Perk_AnomalousBonds")) 
				return new Dictionary<string, byte>();
			else
				return GetBlacklistInventoryItems(SAVE_WITH_DISK, TRINKET);
		}
		return GetBlacklistInventoryItems(TRINKET);
	}

	/// <summary>
	/// 白名单标签内的背包物品
	/// </summary>
	public static Dictionary<string, byte> GetWhitelistInventoryItems(params string[] args) {
		var inventory = Inventory.instance;
		var itemsDict = new Dictionary<string, byte>();
		if (inventory == null) {
			MPMain.LogWarning(Localization.Get("MPCore.InventoryDoesNotExist"));
			return itemsDict;
		}
		// 获取库存中的物品列表
		var items = inventory.GetItems();

		foreach (var item in items) {
			if (!item.itemTags.Intersect(args).Any()) continue;
			itemsDict.TryAdd(item.prefabName, 0);
			itemsDict[item.prefabName]++;
		}

		return itemsDict;
	}

	/// <summary>
	/// 过滤黑名单标签内的背包物品
	/// </summary>
	public static Dictionary<string, byte> GetBlacklistInventoryItems(params string[] args) {
		var inventory = Inventory.instance;
		var itemsDict = new Dictionary<string, byte>();
		if (inventory == null) {
			MPMain.LogWarning(Localization.Get("MPCore.InventoryDoesNotExist"));
			return itemsDict;
		}
		// 获取库存中的物品列表
		var items = inventory.GetItems();

		foreach (var item in items) {
			if(item.itemTags.Intersect(args).Any()) continue;
			itemsDict.TryAdd(item.prefabName, 0);
			itemsDict[item.prefabName]++;
		}

		return itemsDict;
	}
}

