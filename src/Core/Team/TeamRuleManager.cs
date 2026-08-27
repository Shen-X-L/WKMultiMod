using BepInEx;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.VisualScripting;
using WKMPMod.Data;

namespace WKMPMod.Team;

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

	/// <summary>
	/// 根据当前队伍,更新与其他队伍间规则缓存 (单向逻辑拍平)
	/// </summary>
	/// <param name="currentTeam">当前队伍</param>
	public static void UpdateActiveRules(string currentTeam) {
		currentTeam = currentTeam?.ToLower() ?? MPKeys.DEFAULT_TEAM.ToLower();

		foreach (var targetTeam in activeTeams) {

			string targetLower = targetTeam.ToLower();

			ulong maskData = 0;

			// 遍历所有规则，若 GetRule 返回 true，则按位或运算，把对应的位置 1
			foreach (var type in TeamRule.AllRuleTypes) 
				if (GetRule(currentTeam, targetLower, type)) maskData |= (ulong)type; 

			// 直接覆盖字典中的值
			_flatRulesByTarget[targetLower] = new FlattenedRule(maskData);
		}
		MPEventBusGame.NotifyRulesUpdated();
	}

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
		return FlattenedRule.defaultSafeRule.GetFieldValue(type);
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
	/// 获取对 目标队伍 的规则引用
	/// </summary>
	public static FlattenedRule GetActiveRuleRef(string targetTeam) {
		if (_flatRulesByTarget.TryGetValue(targetTeam.ToLower(), out var rule)) return rule;
		return FlattenedRule.defaultSafeRule; // 保底安全对象
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
		return FlattenedRule.defaultSafeRule.GetFieldValue(type);
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
	public static IEnumerable<string> GetTeamsMatchingRule(RuleType type, bool value = false) {
		var result = new List<string>();
		foreach (var (team, rule) in _flatRulesByTarget)
			if (rule.GetFieldValue(type) == value) result.Add(team);

		return result;
	}
}

#region[本地储存]

// 对应 JSON 结构的类
public class ServerRuleConfig {
	// 全局(默认队伍间)规则
	public Dictionary<string, bool> GlobalDefault { get; set; } = new();
	public List<SpecificRuleConfig> SpecificRules { get; set; } = new();
}

/// <summary>
/// attackerTeam队伍 对 victimTeam队伍使用的规则
/// </summary>
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

	#region[序列化和反序列化]

	/// <summary>
	/// 从本地 JSON 读取规则, 并将其转换为 SteamLobby 需要的键值对字典
	/// (此方法极其纯粹, 不涉及任何网络和内存修改操作)
	/// </summary>
	public static Dictionary<string, string> LoadRulesAsLobbyData() {
		var lobbyDataPairs = new Dictionary<string, string>();

		// 如果文件不存在, 生成模板并写入
		if (!File.Exists(configPath)) {
			var template = new ServerRuleConfig {
				GlobalDefault = new Dictionary<string, bool> {
					{ RuleType.Pvp.ToString().ToLower(), false },
					{ RuleType.Hang.ToString().ToLower(), true },
					{ RuleType.Grab.ToString().ToLower(), true },
					{ RuleType.TagShow.ToString().ToLower(), true }
				},
				SpecificRules = new List<SpecificRuleConfig> {
					new SpecificRuleConfig {
						attackerTeam = "hunter",
						victimTeam = "runner",
						rules = new Dictionary<string, bool> {
							{ RuleType.Pvp.ToString().ToLower(), true },
							{ RuleType.Grab.ToString().ToLower(), false },
							{ RuleType.Hang.ToString().ToLower(), false }
						}
					},
					new SpecificRuleConfig {
						attackerTeam = "hunter",
						victimTeam = MPKeys.DEFAULT_TEAM,
						rules = new Dictionary<string, bool> {
							{ RuleType.Pvp.ToString().ToLower(), true }
						}
					}
				}
			};
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

	#endregion

	#region [内部数据转换辅助函数]

	// 辅助函数: 把 Dictionary<"pvp", true> 变成 "pvp:1"
	private static string ConvertDictToString(Dictionary<string, bool> rulesDict) {
		if (rulesDict == null) return "";
		return string.Join(";", rulesDict.Select(kv => $"{kv.Key.ToLower()}:{(kv.Value ? "1" : "0")}"));
	}

	// 辅助函数: 把 TeamRule (带 null) 提取为非 null 的 Dictionary<string, bool>
	private static Dictionary<string, bool> ConvertTeamRuleToDict(TeamRule rule) {
		var dict = new Dictionary<string, bool>();
		foreach (var (type, val) in rule.Rules) 
			if (val.HasValue) dict[type.ToString().ToLower()] = val.Value;
		return dict;
	}

	#endregion
}

#endregion