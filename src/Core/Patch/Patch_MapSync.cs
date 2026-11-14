using HarmonyLib;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.NetWork;
using WKMPMod.Util;
using static WKMPMod.UI.UI_Manager;
using static WorldLoader;

namespace WKMPMod.Patch;

/// <summary>
/// 地图同步短期修复 (重构为"主机下发完整地图"之前):
/// 1. 生成输入统一: 主机生成关卡列表时记录查询过的存档/会话标记 (成就、hasvisited 等) 与生成时间, 写入大厅数据;
///    客机生成关卡列表时按主机的标记回答, 日期检查 (SSM_DateTimeRange, 如寄生虫子区域的万圣节期间) 使用主机的时间,
///    并固定用英文规则解析日期 (原版按系统语言解析, 德/俄/法语系统会解析成错误日期).
/// 2. 路线比对: 主机发布主路线关卡列表, 客机生成后比对, 不一致时记录并提示从第几关开始不同.
/// 只在主机也有本修复的房间生效 (主机发布了 "map sync" 大厅数据), 其他房间保持原版行为.
/// </summary>
public static class MapSync {
	[Serializable]
	public class HostMapInfo {
		public int seed;
		public string time = "";                        // 主机生成时的本地时间 (yyyy-MM-ddTHH:mm:ss)
		public Dictionary<string, bool> flags = new();  // 主机生成关卡列表时查询过的标记及其值
		public List<string> route = new();             // 主机主路线 (根分支) 关卡名
	}

	private const string TIME_FORMAT = "yyyy-MM-ddTHH:mm:ss";
	private static readonly CultureInfo DateCulture = CultureInfo.GetCultureInfo("en-US");

	// 主机: 本次生成的时间与查询过的标记
	private static DateTime? _genTime;
	private static readonly Dictionary<string, bool> _queriedFlags = new();
	// 正在执行 GetGenerationList 的层数 (只在生成关卡列表期间替换标记)
	private static int _listDepth;
	public static bool InGenerationList => _listDepth > 0;
	public static void EnterGenerationList() => _listDepth++;
	public static void ExitGenerationList() => _listDepth = Math.Max(0, _listDepth - 1);
	
	// 客机: 解析后的主机输入 (按原始字符串缓存)
	private static string? _hostMapJson;
	private static HostMapInfo? _hostMap;
	// 是否是客机 (TODO: 迁移到MPSteamworks)
	private static bool IsClient => MPCore.IsInLobby && !MPSteamworks.IsHost;
	
	private static int CurrentSeed => WorldLoader.instance != null ? WorldLoader.instance.seed : 0;

	/// <summary>
	/// 客机: 当前大厅中主机发布的输入 (种子与本机一致才使用, 避免主机重开时用到上一局的数据)
	/// </summary>
	public static HostMapInfo? HostMap {
		get {
			if (!IsClient) return null;
			string? raw = MPSteamworks.Instance?.LobbyData?.GetValueOrDefault(MPKeys.MAP_SYNC);
			if (string.IsNullOrEmpty(raw)) return null;
			if (raw != _hostMapJson) {
				_hostMapJson = raw;
				try {
					_hostMap = JsonConvert.DeserializeObject<HostMapInfo>(raw!);
				} catch (Exception ex) {
					_hostMap = null;
					MPMain.LogWarning(Localization.GetLog("MPMapSync.HostInputsParseFailed", ex.Message));
				}
			}
			return _hostMap != null && _hostMap.seed == CurrentSeed ? _hostMap : null;
		}
	}

	/// <summary>
	/// 本房间是否启用这些修复: 自己是主机, 或主机发布过地图输入 (主机也有本修复).
	/// 加入未安装本修复的房间时全部保持原版行为.
	/// </summary>
	public static bool Enabled {
		get {
			if (!MPCore.IsInLobby) return false;
			if (MPSteamworks.IsHost) return true;
			return !string.IsNullOrEmpty(MPSteamworks.Instance?.LobbyData?.GetValueOrDefault(MPKeys.MAP_SYNC));
		}
	}

	#region[生成输入]

	// 本次生成中已记录过的标记差异 / 日期检查 (测试日志, 每次生成只记一次)
	private static readonly HashSet<string> _loggedThisGeneration = new();
	private static bool _bypassFlagOverride;

	/// <summary>
	/// 客机生成关卡列表时按主机标记使用. 返回 true 表示已替换结果
	/// </summary>
	public static bool TryHostFlag(string name, out bool value) {
		value = false;
		if (_bypassFlagOverride || !InGenerationList || IsRunStateFlag(name)) return false;
		if (HostMap?.flags == null || !HostMap.flags.TryGetValue(name, out value)) return false;
		// 测试日志: 主机值与本机不同时记录 (这正是会导致地图分叉的标记)
		if (_loggedThisGeneration.Add("flag:" + name)) {
			_bypassFlagOverride = true;
			bool local;
			try { local = CL_GameManager.HasActiveFlag(name); } finally { _bypassFlagOverride = false; }
			if (local != value) MPMain.LogInfo(Localization.GetLog("MPMapSync.FlagOverridden", name, local, value));
		}
		return true;
	}

