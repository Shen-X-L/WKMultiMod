using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using WKMPMod.Core;
namespace WKMPMod.Util;

public static class Localization {
	// 用于随机化文本的包装结构,支持单字符串或字符串数组
	public struct LocalizedValue {
		private readonly object _data;

		public LocalizedValue(object data) => _data = data;

		public string[] AsArray => _data as string[];
		public string AsString => _data as string;
		public bool IsArray => _data is string[];
		public int Count => IsArray ? ((string[])_data).Length : 1;

		// 获取指定索引的方法,越界时返回最后一个元素
		public string GetValue(int index = 0) {
			if (_data is string[] arr) {
				return arr[Math.Clamp(index, 0, arr.Length - 1)];
			}
			return _data?.ToString() ?? string.Empty;
		}
		public string GetValue(System.Random rand) {
			if (_data is string[] arr) {
				// 数组: 随机取一项
				return arr[rand.Next(arr.Length)];
			}
			// 单行文本: 直接返回
			return _data?.ToString() ?? string.Empty;
		}
	}

	// 主表:按类别存储字典
	private static Dictionary<string, Dictionary<string, LocalizedValue>> _table;

	// 扁平化缓存,用于快速查找
	private static Dictionary<string, LocalizedValue> _flatCache;

	// 本地化文件前缀
	private const string FILE_PREFIX = "texts";

	// 静态随机实例,用于随机文本选择,避免频繁创建 Random 对象
	private static readonly System.Random _staticRandom = new System.Random();

	#region[初始化字典]

