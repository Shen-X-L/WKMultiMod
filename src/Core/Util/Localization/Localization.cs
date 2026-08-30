using BepInEx.Bootstrap;
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
	private static Dictionary<string, Dictionary<string, LocalizedValue>> _enTable = new();
	private static Dictionary<string, Dictionary<string, LocalizedValue>> _localTable = new();

	// 扁平化缓存,用于快速查找
	private static Dictionary<string, LocalizedValue> _flatEnCache = new();
	private static Dictionary<string, LocalizedValue> _flatLocalCache = new();

	// 本地化文件前缀
	private const string FILE_PREFIX = "texts";

	// 静态随机实例,用于随机文本选择,避免频繁创建 Random 对象
	private static readonly System.Random _staticRandom = new System.Random();

	// 缓存 Mod 检查结果, 避免频繁跨程序集检索
	private static bool _isFontPluginLoaded = false;

	// 检测 WKLocalizationLoader 是否已安装并成功加载
	public static bool HasFontSupport() {
		// 通过 GUID 精确判定 (推荐, 需确认 WKLocalizationLoader 的 GUID)
		if(Chainloader.PluginInfos.ContainsKey("mimimi-turret.wk-localization-loader")) return true;

		// 若不确定 GUID, 通过 PluginInfos 的 Name 或 ProcessName 包含性匹配
		return Chainloader.PluginInfos.Values.Any(info =>
			info.Metadata.Name.Equals("WKLocalizationLoader", StringComparison.OrdinalIgnoreCase));
	}

	#region[初始化字典]

	/// <summary>
	/// 加载本地化文件
	/// </summary>
	public static void Load() {
		string pluginDirectory = MPMain.path;
		_enTable.Clear();
		_localTable.Clear();

		// 加载基础英文文件
		string enFilePath = Path.Combine(pluginDirectory, $"{FILE_PREFIX}_en.json");
		if (File.Exists(enFilePath)) {
			LoadFileToTable(enFilePath, _enTable);
		} else {
			MPMain.LogWarning($"[Localization] Base English file not found at: {enFilePath}");
		}
		// 加载本地语言文件
		string language = GetGameLanguage();
		if (language != "en") {
			string localFilePath = Path.Combine(pluginDirectory, $"{FILE_PREFIX}_{language.ToLower()}.json");
			if (File.Exists(localFilePath)) {
				LoadFileToTable(localFilePath, _localTable);
				MPMain.LogInfo($"[Localization] Loaded local language file for: {language}");
			}
		}
		// 构建缓存
		_flatEnCache = BuildFlatCache(_enTable);
		_flatLocalCache = BuildFlatCache(_localTable);
		if (_flatEnCache.Count == 0 && _flatLocalCache.Count == 0) {
			MPMain.LogError($"[Localization] CRITICAL: No localization files loaded!");
		}
		// 记录是否有mod加载
		_isFontPluginLoaded = HasFontSupport();
	}

	/// <summary>
	/// 读取 JSON 并缓存
	/// </summary>
	private static void LoadFileToTable(string filePath, Dictionary<string, Dictionary<string, LocalizedValue>> targetTable) {
		try {
			string jsonContent = File.ReadAllText(filePath);
			var rawTable = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, object>>>(jsonContent);
			if (rawTable == null) return;

			foreach (var category in rawTable) {
				// 如果大字典里没有这个分类, 先创建一个新的
				if (!targetTable.ContainsKey(category.Key)) 
					targetTable[category.Key] = new Dictionary<string, LocalizedValue>();
				// 遍历键值对, 执行插入或覆盖
				foreach (var kvp in category.Value) {
					if (kvp.Value is JArray jarr) 
						targetTable[category.Key][kvp.Key] = new LocalizedValue(jarr.Select(x => x.ToString()).ToArray());
					else 
						targetTable[category.Key][kvp.Key] = new LocalizedValue(kvp.Value?.ToString());
					
				}
			}
		} catch (Exception e) {
			MPMain.LogError($"[Localization] Unable to parse localization file {Path.GetFileName(filePath)}: {e.Message}");
		}
	}

	/// <summary>
	/// 构建扁平化缓存 (内部使用)
	/// </summary>
	private static Dictionary<string, LocalizedValue> BuildFlatCache(Dictionary<string, Dictionary<string, LocalizedValue>> table) {
		var cache = new Dictionary<string, LocalizedValue>(StringComparer.OrdinalIgnoreCase);
		foreach (var category in table) 
			foreach (var kvp in category.Value) 
				cache[$"{category.Key}.{kvp.Key}"] = kvp.Value;
		return cache;
	}

	#endregion

	#region[获取多语言文本]

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

	/// <summary>
	/// 强制获取英文文本
	/// </summary>
	public static string GetEnglish(string key, params object[] args) {
		if (_flatEnCache.TryGetValue(key, out var val)) {
			return SafeFormat(val.AsString, args);
		}
		return $"[{key}]";
	}

	/// <summary>
	/// 智能获取文本: 若有字体补全且存在本地化则返回本地化文本, 否则回退至英文
	/// </summary>
	public static bool TryGetValue(string key, out LocalizedValue value) {
		// 优先匹配规则: 如果带有字体补全 Mod, 且本地表有该 Key, 则使用本地化
		if (_flatLocalCache.TryGetValue(key, out value)) return true;
		// 否则回退至英文
		if (_flatEnCache.TryGetValue(key, out value)) return true;
		value = new LocalizedValue($"[{key}]");
		return false;
	}

	/// <summary>
	/// 智能获取文本: 若有字体补全且存在本地化则返回本地化文本, 否则回退至英文
	/// </summary>
	public static bool TryGetValueSmart(string key, out LocalizedValue value) {
		// 优先匹配规则: 如果带有字体补全 Mod, 且本地表有该 Key, 则使用本地化
		if (_isFontPluginLoaded && _flatLocalCache.TryGetValue(key, out value)) return true;
		
		// 否则回退至英文
		if (_flatEnCache.TryGetValue(key, out value)) return true;

		// 两者均无, 返回缺省标记
		value = new LocalizedValue($"[{key}]");
		return false;
	}

	/// <summary>
	/// 获取单行文本
	/// </summary>
	/// <param name="key">格式: category.key</param>
	public static string Get(string key, params object[] args) {
		if (!TryGetValue(key, out var val)) return val.AsString;
		return SafeFormat(val.AsString, args);
	}

	/// <summary>
	/// 获取单行文本
	/// </summary>
	/// <param name="key">格式: category.key</param>
	public static string GetSmart(string key, params object[] args) {
		if (!TryGetValueSmart(key, out var val)) return val.AsString;
		return SafeFormat(val.AsString, args);
	}

	/// <summary>
	/// 获取本地化文本(随机获取列表中的一项)
	/// </summary>
	public static string GetRandom(string key, params object[] args) {
		if (!TryGetValueSmart(key, out var val)) return val.AsString;
		return SafeFormat(val.GetValue(_staticRandom), args);
	}

	/// <summary>
	/// 获取本地化文本(获取列表中特定的一项)
	/// </summary>
	public static string GetByIndex(string key, int index, params object[] args) {
		if (!TryGetValueSmart(key, out var val)) return val.AsString;
		return SafeFormat(val.GetValue(index), args);
	}

	/// <summary>
	/// 获取本地化文本数组数量
	/// </summary>
	public static int GetCount(string key) {
		return TryGetValueSmart(key, out var val) ? val.Count : 0;
	}

	/// <summary>
	/// 获取本地化文本的所有元素 (非数组时返回单元素数组)
	/// </summary>
	public static string[] GetAll(string key) {
		if (!TryGetValueSmart(key, out var val)) return new string[] { };
		return val.IsArray ? val.AsArray : new[] { val.AsString };
	}

	#endregion

	#region[Debug检查]

	/// <summary>
	/// 检查键是否存在
	/// </summary>
	public static bool HasLocalKey(string key) {
		return _flatLocalCache.ContainsKey(key);
	}

	/// <summary>
	/// 检查分类和键是否存在
	/// </summary>
	public static bool HasLocalKey(string category, string key) {
		if (_localTable.TryGetValue(category, out var categoryDict)) {
			return categoryDict.ContainsKey(key);
		}
		return false;
	}

	/// <summary>
	/// 获取所有分类
	/// </summary>
	public static IEnumerable<string> GetAllLocalCategories() {
		return _localTable.Keys;
	}

	/// <summary>
	/// 获取指定分类的所有键
	/// </summary>
	public static IEnumerable<string> GetKeysInLocalCategory(string category) {
		if (_localTable.TryGetValue(category, out var categoryDict)) {
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