	/// <summary>
	/// 测试日志: 每次生成中每个日期范围只记一次
	/// </summary>
	public static void LogDateCheck(string start, string end, DateTime now, float result) {
		if (!_loggedThisGeneration.Add($"date:{start}|{end}")) return;
		MPMain.LogInfo(Localization.GetLog("MPMapSync.DateChecked", start, end, now.ToString(TIME_FORMAT, CultureInfo.InvariantCulture), result));
	}

	/// <summary>
	/// 主机记录生成关卡列表时查询过的标记
	/// </summary>
	public static void RecordFlag(string name, bool value) {
		if (!InGenerationList || IsRunStateFlag(name)) return;
		// 只记第一次查询的值: 生成过程中值可能被改写, 客机需要的是生成开始时的输入
		if (MPCore.IsInLobby && MPSteamworks.IsHost && !_queriedFlags.ContainsKey(name)) _queriedFlags[name] = value;
	}

	/// <summary>
	/// 生成过程本身写入的本局状态标记 (子区域"本局已生成过"), 各端生成时自行演变, 不能按主机的最终值回答
	/// </summary>
	private static bool IsRunStateFlag(string name) =>
		string.IsNullOrEmpty(name) || name.StartsWith("hasgeneratedthisrun-", StringComparison.Ordinal);

	/// <summary>
	/// 日期检查使用的"当前时间": 客机用主机生成时间, 主机用本局生成时间
	/// </summary>
	public static DateTime Now {
		get {
			if (IsClient && HostMap is { } hostMap
				&& DateTime.TryParseExact(host.time, TIME_FORMAT, CultureInfo.InvariantCulture, DateTimeStyles.None, out var hostTime))
				return hostTime;
			return _genTime ?? DateTime.Now;
		}
	}

	/// <summary>
	/// 以英文规则解析日期 ("October 25", "11AM, November 1"), 年份取 now 的年份
	/// </summary>
	public static bool TryParseDate(string text, DateTime now, out DateTime result) {
		result = default;
		if (!DateTime.TryParse(text, DateCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed)) return false;
		result = new DateTime(now.Year, parsed.Month, parsed.Day, parsed.Hour, parsed.Minute, parsed.Second);
		return true;
	}

	#endregion

	#region[生成流程]

	/// <summary>
	/// GenerateLevels 开始时调用
	/// </summary>
	public static void OnGenerateStart(WorldLoader loader, BranchInfo branch, GenerationParameters? genParams) {
		_loggedThisGeneration.Clear();
		if (!Enabled) return;
		// 是否从存档加载
		bool isSaveLoad = genParams?.generationType == GenerationParameters.GenerationType.saveLoad;
		// 这个主线判断原理是什么?
		bool isRoot = branch != null && branch.generationBranch == null;
		// 测试日志: 客机本次生成是否使用主机输入
		if (!MPSteamworks.IsHost) {
			var host = HostMap;
			MPMain.LogInfo(host != null
				? Localization.GetLog("MPMapSync.UsingHostInputs", host.seed, host.flags?.Count ?? 0, host.time, host.route?.Count ?? 0)
				: Localization.GetLog("MPMapSync.HostInputsUnavailable", CurrentSeed));
		}
		// 主机: 全新生成主路线时开始记录本局输入
		bool fresh = isRoot && (genParams == null || genParams.generationType == GenerationParameters.GenerationType.standard)
			&& (branch!.levelTracker == null || branch.levelTracker.Count == 0);
		if (fresh && MPSteamworks.IsHost) {
			_genTime = DateTime.Now;
			_queriedFlags.Clear();
		}
		// 等待关卡生成完毕后执行
		if (isRoot) loader.StartCoroutine(AfterGeneration(loader, branch!, fresh, isSaveLoad));
	}

	private static readonly AccessTools.FieldRef<WorldLoader, bool> LoadingField =
		AccessTools.FieldRefAccess<WorldLoader, bool>("loading");

	private static IEnumerator AfterGeneration(WorldLoader loader, BranchInfo branch, bool fresh, bool saveLoad) {
		// 等待生成流程开始->结束 (loading true->false) 
		yield return new WaitUntil(() => loader == null || LoadingField(loader));
		yield return new WaitUntil(() => loader == null || !LoadingField(loader));
		if (loader == null || !MapSync.Enabled) yield break;
		// 分支内生成过的全部关卡 (含已卸载的), 续接生成后也完整
		var route = branch.GetLevelIds()
			.Select(id => id?.level)
			.Where(name => !string.IsNullOrEmpty(name))
			.Select(name => name!)
			.ToList();
		if (MPSteamworks.IsHost) {
			// 全新生成与续接生成都重新发布路线 (输入只在全新生成时重置); 读档复活不发布
			if (!saveLoad) Publish(route);
		} else {
			CompareWithHost(route, saveLoad);
		}
	}

