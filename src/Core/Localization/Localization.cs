using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using WKMPMod.Core;
namespace WKMPMod.Util;

public static class Localization {
	// 主表：按类别存储字典
	private static Dictionary<string, Dictionary<string, string>> _table =
		new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

	// 扁平化缓存，用于快速查找
	private static Dictionary<string, string> _flatCache = null;

	private const string FILE_PREFIX = "texts";

	/// <summary>
	/// 加载本地化文件
	/// </summary>
	public static void Load() {
		// 获取插件路径
		string pluginDirectory = Path.GetDirectoryName(typeof(Localization).Assembly.Location);
		// 获取系统语言
		string language = GetGameLanguage(); 
		string fileName = $"{FILE_PREFIX}_{language.ToLower()}.json";
		string filePath = Path.Combine(pluginDirectory, fileName);

		// 如果找不到对应语言文件，使用默认版
		if (!File.Exists(filePath)) {
			// 未在: {filePath} 发现文本文件 {fileName}
			MPMain.LogError($"[Localization] {fileName} file not found at path: {filePath}");
			// 使用英文版
			fileName = $"{FILE_PREFIX}_en.json";
			filePath = Path.Combine(pluginDirectory, fileName);

			if (!File.Exists(filePath)) {
				MPMain.LogError($"[Localization] Localization file not found, please confirm that {FILE_PREFIX}_en.json file exists");
				return;
			}
		}

		try {
			string jsonContent = File.ReadAllText(filePath);

			_table = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, string>>>(jsonContent);

			// 重置扁平化缓存
			_flatCache = null;

			int totalEntries = 0;
			foreach (var category in _table) {
				totalEntries += category.Value.Count;
			}
			// 已成功加载 {_table.Count} 个类别,共 {totalEntries} 个条目
			MPMain.LogInfo($"[Localization] Successfully loaded {_table.Count} categories with {totalEntries} entries");
		} catch (Exception e) {
			// 无法分析本地化文件
			MPMain.LogError($"[Localization] Unable to parse localization file: {e.Message}");
		}
	}

	/// <summary>
	/// 获取本地化文本(分类.键名格式)
	/// </summary>
	public static string Get(string key, params object[] args) {
		// 确保扁平化缓存已构建
		if (_flatCache == null) {
			BuildFlatCache();
		}

		// 查找键
		if (!_flatCache.TryGetValue(key, out string pattern)) {
			// 键未找到: {key}
			MPMain.LogWarning($"[Localization] Key not found: {key}");
			return key;
		}

		// 无参数直接返回
		if (args == null || args.Length == 0) {
			return pattern;
		}

		// 格式化字符串
		try {
			return string.Format(pattern, args);
		} catch (FormatException e) {
			MPMain.LogError($"[Localization] Format error for key '{key}': {e.Message}");
			return pattern;
		}
	}

	/// <summary>
	/// 获取本地化文本(分类，键名分开)
	/// </summary>
	public static string Get(string category, string key, params object[] args) {
		// 验证参数
		if (string.IsNullOrEmpty(category)) {
			// 分类为空
			MPMain.LogWarning("[Localization] Category is null or empty");
			return key;
		}

		// 查找分类
		if (!_table.TryGetValue(category, out var categoryDict)) {
			// 分类未找到
			MPMain.LogWarning($"[Localization] Category not found: {category}");
			return $"[{category}] {key}";
		}

		// 查找键
		if (!categoryDict.TryGetValue(key, out string pattern)) {
			// 子选项未找到
			MPMain.LogWarning($"[Localization] Key '{key}' not found in category '{category}'");
			return $"[{category}] {key}";
		}

		// 无参数直接返回
		if (args == null || args.Length == 0) {
			return pattern;
		}

		// 格式化字符串
		try {
			return string.Format(pattern, args);
		} catch (FormatException e) {
			MPMain.LogError($"[Localization] Format error for '{category}.{key}': {e.Message}");
			return pattern;
		}
	}

	/// <summary>
	/// 检查键是否存在
	/// </summary>
	public static bool HasKey(string key) {
		if (_flatCache == null) {
			BuildFlatCache();
		}
		return _flatCache.ContainsKey(key);
	}

	/// <summary>
	/// 检查分类和键是否存在
	/// </summary>
	public static bool HasKey(string category, string key) {
		if (_table.TryGetValue(category, out var categoryDict)) {
			return categoryDict.ContainsKey(key);
		}
		return false;
	}

	/// <summary>
	/// 获取所有分类
	/// </summary>
	public static IEnumerable<string> GetAllCategories() {
		return _table.Keys;
	}

	/// <summary>
	/// 获取指定分类的所有键
	/// </summary>
	public static IEnumerable<string> GetKeysInCategory(string category) {
		if (_table.TryGetValue(category, out var categoryDict)) {
			return categoryDict.Keys;
		}
		return new List<string>();
	}

	/// <summary>
	/// 构建扁平化缓存 (内部使用)
	/// </summary>
	private static void BuildFlatCache() {
		_flatCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		foreach (var category in _table) {
			foreach (var kvp in category.Value) {
				// 扁平化格式为 类名.文本名
				string flatKey = $"{category.Key}.{kvp.Key}";
				_flatCache[flatKey] = kvp.Value;
			}
		}
	}

	public static string GetGameLanguage() {
		// 根据系统语言返回 "zh", "en" 等
		switch (Application.systemLanguage) {
			case SystemLanguage.Chinese:
			case SystemLanguage.ChineseSimplified:
				return "zh";
			case SystemLanguage.ChineseTraditional:
				return "zh_tw";
			case SystemLanguage.Japanese:
				return "ja";
			case SystemLanguage.Korean:
				return "ko";
			case SystemLanguage.Russian:
				return "ru";
			case SystemLanguage.German:
				return "de";
			case SystemLanguage.French:
				return "fr";
			case SystemLanguage.Spanish:
				return "es";
			default:
				return "en";
		}
	}
}