	/// <summary>
	/// 加载本地化文件
	/// </summary>
	public static void Load() {
		// 获取插件路径
		string pluginDirectory = MPMain.path;
		// 获取系统语言
		string language = GetGameLanguage(); 
		string fileName = $"{FILE_PREFIX}_{language.ToLower()}.json";
		string filePath = Path.Combine(pluginDirectory, fileName);

		// 如果找不到对应语言文件,使用默认版
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

			var rawTable = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, object>>>(jsonContent);

			_table = new Dictionary<string, Dictionary<string, LocalizedValue>>();

			// 转换原始表到支持 LocalizedValue 的结构
			foreach (var category in rawTable) {
				_table[category.Key] = new Dictionary<string, LocalizedValue>();
				foreach (var kvp in category.Value) {
					if (kvp.Value is JArray jarr) {
						// 如果值是数组,转换为 LocalizedValue 包装的字符串数组
						_table[category.Key][kvp.Key] = new LocalizedValue(jarr.Select(x => x.ToString()).ToArray());
					} else {
						// 否则直接转换为字符串
						_table[category.Key][kvp.Key] = new LocalizedValue(kvp.Value?.ToString());
					}
				}
			}

			// 重置扁平化缓存
			BuildFlatCache();

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
	/// 构建扁平化缓存 (内部使用)
	/// </summary>
	private static void BuildFlatCache() {
		_flatCache = new Dictionary<string, LocalizedValue>(StringComparer.OrdinalIgnoreCase);

		foreach (var category in _table) {
			foreach (var kvp in category.Value) {
				// 扁平化格式为 类名.文本名
				string flatKey = $"{category.Key}.{kvp.Key}";
				_flatCache[flatKey] = kvp.Value;
			}
		}
	}

	#endregion
	#region["分类","键名" 获取多语言文本]

	/// <summary>
	/// 获取本地化文本组(分类,键名分开)
	/// </summary>
	public static bool TryGetValueSplit(string category, string key,out LocalizedValue value) {
		// 验证参数
		if (string.IsNullOrEmpty(category)) {
			// 分类为空
			MPMain.LogWarning("[MP Localization] Category is null or empty");
			value = new LocalizedValue($"[{category}.{key}]");
			return false;
		}

		// 查找分类
		if (!_table.TryGetValue(category, out var categoryDict)) {
			// 分类未找到
			MPMain.LogWarning($"[MP Localization] Category not found: {category}");
			value = new LocalizedValue($"[{category}.{key}]");
			return false;
		}

		// 查找键
		if (!categoryDict.TryGetValue(key, out LocalizedValue pattern)) {
			// 子选项未找到
			MPMain.LogWarning($"[MP Localization] Key '{key}' not found in category '{category}'");
			value = new LocalizedValue($"[{category}.{key}]");
			return false;
		}
		value = pattern;
		return true;
	}

	/// <summary>
	/// 获取本地化文本(必须是单行文本)
	/// </summary>
	public static string GetSplit(string category, string key, params object[] args) {
		if (!TryGetValueSplit(category, key, out var val)) return val.AsString;
		return SafeFormat(val.AsString, args);
	}

	/// <summary>
	/// 获取本地化文本(随机获取列表中的一项)
	/// </summary>
	public static string GetRandomSplit(string category, string key, params object[] args) {
		if (!TryGetValueSplit(category, key, out var val)) return val.AsString;
		return SafeFormat(val.GetValue(_staticRandom), args);
	}

	/// <summary>
	/// 获取本地化文本(获取列表中特定的一项)
	/// </summary>
	public static string GetByIndexSplit(string category, string key, int index, params object[] args) {
		if (!TryGetValueSplit(category, key, out var val)) return val.AsString;
		return SafeFormat(val.GetValue(index), args);
	}

	/// <summary>
	/// 获取本地化文本数组数量
	/// </summary>
	public static int GetCountSplit(string category, string key) {
		return TryGetValueSplit(category, key, out var val) ? val.Count : 0;
	}

	/// <summary>
	/// 获取本地化文本的所有元素 (非数组时返回单元素数组)
	/// </summary>
	public static string[] GetAllSplit(string category, string key) {
		if (!TryGetValueSplit(category, key, out var val)) return new string[] { };
		return val.IsArray ? val.AsArray : new[] { val.AsString };
	}

	/// <summary>
	/// 避免代码重复
	/// </summary>
	private static string SafeFormat(string pattern, object[] args) {
		if (args == null || args.Length == 0) return pattern;
		try {
			return string.Format(pattern, args);
		} catch (Exception e) {
			MPMain.LogError($"[Localization] Format error: {e.Message}");
			return pattern;
		}
	}

	#endregion
	#region["分类.键名" 获取多语言文本]

	/// <summary>
	/// 获取本地化文本(分类.键名格式)
	/// </summary>
	public static bool TryGetValue(string key, out LocalizedValue value) {
		// 查找键,未找到
		if (!_flatCache.TryGetValue(key, out LocalizedValue pattern)) {
			value = new LocalizedValue($"[{key}]");
			return false;
		}
		value = pattern;
		return true;
	}

	/// <summary>
	/// 获取本地化文本(必须是单行文本)
	/// </summary>
	public static string Get(string key, params object[] args) {
		if (!TryGetValue(key, out var val)) return val.AsString;
		return SafeFormat(val.AsString, args);
	}

	/// <summary>
	/// 获取本地化文本(随机获取列表中的一项)
	/// </summary>
	public static string GetRandom(string key, params object[] args) {
		if (!TryGetValue(key, out var val)) return val.AsString;
		return SafeFormat(val.GetValue(_staticRandom), args);
	}

	/// <summary>
	/// 获取本地化文本(获取列表中特定的一项)
	/// </summary>
	public static string GetByIndex(string key, int index, params object[] args) {
		if (!TryGetValue(key, out var val)) return val.AsString;
		return SafeFormat(val.GetValue(index), args);
	}

	/// <summary>
	/// 获取本地化文本数组数量
	/// </summary>
	public static int GetCount(string key) {
		return TryGetValue(key, out var val) ? val.Count : 0;
	}

	/// <summary>
	/// 获取本地化文本的所有元素 (非数组时返回单元素数组)
	/// </summary>
	public static string[] GetAll(string key) {
		if (!TryGetValue(key, out var val)) return new string[] { };
		return val.IsArray ? val.AsArray : new[] { val.AsString };
	}
	#endregion
	#region[Debug检查]

	/// <summary>
	/// 检查键是否存在
	/// </summary>
	public static bool HasKey(string key) {
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

	#endregion
	#region[获取系统语言]
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
	#endregion
}