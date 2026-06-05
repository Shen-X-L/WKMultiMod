using BepInEx;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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

	// 【新增】对象拷贝方法: 用于在修改前克隆一个旧规则, 实现完美增量更新
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
		}
		;
	}
}

// 全是明确的 bool, 没有 null, 供组件每帧高频无开销读取
public class FlattenedRule {
	public bool pvp;
	public bool hang;
	public bool grab;
	public bool tagShow;
	public bool syncItem;
	public bool syncInventory;
	public bool syncDied;
	public bool collision;
}

public static class TeamRuleManager {
	// 缓存字典, Key为 "Rule_A_B"
	private static Dictionary<string, TeamRule> _rulesCache = new();
	// 直接缓存字典, Key为 TeamName
	private static Dictionary<string, FlattenedRule> _flatRulesByTarget = new();

	// 活跃队伍列表
	public static HashSet<string> activeTeams = new HashSet<string>();

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

	/// <summary>
	/// 查询 A 对 B 的某项规则 (支持三级回退)
	/// </summary>
	public static bool GetRule(string teamA, string teamB, RuleType type, bool newValue = false) {
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
		return newValue;
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
	/// 更新活跃队伍列表 (在解析规则时调用)
	/// </summary>
	public static void UpdateActiveTeams(IEnumerable<string> teams) {
		activeTeams.Clear();
		activeTeams.Add(MPKeys.DEFAULT_TEAM.ToLower());
		foreach (var team in teams) {
			if (!string.IsNullOrEmpty(team))
				activeTeams.Add(team);
		}
	}

	/// <summary>
	/// 更新活跃规则缓存 (单向逻辑拍平)
	/// </summary>
	public static void UpdateActiveRules(string currentTeam) {
		currentTeam = currentTeam?.ToLower() ?? MPKeys.DEFAULT_TEAM.ToLower();

		foreach (var targetTeam in activeTeams) {
			string targetLower = targetTeam.ToLower();

			// 如果字典里还没有这个目标队伍的缓存对象, 则创建一个新的
			if (!_flatRulesByTarget.TryGetValue(targetLower, out var flatRule)) {
				flatRule = new FlattenedRule();
				_flatRulesByTarget[targetLower] = flatRule;
			}

			flatRule.pvp = GetRule(currentTeam, targetLower, RuleType.Pvp, newValue: false);
			flatRule.hang = GetRule(currentTeam, targetLower, RuleType.Hang, newValue: true);
			flatRule.grab = GetRule(currentTeam, targetLower, RuleType.Grab, newValue: false);
			flatRule.tagShow = GetRule(currentTeam, targetLower, RuleType.TagShow, newValue: true);
			flatRule.syncItem = GetRule(currentTeam, targetLower, RuleType.SyncItem, newValue: false);
			flatRule.syncInventory = GetRule(currentTeam, targetLower, RuleType.SyncInventory, newValue: false);
			flatRule.syncDied = GetRule(currentTeam, targetLower, RuleType.SyncDied, newValue: false);
			flatRule.collision = GetRule(currentTeam, targetLower, RuleType.Collision, newValue: false);
		}
	}

	/// <summary>
	/// 供 RPContainer 获取内存引用
	/// </summary>
	public static FlattenedRule GetActiveRuleRef(string targetTeam) {
		if (_flatRulesByTarget.TryGetValue(targetTeam.ToLower(), out var rule)) {
			return rule;
		}
		return _defaultSafeRule; // 保底安全对象
	}

	private static readonly FlattenedRule _defaultSafeRule = new FlattenedRule { tagShow = true };

	// 添加活跃队伍
	public static void AddActiveTeam(string team) {
		if (!string.IsNullOrEmpty(team))
			activeTeams.Add(team);
	}

	// 删除特定活跃队伍和规则
	public static void RemoveActiveTeam(string team) {
		if (string.IsNullOrEmpty(team)) return;

		activeTeams?.Remove(team);

		// 删除相关规则
		var keysToRemove = new List<string>();
		foreach (var key in _rulesCache.Keys) {
			if (key.Contains($"_{team}_") || key.EndsWith($"_{team}")) {
				keysToRemove.Add(key);
			}
		}
		foreach (var key in keysToRemove) {
			_rulesCache.Remove(key);
		}
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