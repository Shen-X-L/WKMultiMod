using BepInEx;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.VisualScripting;
using WKMPMod.Core;

namespace WKMPMod.Data;

// 定义规则枚举, 方便外部通过 API 查询
public enum RuleType { Pvp, Hang, Grab, TagShow, SyncItem, SyncInventory, SyncDied, Collision }

// 队伍规则实体 (使用可空布尔值 bool?, null代表未设置, 需要触发回退) 
public class TeamRule {
	public static readonly List<string> ruleFieldNames = new List<string>(){
		"pvp", "hang", "grab", "tagshow", "syncitem", "syncinventory", "syncdied", "collision"};
	public static readonly HashSet<string> ruleFieldLookup = new HashSet<string>(ruleFieldNames);

	public bool? pvp;
	public bool? hang;
	public bool? grab;
	public bool? tagShow;
	public bool? syncItem;
	public bool? syncInventory;
	public bool? syncDied;
	public bool? collision;

	// 对象拷贝方法: 用于在修改前克隆一个旧规则, 实现增量更新
	public TeamRule Clone() {
		return new TeamRule {
			pvp = this.pvp,
			hang = this.hang,
			grab = this.grab,
			tagShow = this.tagShow,
			syncItem = this.syncItem,
			syncInventory = this.syncInventory,
			syncDied = this.syncDied,
			collision = this.collision
		};
	}

	// 动态更新规则方法: 将外部传入的 string 转化为内部状态
	public void UpdateRule(string ruleName, string valStr) {
		// 转换为 bool?
		bool? val = null;
		if (valStr == "true" || valStr == "1") val = true;
		else if (valStr == "false" || valStr == "0") val = false;
		else if (valStr == "default" || valStr == "null") val = null;

		// 映射并赋值
		switch (ruleName) {
			case "pvp": pvp = val; break;
			case "hang": hang = val; break;
			case "grab": grab = val; break;
			case "tagshow": tagShow = val; break;
			case "syncitem": syncItem = val; break;
			case "syncinventory": syncInventory = val; break;
			case "syncdied": syncDied = val; break;
			case "collision": collision = val; break;
		}
	}

	// 解析形如 "pvp:1;grab:0;hang:1" 的字符串
	public static TeamRule Parse(string data) {
		var rule = new TeamRule();
		if (string.IsNullOrEmpty(data)) return rule;

		string[] parts = data.Split(';');
		foreach (var part in parts) {
			string[] kv = part.Split(':');
			if (kv.Length == 2 && int.TryParse(kv[1], out int val)) {
				bool isOn = val == 1; // 1开启, 0关闭
				switch (kv[0].ToLower()) {
					case "pvp": rule.pvp = isOn; break;
					case "hang": rule.hang = isOn; break;
					case "grab": rule.grab = isOn; break;
					case "tagshow": rule.tagShow = isOn; break;
					case "syncitem": rule.syncItem = isOn; break;
					case "syncinventory": rule.syncInventory = isOn; break;
					case "syncdied": rule.syncDied = isOn; break;
					case "collision": rule.collision = isOn; break;
				}
			}
		}
		return rule;
	}

	/// <summary>
	/// 将 TeamRule 对象逆向打包为网络传输的压缩字符串
	/// </summary>
	public string SerializeTeamRule() {
		List<string> parts = new List<string>();
		if (pvp.HasValue) parts.Add($"pvp:{(pvp.Value ? "1" : "0")}");
		if (hang.HasValue) parts.Add($"hang:{(hang.Value ? "1" : "0")}");
		if (grab.HasValue) parts.Add($"grab:{(grab.Value ? "1" : "0")}");
		if (tagShow.HasValue) parts.Add($"tagshow:{(tagShow.Value ? "1" : "0")}");
		if (syncItem.HasValue) parts.Add($"syncitem:{(syncItem.Value ? "1" : "0")}");
		if (syncInventory.HasValue) parts.Add($"syncinventory:{(syncInventory.Value ? "1" : "0")}");
		if (syncDied.HasValue) parts.Add($"syncdied:{(syncDied.Value ? "1" : "0")}");
		if (collision.HasValue) parts.Add($"collision:{(collision.Value ? "1" : "0")}");
		return string.Join(";", parts);
	}

	// 辅助函数: 根据枚举获取字段值
	public bool? GetFieldValue(RuleType type) {
		return type switch {
			RuleType.Pvp => pvp,
			RuleType.Hang => hang,
			RuleType.Grab => grab,
			RuleType.TagShow => tagShow,
			RuleType.SyncItem => syncItem,
			RuleType.SyncInventory => syncInventory,
			RuleType.SyncDied => syncDied,
			RuleType.Collision => collision,
			_ => null
		};
	}

