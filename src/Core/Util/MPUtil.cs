using DG.Tweening.Plugins.Core;
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using WKMPMod.Core;

namespace WKMPMod.Util;

public static class MPUtil {
	/// <summary>
	/// 通过预制体名称获取物品预制体
	/// </summary>
	/// <param name="prefabName">预制体资源名称</param>
	public static bool TryGetItemPrefab(string prefabName,out Item_Object item) {
		item = null;
		// 参数防御校验
		if (string.IsNullOrEmpty(prefabName)) {
			MPMain.LogWarning("[MP] prefabName is null or empty.");
			return false;
		}

		// 双重检测获取 Item_Object 组件
		Item_Object itemObject = null;
		try {
			itemObject = CL_AssetManager.GetItemObjectPrefab(prefabName);
		} catch (Exception e) {
			MPMain.LogError("[MP] " + e.ToString());
		}
		// 优先使用专用的 Item_Object 查找机制
		if (itemObject == null) {
			// 备用：从通用 GameObject 中获取组件
			GameObject go = CL_AssetManager.GetAssetGameObject(prefabName);
			itemObject = go?.GetComponent<Item_Object>();
		}
		if (itemObject == null || itemObject.itemData == null) {
			MPMain.LogError($"[MP] Item_Object or itemData not found for prefab '{prefabName}'");
			return false;
		}

		item = itemObject;
		return true;
	}

	public static readonly Dictionary<string, Color32> PlayerColorPresets = new(StringComparer.OrdinalIgnoreCase) {
		{ "default", new Color32(255, 255, 255, 255) },
		{ "white", new Color32(255, 255, 255, 255) },
		{ "red", new Color32(255, 80, 80, 255) },
		{ "orange", new Color32(255, 165, 0, 255) },
		{ "yellow", new Color32(255, 220, 64, 255) },
		{ "green", new Color32(80, 220, 120, 255) },
		{ "cyan", new Color32(64, 220, 255, 255) },
		{ "blue", new Color32(90, 140, 255, 255) },
		{ "purple", new Color32(170, 90, 255, 255) },
		{ "pink", new Color32(255, 110, 180, 255) },
		{ "black", new Color32(32, 32, 32, 255) },
	};

	/// <summary>
	/// 标准化PrefabKey
	/// 去除空格和Unity实例化后附加的(Clone)后缀<br/>
	///
	/// Normalizes a prefab key
	/// Removes whitespace and Unity's instantiated "(Clone)" suffix
	/// </summary>
	public static string CleanCloneName(string prefabKey) {
		return string.IsNullOrEmpty(prefabKey) ? string.Empty : prefabKey.Replace("(Clone)", string.Empty).Trim();
	}

	/// <summary>
	/// 构建变换层级路径. 例如 "Root[0]/Child[1]/Grandchild[0]".
	/// </summary>
	public static string BuildTransformPath(Transform transform) {
		if (transform == null) return string.Empty;
		var stack = new Stack<string>();
		var current = transform;
		while (current != null) {
			stack.Push($"{MPUtil.CleanCloneName(current.name)}[{current.GetSiblingIndex()}]");
			current = current.parent;
		}
		return string.Join("/", stack);
	}

	public static string SerializePlayerColor(Color32 color) => $"{color.r},{color.g},{color.b}";

	public static bool TryParsePlayerColor(string value, out Color32 color) {
		color = new Color32(255, 255, 255, 255);
		if (string.IsNullOrWhiteSpace(value)) {
			return false;
		}

		var parts = value.Split(',');
		if (parts.Length != 3) {
			return false;
		}

		if (!TryParseColorChannel(parts[0], out var r)
			|| !TryParseColorChannel(parts[1], out var g)
			|| !TryParseColorChannel(parts[2], out var b)) {
			return false;
		}

		color = new Color32((byte)r, (byte)g, (byte)b, 255);
		return true;
	}

	public static bool TryParseColorChannel(string value, out int channel) {
		if (int.TryParse(value.Trim(), out channel)) {
			return channel >= 0 && channel <= 255;
		}

		return false;
	}
}
