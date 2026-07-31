using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using WKMPMod.Util;

namespace WKMPMod.Core;

public static class InventoryManager {

	public const string SAVE_WITH_DISK = "savewithdisk";    // 死亡后依然保留
	public const string ARTIFACT = "artifact";              // 神器
	public const string CONTRABAND = "contraband";          // 不可放入保险柜
	public const string TRINKET = "trinket";                // 饰品

	/// <summary>
	/// 获取物品清单字典
	/// </summary>
	public static Dictionary<string, byte> GetInventoryItems(bool checkBag = true, bool checkHands = true, bool checkPouches = true) {
		var inventory = Inventory.instance;
		var itemsDict = new Dictionary<string, byte>();

		if (inventory == null) {
			MPMain.LogWarning(Localization.Get("MPCore.InventoryDoesNotExist"));
			return itemsDict;
		}
		// 获取库存中的物品列表
		var items = inventory.GetItems(checkBag:checkBag, checkHands:checkHands, checkPouches:checkPouches);
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
				return GetBlacklistInventoryItems(tags: new string[] { SAVE_WITH_DISK, TRINKET });
		}
		return GetBlacklistInventoryItems(tags: new string[] { TRINKET });
	}

	/// <summary>
	/// 白名单标签内的背包物品
	/// </summary>
	public static Dictionary<string, byte> GetWhitelistInventoryItems(
		IReadOnlyList<string> tags = null, IReadOnlyList<string> prefabNames = null
		) {
		var inventory = Inventory.instance;
		var itemsDict = new Dictionary<string, byte>();

		if (inventory == null) {
			MPMain.LogWarning(Localization.Get("MPCore.InventoryDoesNotExist"));
			return itemsDict;
		}

		bool filterTags = tags is { Count: > 0 };
		bool filterPrefabs = prefabNames is { Count: > 0 };
		var blacklistedCache = new HashSet<string>();

		foreach (var item in inventory.GetItems()) {
			// 已通过 已在字典中
			if (itemsDict.ContainsKey(item.prefabName)) {
				itemsDict[item.prefabName]++;
				continue;
			}

			// 已确认黑名单
			if (blacklistedCache.Contains(item.prefabName)) continue;

			// 首次黑名单判断 不在tags中 && 不在预制体列表中
			if (!(filterTags && item.itemTags.Intersect(tags).Any())
				&& !(filterPrefabs && prefabNames.Contains(item.prefabName))) {
				blacklistedCache.Add(item.prefabName);
				continue;
			}

			itemsDict.Add(item.prefabName, 1);
		}

		return itemsDict;
	}

	/// <summary>
	/// 过滤黑名单标签内的背包物品
	/// </summary>
	public static Dictionary<string, byte> GetBlacklistInventoryItems(
		IReadOnlyList<string> tags = null, IReadOnlyList<string> prefabNames = null) {
		var inventory = Inventory.instance;
		var itemsDict = new Dictionary<string, byte>();

		if (inventory == null) {
			MPMain.LogWarning(Localization.Get("MPCore.InventoryDoesNotExist"));
			return itemsDict;
		}

		bool filterTags = tags is { Count: > 0 };
		bool filterPrefabs = prefabNames is { Count: > 0 };
		var blacklistedCache = new HashSet<string>();

		foreach (var item in inventory.GetItems()) {
			// 已通过 已在字典中
			if (itemsDict.ContainsKey(item.prefabName)) {
				itemsDict[item.prefabName]++;
				continue;
			}

			// 已确认黑名单
			if (blacklistedCache.Contains(item.prefabName)) continue;

			// 首次黑名单判断 在tags中 || 在预制体列表中
			if ((filterTags && item.itemTags.Intersect(tags).Any())
				|| (filterPrefabs && prefabNames.Contains(item.prefabName))) {
				blacklistedCache.Add(item.prefabName);
				continue;
			}

			itemsDict.Add(item.prefabName, 1);
		}

		return itemsDict;
	}
}