	// 辅助函数: 根据枚举获取字段值
	public void SetFieldValue(RuleType type, bool? value) {
		switch (type) {
			case RuleType.Pvp: pvp = value; break;
			case RuleType.Hang: hang = value; break;
			case RuleType.Grab: grab = value; break;
			case RuleType.TagShow: tagShow = value; break;
			case RuleType.SyncItem: syncItem = value; break;
			case RuleType.SyncInventory: syncInventory = value; break;
			case RuleType.SyncDied: syncDied = value; break;
			case RuleType.Collision: collision = value; break;
		};
	}
}

/// <summary>
/// 全是明确的 bool, 没有 null, 供组件每帧高频无开销读取
/// 内存压缩版规则结构体, 仅占用 8 位 1 Byte
/// </summary>
public readonly struct FlattenedRule {
	// 内部仅使用 1 个字节存储 8 个 bool 状态
	private readonly byte _data;

	// 定义各个规则对应的 Bit 位掩码 (Bitmask)
	private const byte MASK_PVP = 1 << 0; // 0000 0001 (0x01)
	private const byte MASK_HANG = 1 << 1; // 0000 0010 (0x02)
	private const byte MASK_GRAB = 1 << 2; // 0000 0100 (0x04)
	private const byte MASK_TAG_SHOW = 1 << 3; // 0000 1000 (0x08)
	private const byte MASK_SYNC_ITEM = 1 << 4; // 0001 0000 (0x10)
	private const byte MASK_SYNC_INVENTORY = 1 << 5; // 0010 0000 (0x20)
	private const byte MASK_SYNC_DIED = 1 << 6; // 0100 0000 (0x40)
	private const byte MASK_COLLISION = 1 << 7; // 1000 0000 (0x80)

	#region [属性暴露 - 保持对外 API 兼容]

	public bool pvp => (_data & MASK_PVP) != 0;
	public bool hang => (_data & MASK_HANG) != 0;
	public bool grab => (_data & MASK_GRAB) != 0;
	public bool tagShow => (_data & MASK_TAG_SHOW) != 0;
	public bool syncItem => (_data & MASK_SYNC_ITEM) != 0;
	public bool syncInventory => (_data & MASK_SYNC_INVENTORY) != 0;
	public bool syncDied => (_data & MASK_SYNC_DIED) != 0;
	public bool collision => (_data & MASK_COLLISION) != 0;

	#endregion

	#region [构造函数]

	// 基础构造函数：直接通过 raw byte 构建
	public FlattenedRule(byte rawData) {
		_data = rawData;
	}

	// 全参构造函数：通过 8 个 bool 拼装位图
	public FlattenedRule(
		bool pvp, bool hang, bool grab, bool tagShow,
		bool syncItem, bool syncInventory, bool syncDied, bool collision) {

		byte data = 0;
		if (pvp) data |= MASK_PVP;
		if (hang) data |= MASK_HANG;
		if (grab) data |= MASK_GRAB;
		if (tagShow) data |= MASK_TAG_SHOW;
		if (syncItem) data |= MASK_SYNC_ITEM;
		if (syncInventory) data |= MASK_SYNC_INVENTORY;
		if (syncDied) data |= MASK_SYNC_DIED;
		if (collision) data |= MASK_COLLISION;

		_data = data;
	}

	#endregion

	#region [核心 API: 获取与生成新值的修改]

	/// <summary>
	/// 根据 RuleType 查询对应的布尔状态
	/// </summary>
	public bool GetFieldValue(RuleType type) {
		byte mask = GetMaskByRuleType(type);
		return (_data & mask) != 0;
	}

	/// <summary>
	/// 由于 readonly struct 是不可变的, SetFieldValue 返回修改后的新结构体实例
	/// </summary>
	public FlattenedRule SetFieldValue(RuleType type, bool value) {
		byte mask = GetMaskByRuleType(type);
		byte newData = value
			? (byte)(_data | mask)   // 将指定位置 1
			: (byte)(_data & ~mask); // 将指定位置 0

		return new FlattenedRule(newData);
	}

	// 辅助映射函数：将枚举转为对应的 Bitmask
	private static byte GetMaskByRuleType(RuleType type) {
		return type switch {
			RuleType.Pvp => MASK_PVP,
			RuleType.Hang => MASK_HANG,
			RuleType.Grab => MASK_GRAB,
			RuleType.TagShow => MASK_TAG_SHOW,
			RuleType.SyncItem => MASK_SYNC_ITEM,
			RuleType.SyncInventory => MASK_SYNC_INVENTORY,
			RuleType.SyncDied => MASK_SYNC_DIED,
			RuleType.Collision => MASK_COLLISION,
			_ => 0
		};
	}

	#endregion
}

