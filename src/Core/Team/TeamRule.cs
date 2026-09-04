using System;
using System.Collections.Generic;
using System.Linq;

namespace WKMPMod.Team;

// 队伍规则实体 (使用可空布尔值 bool?, null代表未设置, 需要触发回退) 
public partial class TeamRule {
	// 所有规则类型的缓存数组, 用于遍历和序列化
	public static readonly RuleType[] AllRuleTypes =
		Enum.GetValues(typeof(RuleType)).Cast<RuleType>().Where(t => t != RuleType.None).ToArray();
	// 所有规则名称的缓存列表, 用于遍历和序列化
	private static List<string> _definitionNamesCache;
	public static List<string> DefinitionNames {
		get {
			if (_definitionNamesCache == null)
				_definitionNamesCache = Definitions.Select(d => d.name).ToList();
			return _definitionNamesCache;
		}
	}

	// 私有缓存与脏标记
	private List<(string type, string value)> _formattedRulesCache;
	private bool _isCacheDirty = true;
	/// <summary>
	/// 获取全量规则格式化后的键值对列表, 格式为 (规则名称小写, "true"/"false"/"default")
	/// 未设置的规则会自动补全为 "default"
	/// </summary>
	public IReadOnlyList<(string type, string value)> FormattedRules {
		get {
			if (_isCacheDirty || _formattedRulesCache == null) {
				// 遍历全量规则 AllRuleTypes 确保数量一致
				_formattedRulesCache = AllRuleTypes.Select(type => {
					string keyStr = type.ToString().ToLower();
					bool? val = GetFieldValue(type); // 若 Rules 中不存在则返回 null

					string valStr = val.HasValue ? (val.Value ? "true" : "false") : "default";
					return (keyStr, valStr);
				}).Append((",", "change team")).ToList();

				_isCacheDirty = false;
			}
			return _formattedRulesCache;
		}
	}

	public Dictionary<RuleType, bool?> Rules { get; set; } = new();

	// 辅助函数: 根据枚举获取字段值
	public bool? GetFieldValue(RuleType type)
		=> Rules.TryGetValue(type, out var val) ? val : null;

	// 辅助函数: 根据枚举设置字段值
	public void SetFieldValue(RuleType type, bool? value) {
		if (value.HasValue) Rules[type] = value;
		else Rules.Remove(type);
		_isCacheDirty = true;
	}

	// 直接通过构造函数深拷贝字典
	public TeamRule Clone() => new TeamRule {
		Rules = new Dictionary<RuleType, bool?>(this.Rules)
	};

	// 利用 Enum.TryParse 自动映射, 无需 switch-case
	public void UpdateRule(string ruleName, string valStr) {
		if (!Enum.TryParse<RuleType>(ruleName, true, out var type)) return;

		bool? val = valStr switch {
			"true" or "1" => true,
			"false" or "0" => false,
			_ => null
		};

		SetFieldValue(type, val);
	}

	#region[序列化和反序列化]

	/// <summary>
	/// 使用反射自动进行序列化
	/// 格式为ruleA:1;ruleA:0;
	/// </summary>
	public string SerializeTeamRule() {
		var parts = new List<string>();
		foreach (RuleType type in AllRuleTypes) {
			var val = GetFieldValue(type);
			if (val.HasValue) parts.Add($"{type.ToString().ToLower()}:{(val.Value ? "1" : "0")}");
		}
		return string.Join(";", parts);
	}

	// 使用反射自动进行反序列化
	public static TeamRule Parse(string data) {
		var rule = new TeamRule();
		if (string.IsNullOrEmpty(data)) return rule;

		foreach (var part in data.Split(';')) {
			var kv = part.Split(':');
			// 改为直写
			if (kv.Length == 2 && Enum.TryParse<RuleType>(kv[0], true, out var type)) 
				rule.Rules[type] = (kv[1] == "1");
		}
		rule._isCacheDirty = true;
		return rule;
	}

	#endregion
}

/// <summary>
/// 使用 bool, 没有 null, 供组件每帧高频无开销读取
/// 内存压缩版规则结构体
/// </summary>
public readonly partial struct FlattenedRule {
	// 保底安全对象
	public static readonly FlattenedRule defaultSafeRule = new FlattenedRule(
		(ulong)(RuleType.Hang | RuleType.TagShow | RuleType.SyncDropItem)
	);

	private readonly ulong _data;

	#region [构造函数]

	// 基础构造函数：直接通过 raw byte 构建
	public FlattenedRule(ulong rawData) => _data = rawData;

	#endregion

	#region [获取与生成新值的修改]

	/// <summary>
	/// 根据 RuleType 查询对应的布尔状态
	/// </summary>
	public bool GetFieldValue(RuleType type) => (_data & (ushort)type) != 0;

	/// <summary>
	/// 由于 readonly struct 是不可变的, SetFieldValue 返回修改后的新结构体实例
	/// </summary>
	public FlattenedRule SetFieldValue(RuleType type, bool value) {
		ulong mask = (ulong)type;
		ulong newData = value ? (ulong)(_data | mask) : (ulong)(_data & ~mask);
		return new FlattenedRule(newData);
	}

	#endregion
}