	/// <summary>
	/// 提交关卡数据到SteamLobbyDAta
	/// </summary>
	private static void Publish(List<string> route) {
		var inputs = new HostMapInfo {
			seed = CurrentSeed,
			time = (_genTime ?? DateTime.Now).ToString(TIME_FORMAT, CultureInfo.InvariantCulture),
			flags = new Dictionary<string, bool>(_queriedFlags),
			route = route,
		};
		MPSteamworks.Instance.SetLobbyData(MPKeys.MAP_SYNC, JsonConvert.SerializeObject(inputs));
		MPMain.LogInfo(Localization.GetLog("MPMapSync.Published", inputs.flags.Count, route.Count));
	}

	/// <summary>
	/// 客机: 与主机主路线比对. 本机路线可能不从第一关开始 (读档复活从存档关卡开始、前面的关卡已卸载),
	/// 按本机首关在主机路线中的位置对齐后比对
	/// 无作用 仅输出错误 DEBUG使用
	/// </summary>
	private static void CompareWithHost(List<string> route, bool saveLoad) {
		var hostMap = HostMap;
		if (host?.route == null || host.route.Count == 0 || route.Count == 0) return;
		// 无当前关卡匹配节点(TODO: 不知道捷径中表现会如何,但是目前捷径同步关闭,需要靠下次地图重构)
		int offset = hostMap.route.IndexOf(route[0]);
		if (offset < 0) {
			// 本机路线从主机已发布路线之外开始 (例如主机还没生成到复活点), 无法比对, 不算不一致
			MPMain.LogInfo(Localization.GetLog("MPMapSync.RouteBeyondHost", route[0], host.route.Count));
			return;
		}
		int count = Math.Min(route.Count, host.route.Count - offset);
		for (int i = 0; i < count; i++) {
			if (route[i] != host.route[offset + i]) {
				Report(offset + i, route[i], host.route[offset + i]);
				return;
			}
		}
		MPMain.LogInfo(Localization.GetLog("MPMapSync.RouteMatch", count));
	}

	private static void Report(int index, string mine, string hosts) {
		MPMain.LogWarning(Localization.GetLog("MPMapSync.RouteMismatch", index, mine, hosts));
		MPCore.SystemMessage(Localization.Get("MPMapSync.RouteMismatchMessage", index), UIDisplayType.TipHeader);
	}

	#endregion
}

// 拦截所有GetGenerationList 调用时添加标志位
[HarmonyPatch]
public static class Patch_MapSync_GenerationList {
	// 所有关卡生成器 (战役模式、捷径分支等) 的 GetGenerationList
	static IEnumerable<MethodBase> TargetMethods() {
		foreach (var type in typeof(WorldGenerator).Assembly.GetTypes()) {
			if (type.IsInterface || !typeof(WorldGenerator).IsAssignableFrom(type)) continue;
			var method = AccessTools.Method(type, nameof(WorldGenerator.GetGenerationList));
			if (method != null && method.DeclaringType == type) yield return method;
		}
	}
	static void Prefix() => MapSync.EnterGenerationList();
	static Exception? Finalizer(Exception? __exception) {
		MapSync.ExitGenerationList();
		return __exception;
	}
}

// 进行关卡生成序列记录入口点
[HarmonyPatch(typeof(WorldLoader), "GenerateLevels")]
public class Patch_MapSync_GenerateLevels {
	static void Prefix(WorldLoader __instance, BranchInfo branch, GenerationParameters genParams) {
		try {
			MapSync.OnGenerateStart(__instance, branch, genParams);
		} catch (Exception ex) {
			MPMain.LogError($"[MP MapSync] {ex}");
		}
	}
}

// 记录生成标记
[HarmonyPatch(typeof(CL_GameManager), nameof(CL_GameManager.HasActiveFlag))]
public static class Patch_MapSync_HasActiveFlag {
	static bool Prefix(string name, ref bool __result) {
		if (!MapSync.TryHostFlag(name, out bool value)) return true;
		__result = value;
		return false;
	}
	static void Postfix(string name, bool __result) => MapSync.RecordFlag(name, __result);
}

// 日期范围检查: 联机时用主机时间, 并按英文规则解析日期
[HarmonyPatch(typeof(SSM_DateTimeRange), nameof(SSM_DateTimeRange.Check))]
public static class Patch_MapSync_DateTimeRange {
	static bool Prefix(SSM_DateTimeRange __instance, ref float __result) {
		if (!MapSync.Enabled) return true;
		var now = MapSync.Now;
		if (!MapSync.TryParseDate(__instance.startTime, now, out var start) || !MapSync.TryParseDate(__instance.endTime, now, out var end))
			return true;
		__result = now >= start && now <= end ? __instance.trueValue : __instance.falseValue;
		if (MapSync.InGenerationList) MapSync.LogDateCheck(__instance.startTime, __instance.endTime, now, __result);
		return false;
	}
}