public static class TeamRuleManager {
	// 所有可能的队伍组合 的 规则缓存字典, Key为 "Rule_A_B"
	private static Dictionary<string, TeamRule> _rulesCache = new();
	// 当前队伍 对 其他队伍 规则的直接缓存字典, Key为 TeamName
	private static Dictionary<string, FlattenedRule> _flatRulesByTarget = new();

	// 活跃队伍列表
	public static HashSet<string> activeTeams = new();

	public static string GetRuleKey(string attackerTeam, string targetTeam) => $"Rule_{attackerTeam}_{targetTeam}";

	public static IReadOnlyDictionary<string, TeamRule> GetAllRules() => _rulesCache;

	// 接收大厅数据更新缓存
	public static void UpdateRuleCache(string key, string data) {
		_rulesCache[key] = TeamRule.Parse(data);
	}

	// 清理缓存 (断开连接时调用) 
	public static void ClearCache() {
		_rulesCache.Clear();
		_flatRulesByTarget.Clear();
	}

	// 保底安全对象 (使用构造函数进行初始化)
	private static readonly FlattenedRule _defaultSafeRule = new FlattenedRule(
		pvp: false,
		hang: true,
		grab: false,
		tagShow: true,
		syncItem: true,
		syncInventory: false,
		syncDied: false,
		collision: false
	);

	/// <summary>
	/// 查询 A 对 B 的某项规则 (支持三级回退)
	/// </summary>
	public static bool GetRule(string teamA, string teamB, RuleType type) {
		// 第一级: 查询专属规则 Rule_{teamA}_{teamB}
		if (_rulesCache.TryGetValue(GetRuleKey(teamA, teamB), out var specificRule)) {
			bool? val = specificRule.GetFieldValue(type);
			if (val.HasValue) return val.Value;
		}

		// 第二级: 查询单边默认规则 Rule_{teamA}_{DEFAULT_TEAM}
		if (_rulesCache.TryGetValue(GetRuleKey(teamA, MPKeys.DEFAULT_TEAM), out var teamDefaultRule)) {
			bool? val = teamDefaultRule.GetFieldValue(type);
			if (val.HasValue) return val.Value;
		}

		// 第三级: 查询全局默认规则 Rule_{DEFAULT_TEAM}_{DEFAULT_TEAM}
		if (_rulesCache.TryGetValue(GetRuleKey(MPKeys.DEFAULT_TEAM, MPKeys.DEFAULT_TEAM), out var globalRule)) {
			bool? val = globalRule.GetFieldValue(type);
			if (val.HasValue) return val.Value;
		}

		// 第四级: 如果连全局规则都没配置, 返回程序硬编码的安全默认值
		return _defaultSafeRule.GetFieldValue(type);
	}

	/// <summary>
	/// 修改 A 对 B 的某项规则 (返回修改后的序列化字符串, 供发送给 SteamLobby)
	/// </summary>
	public static string SetRule(string teamA, string teamB, RuleType type, bool? safeDefault) {
		if (!activeTeams.Contains(teamA) || !activeTeams.Contains(teamB)) return null;
		string key = GetRuleKey(teamA, teamB);
		if (!_rulesCache.TryGetValue(key, out var rule)) return null;
		rule.SetFieldValue(type, safeDefault);
		return rule.SerializeTeamRule();
	}

	/// <summary>
	/// 更新活跃队伍列表
	/// </summary>
	public static void UpdateActiveTeams(IEnumerable<string> teams) {
		activeTeams.Clear();
		activeTeams.Add(MPKeys.DEFAULT_TEAM.ToLower());
		foreach (var team in teams)
			if (!string.IsNullOrEmpty(team)) activeTeams.Add(team);
	}

