using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
		string pluginDirectory = MPMain.path;
		_table = new Dictionary<string, Dictionary<string, LocalizedValue>>();

		// 强制优先加载英文作为基础
		string enFileName = $"{FILE_PREFIX}_en.json";
		string enFilePath = Path.Combine(pluginDirectory, enFileName);

		bool enLoaded = false;
		if (File.Exists(enFilePath)) {
			LoadAndMerge(enFilePath);
			enLoaded = true;
		} else {
			MPMain.LogWarning($"[Localization] Base English file not found at: {enFilePath}");
		}

		// 获取并尝试加载当前系统语言
		string language = GetGameLanguage();
		// 如果是英文就不重复加载了
		if (language != "en") {
			string localFileName = $"{FILE_PREFIX}_{language.ToLower()}.json";
			string localFilePath = Path.Combine(pluginDirectory, localFileName);

			if (File.Exists(localFilePath)) {
				LoadAndMerge(localFilePath);
				MPMain.LogInfo($"[Localization] Loaded local language file: {localFileName} and merged over English.");
			} else {
				MPMain.LogInfo($"[Localization] Local language file {localFileName} not found. Using English fallback.");
			}
		}

		// 熔断检查
		if (_table.Count == 0 && !enLoaded) {
			MPMain.LogError($"[Localization] CRITICAL: No localization files could be loaded!");
			return;
		}

		// 构建扁平化缓存
		BuildFlatCache();

		int totalEntries = 0;
		foreach (var category in _table) {
			totalEntries += category.Value.Count;
		}
		MPMain.LogInfo($"[Localization] Successfully loaded {_table.Count} categories with {totalEntries} entries");
	}

	/// <summary>
	/// 读取 JSON 并合并到当前字典中 (同名键覆盖, 异名键新增)
	/// </summary>
	private static void LoadAndMerge(string filePath) {
		try {
			string jsonContent = File.ReadAllText(filePath);
			var rawTable = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, object>>>(jsonContent);

			if (rawTable == null) return;

			foreach (var category in rawTable) {
				// 如果大字典里没有这个分类, 先创建一个新的
				if (!_table.ContainsKey(category.Key)) {
					_table[category.Key] = new Dictionary<string, LocalizedValue>();
				}

				// 遍历键值对, 执行插入或覆盖
				foreach (var kvp in category.Value) {
					if (kvp.Value is JArray jarr) {
						_table[category.Key][kvp.Key] = new LocalizedValue(jarr.Select(x => x.ToString()).ToArray());
					} else {
						_table[category.Key][kvp.Key] = new LocalizedValue(kvp.Value?.ToString());
					}
				}
			}
		} catch (Exception e) {
			MPMain.LogError($"[Localization] Unable to parse localization file {Path.GetFileName(filePath)}: {e.Message}");
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
	/// 高级安全格式化 自动抛弃多余参数, 自动为缺失参数补空字符串
	/// </summary>
	public static string SafeFormat(string pattern, params object[] args) {
		if (string.IsNullOrEmpty(pattern)) return string.Empty;
		if (args == null || args.Length == 0) return pattern;

		try {
			// 用正则找出文本里最大的占位符索引 (例如文本里有 {2}, 说明最大索引是 2)
			var matches = Regex.Matches(pattern, @"\{([0-9]+)\}");
			int maxIndex = -1;
			foreach (Match match in matches) {
				if (int.TryParse(match.Groups[1].Value, out int index) && index > maxIndex) {
					maxIndex = index;
				}
			}

			// 如果根本没有数字占位符, 直接返回原文本
			if (maxIndex == -1) return pattern;

			// 计算实际需要的参数数量 (最大索引 + 1)
			int requiredArgsCount = maxIndex + 1;

			// 构建对齐的参数数组
			object[] paddedArgs = new object[requiredArgsCount];
			for (int i = 0; i < requiredArgsCount; i++) {
				// 有传入则用传入的, 不够的话强制补 ""
				paddedArgs[i] = (i < args.Length && args[i] != null) ? args[i] : "";
			}

			// 执行原生 Format
			return string.Format(pattern, paddedArgs);
		} catch (Exception e) {
			MPMain.LogError($"[Localization] Format error: {e.Message} | Pattern: {pattern}");
			return pattern; // 最差的情况降级返回原文本, 不让游戏崩溃
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