	/// <summary>
	/// 根据当前队伍,更新与其他队伍间规则缓存 (单向逻辑拍平)
	/// </summary>
	/// <param name="currentTeam">当前队伍</param>
	public static void UpdateActiveRules(string currentTeam) {
		currentTeam = currentTeam?.ToLower() ?? MPKeys.DEFAULT_TEAM.ToLower(); 

    foreach (var targetTeam in activeTeams) {
			
        string targetLower = targetTeam.ToLower(); 

        // 一次性从源规则中读取并构建全新的 FlattenedRule 实例
        var newFlatRule = new FlattenedRule(
			pvp: GetRule(currentTeam, targetLower, RuleType.Pvp), 
            hang: GetRule(currentTeam, targetLower, RuleType.Hang),
            grab: GetRule(currentTeam, targetLower, RuleType.Grab),
            tagShow: GetRule(currentTeam, targetLower, RuleType.TagShow),
            syncItem: GetRule(currentTeam, targetLower, RuleType.SyncItem),
            syncInventory: GetRule(currentTeam, targetLower, RuleType.SyncInventory),
            syncDied: GetRule(currentTeam, targetLower, RuleType.SyncDied),
            collision: GetRule(currentTeam, targetLower, RuleType.Collision)
        );

			// 直接覆盖字典中的值
			_flatRulesByTarget[targetLower] = newFlatRule;
		}

		MPEventBusGame.NotifyRulesUpdated();
	}

	/// <summary>
	/// 获取对 目标队伍 的规则引用
	/// </summary>
	public static FlattenedRule GetActiveRuleRef(string targetTeam) {
		if (_flatRulesByTarget.TryGetValue(targetTeam.ToLower(), out var rule)) return rule;
		return _defaultSafeRule; // 保底安全对象
	}

	/// <summary>
	/// 获取对 目标队伍 具体规则启用情况
	/// </summary>
	/// <param name="targetTeam"></param>
	/// <param name="type"></param>
	/// <returns></returns>
	public static bool GetActiveRule(string targetTeam, RuleType type) {
		if (_flatRulesByTarget.TryGetValue(targetTeam.ToLower(), out var rule)) 
			return rule.GetFieldValue(type);
		return _defaultSafeRule.GetFieldValue(type);
	}

	// 添加活跃队伍
	public static void AddActiveTeam(string team) {
		if (!string.IsNullOrEmpty(team)) activeTeams.Add(team);
	}

	// 添加多个活跃队伍
	public static void AddActiveTeams(IEnumerable<string> team) {
		activeTeams.AddRange(team);
	}

	// 删除特定活跃队伍和规则
	public static void RemoveActiveTeam(string team) {
		if (string.IsNullOrEmpty(team)) return;

		activeTeams?.Remove(team);

		// 删除相关规则
		var keysToRemove = new List<string>();
		foreach (var key in _rulesCache.Keys) 
			if (key.Contains($"_{team}_") || key.EndsWith($"_{team}")) keysToRemove.Add(key);

		foreach (var key in keysToRemove) _rulesCache.Remove(key);
	}

	// 获取符合条件的活跃队伍列表
	public static IEnumerable<string> GetTeamsMatchingRule(RuleType type,bool value = false) { 
		var result = new List<string>();
		foreach (var (team, rule) in _flatRulesByTarget)
			if (rule.GetFieldValue(type) == value) result.Add(team);
		
		return result;
	}
}

// 对应 JSON 结构的类
public class ServerRuleConfig {
	public Dictionary<string, bool> GlobalDefault { get; set; } = new();
	public List<SpecificRuleConfig> SpecificRules { get; set; } = new();
}

public class SpecificRuleConfig {
	public string attackerTeam { get; set; }
	public string victimTeam { get; set; }
	public Dictionary<string, bool> rules { get; set; } = new();
}

/// <summary>
/// 本地规则配置文件加载器, 负责从 JSON 文件读取规则并转换为 SteamLobby 需要的键值对字典
/// </summary>
public static class RuleConfigLoader {
	private static string configPath => Path.Combine(Paths.ConfigPath, MPKeys.TEAM_RULES_FILE);

	/// <summary>
	/// 从本地 JSON 读取规则, 并将其转换为 SteamLobby 需要的键值对字典
	/// (此方法极其纯粹, 不涉及任何网络和内存修改操作)
	/// </summary>
	public static Dictionary<string, string> LoadRulesAsLobbyData() {
		var lobbyDataPairs = new Dictionary<string, string>();

		// 如果文件不存在, 生成模板并写入
		if (!File.Exists(configPath)) {
			var template = new ServerRuleConfig();
			// 假设 MPConfig.AllowPVP 存在
			template.GlobalDefault.Add("pvp", false);
			template.GlobalDefault.Add("hang", true);
			template.GlobalDefault.Add("grab", true);
			template.GlobalDefault.Add("tagshow", true);
			template.SpecificRules.Add(new SpecificRuleConfig {
				attackerTeam = "hunter",
				victimTeam = "runner",
				rules = new Dictionary<string, bool> { { "pvp", true }, { "grab", false }, { "hang", false } }
			});
			template.SpecificRules.Add(new SpecificRuleConfig {
				attackerTeam = "hunter",
				victimTeam = MPKeys.DEFAULT_TEAM,
				rules = new Dictionary<string, bool> { { "pvp", true } }
			});
			template.SpecificRules.Add(new SpecificRuleConfig {
				attackerTeam = "hunter",
				victimTeam = "hider",
				rules = new Dictionary<string, bool> { { "pvp", true }, { "tagshow", false } }
			});
			File.WriteAllText(configPath, JsonConvert.SerializeObject(template, Formatting.Indented));
		}

		// 读取并反序列化 JSON
		string jsonStr = File.ReadAllText(configPath);
		var config = JsonConvert.DeserializeObject<ServerRuleConfig>(jsonStr);
		if (config == null) return lobbyDataPairs;

		// 处理全局默认规则
		string globalKey = TeamRuleManager.GetRuleKey(MPKeys.DEFAULT_TEAM, MPKeys.DEFAULT_TEAM);
		lobbyDataPairs[globalKey] = ConvertDictToString(config.GlobalDefault);

		// 处理特定队伍规则
		foreach (var spec in config.SpecificRules) {
			string key = TeamRuleManager.GetRuleKey(spec.attackerTeam, spec.victimTeam);
			lobbyDataPairs[key] = ConvertDictToString(spec.rules);
		}

		return lobbyDataPairs;
	}

	/// <summary>
	/// 将当前内存中的队伍规则 (TeamRuleManager) 保存回 JSON 文件
	/// (适用于房主在游戏内使用指令修改规则后, 将其持久化保存)
	/// </summary>
	public static void SaveCurrentRulesToFile() {
		var config = new ServerRuleConfig();
		var allRules = TeamRuleManager.GetAllRules();

		foreach (var kvp in allRules) {
			string key = kvp.Key;          // 格式如 "Rule_hunter_runner"
			TeamRule rule = kvp.Value;

			// 将 TeamRule (bool?) 转换为 Dictionary<string, bool>, 去除 null 值
			var ruleDict = ConvertTeamRuleToDict(rule);
			if (ruleDict.Count == 0) continue; // 如果是空的, 不保存

			// 解析 Key 获取队伍名字 ("Rule_A_B" -> A 和 B)
			string[] parts = key.Split('_');
			if (parts.Length != 3) continue;

			string teamA = parts[1];
			string teamB = parts[2];

			// 区分是全局规则还是特定规则
			if (teamA == MPKeys.DEFAULT_TEAM && teamB == MPKeys.DEFAULT_TEAM) {
				config.GlobalDefault = ruleDict;
			} else {
				config.SpecificRules.Add(new SpecificRuleConfig {
					attackerTeam = teamA,
					victimTeam = teamB,
					rules = ruleDict
				});
			}
		}

		// 写入文件
		File.WriteAllText(configPath, JsonConvert.SerializeObject(config, Formatting.Indented));
	}

	#region [内部数据转换辅助函数]

	// 辅助函数: 把 Dictionary<"pvp", true> 变成 "pvp:1"
	private static string ConvertDictToString(Dictionary<string, bool> rulesDict) {
		if (rulesDict == null) return "";
		List<string> parts = new List<string>();
		foreach (var kvp in rulesDict) {
			parts.Add($"{kvp.Key.ToLower()}:{(kvp.Value ? "1" : "0")}");
		}
		return string.Join(";", parts);
	}

	// 辅助函数: 把 TeamRule (带 null) 提取为非 null 的 Dictionary<string, bool>
	private static Dictionary<string, bool> ConvertTeamRuleToDict(TeamRule rule) {
		var dict = new Dictionary<string, bool>();
		if (rule.pvp.HasValue) dict["pvp"] = rule.pvp.Value;
		if (rule.hang.HasValue) dict["hang"] = rule.hang.Value;
		if (rule.grab.HasValue) dict["grab"] = rule.grab.Value;
		if (rule.tagShow.HasValue) dict["tagshow"] = rule.tagShow.Value;
		if (rule.syncItem.HasValue) dict["syncitem"] = rule.syncItem.Value;
		if (rule.syncInventory.HasValue) dict["syncinventory"] = rule.syncInventory.Value;
		if (rule.syncDied.HasValue) dict["syncdied"] = rule.syncDied.Value;
		if (rule.collision.HasValue) dict["collision"] = rule.collision.Value;
		return dict;
	}

	#endregion
}