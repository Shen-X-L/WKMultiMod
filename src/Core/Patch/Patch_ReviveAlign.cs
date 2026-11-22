using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using WKMPMod.Core;
using WKMPMod.Data;
using static WorldLoader;

namespace WKMPMod.Patch;

/// <summary>
/// 读档复活保持世界坐标 (战役):
/// 读档 (saveLoad) 时游戏卸载全部关卡, 只从存档关卡开始重新生成, 存档关卡被放到世界原点且朝向归零,
/// 复活玩家的整条关卡链与其他玩家错开 互相看不见.
/// 处理: 读档前记下存档关卡复活前的世界位姿; 重新生成时, 原版放置存档关卡的同一帧 (实例化关卡并拼接之后 抓点登记 LoadHandholds 之前)
/// 把它放回这个位姿. 后续关卡由原版按出口依次衔接, 生成出来就在原坐标上;
/// 不移动任何已激活的关卡, 也不需要刷新碰撞体 (同一帧内的移动在下一次物理步统一同步).
/// 存档里不记录关卡的世界坐标, 所以位姿来自内存: 存档关卡仍加载时直接读取, 已卸载时使用生成完成时的缓存.
/// 退出游戏后重新读档时内存中没有记录, 保持原版行为 (放到原点).
/// </summary>
public static class ReviveAlign {
	#region[静态反射工具]

	private static readonly AccessTools.FieldRef<WorldLoader, List<BranchInfo>> BranchesField =
		AccessTools.FieldRefAccess<WorldLoader, List<BranchInfo>>("branches");
	private static readonly AccessTools.FieldRef<WorldLoader, bool> LoadingField =
		AccessTools.FieldRefAccess<WorldLoader, bool>("loading");

	#endregion

	// 关卡名 → 最近一次生成完成时的世界位姿 (存档关卡已卸载时的后备来源)
	private static readonly Dictionary<string, (Vector3 pos, Quaternion rot)> _poseCache = new();
	private static WorldLoader? _cacheOwner;

	// 待放回的存档关卡及其复活前位姿
	private static (string levelName, Vector3 pos, Quaternion rot)? _pending;
	// 正在读档重新生成 (只在此期间放回, 不影响正常续接生成)
	private static bool _restoring;

	public static bool Active => MPCore.IsInLobby && MPGameModeManager.IsCampaignMode;

	private static bool _restore;

	/// <summary>
	/// 静态构造函数: 订阅生成事件
	/// </summary>
	static ReviveAlign() {
		EventBusGame.OnGenerateLevelsStart += OnGenerateStart;
		EventBusGame.OnGenerateLevelsEnd += AfterGeneration;
	}


	private static void EnsureCacheOwner(WorldLoader loader) {
		if (_cacheOwner == loader) return;
		_poseCache.Clear();
		_cacheOwner = loader;
	}

	/// <summary>
	/// 当前全部分支信息
	/// </summary>
	private static IEnumerable<BranchInfo> AllBranches(WorldLoader loader) {
		if (loader.currentBranch != null) yield return loader.currentBranch;
		var branches = BranchesField(loader);
		if (branches == null) yield break;
		foreach (var branch in branches)
			if (branch != null && branch != loader.currentBranch) yield return branch;
	}

	/// <summary>
	/// Debug输出向量信息
	/// </summary>
	private static string V(Vector3 v) => $"({v.x:0.##}, {v.y:0.##}, {v.z:0.##})";

	/// <summary>
	/// 记录所有已加载关卡的世界位姿
	/// </summary>
	private static void RecordLoadedLevels(WorldLoader loader) {
		EnsureCacheOwner(loader);
		foreach (var branch in AllBranches(loader)) {
			foreach (var info in branch.levelTracker) {
				var level = info?.GetLevel();
				if (level == null || string.IsNullOrEmpty(level.levelName)) continue;
				_poseCache[level.levelName] = (level.transform.position, level.transform.rotation);
			}
		}
	}

	/// <summary>
	/// 记录所有已加载关卡的世界位姿
	/// </summary>
	private static bool TryGetOldPose(WorldLoader loader, string levelName, out Vector3 pos, out Quaternion rot, out string source) {
		foreach (var branch in AllBranches(loader)) {
			foreach (var info in branch.levelTracker) {
				var level = info?.GetLevel();
				if (level == null || level.levelName != levelName) continue;
				pos = level.transform.position;
				rot = level.transform.rotation;
				source = "loaded";
				return true;
			}
		}
		EnsureCacheOwner(loader);
		if (_poseCache.TryGetValue(levelName, out var cached)) {
			(pos, rot) = cached;
			source = "cache";
			return true;
		}
		pos = Vector3.zero;
		rot = Quaternion.identity;
		source = "none";
		return false;
	}

	/// <summary>
	/// 读档前: 记下存档关卡当前的世界位姿
	/// </summary>
	public static void OnLoadSave(CL_SaveManager.SaveState save) {
		_pending = null;
		if (!Active || save?.levelData == null || save.levelData.Count == 0 || WorldLoader.instance == null) return;
		string target = save.levelData[0].levelName;
		if (TryGetOldPose(WorldLoader.instance, target, out var pos, out var rot, out var source)) {
			_pending = (target, pos, rot);
			MPMain.LogInfo($"[MP ReviveAlign] Load save ({save.type}): {target} was at {V(pos)} yaw {rot.eulerAngles.y:0.#} ({source})");
		} else {
			MPMain.LogWarning($"[MP ReviveAlign] Load save ({save.type}): previous pose of {target} unknown, keeping vanilla placement");
		}
	}

	/// <summary>
	/// 启动世界生成结束监听并记录关卡位置
	/// </summary>
	public static void OnGenerateStart(WorldLoader loader, BranchInfo branch, GenerationParameters? genParams) {
		_restore = genParams != null
			&& (genParams.generationType == GenerationParameters.GenerationType.saveLoad
				|| genParams.generationType == GenerationParameters.GenerationType.generateUntilFound);
		if (_restore && Active) _restoring = true;
	}

	/// <summary>
	/// 生成完成后刷新位姿缓存, 并结束读档生成流程
	/// </summary>
	private static void AfterGeneration(WorldLoader loader, BranchInfo branch, GenerationParameters genParams) {
		if (loader == null) return;
		if (_restore) {
			_restoring = false;
			if (_pending is { } pending) {
				MPMain.LogWarning($"[MP ReviveAlign] {pending.levelName} was not placed during restore generation, not aligned");
				_pending = null;
			}
		}
		if (Active) RecordLoadedLevels(loader);
	}

	/// <summary>
	/// 游戏整体平移世界 (高度过高时重定位) 时同步平移缓存
	/// </summary>
	public static void OnWorldOffset(float amount) {
		var keys = new List<string>(_poseCache.Keys);
		foreach (var key in keys) {
			var pose = _poseCache[key];
			_poseCache[key] = (pose.pos + Vector3.up * amount, pose.rot);
		}
	}

	/// <summary>
	/// 原版放置完关卡后把存档关卡放回复活前的位姿
	/// </summary>
	public static void OnLevelPlaced(GameObject go) {
		if (!_restoring || _pending is not { } pending || go == null) return;
		var level = go.GetComponent<M_Level>();
		if (level == null || level.levelName != pending.levelName) return;
		_pending = null;
		var t = level.transform;
		Vector3 placed = t.position;
		float angle = Quaternion.Angle(t.rotation, pending.rot);
		t.SetPositionAndRotation(pending.pos, pending.rot);
		MPMain.LogInfo($"[MP ReviveAlign] Placed {pending.levelName} back: {V(placed)} -> {V(pending.pos)}, rotated {angle:0.#} deg");
	}
}

[HarmonyPatch(typeof(CL_SaveManager), nameof(CL_SaveManager.LoadSave),
	new[] { typeof(CL_SaveManager.SaveState), typeof(CL_SaveManager.SessionSave) })]
public class Patch_ReviveAlign_LoadSave {
	static void Prefix(CL_SaveManager.SaveState save) {
		try {
			ReviveAlign.OnLoadSave(save);
		} catch (Exception ex) {
			MPMain.LogError($"[MP ReviveAlign] {ex}");
		}
	}
}

[HarmonyPatch(typeof(WorldLoader))]
public class Patch_ReviveAlign_WorldLoader {
	[HarmonyPatch("SetOffset")]
	[HarmonyPostfix]
	static void SetOffset_Postfix(float amount) {
		ReviveAlign.OnWorldOffset(amount);
		World.ShortcutMergeModule.Instance.OnWorldOffset(amount);
	}
}

/// <summary>
/// 在关卡Instantiate 随机翻转 拼接位置后立刻重设位置
/// </summary>
[HarmonyPatch(typeof(HandholdManager), nameof(HandholdManager.LoadHandholds))]
public class Patch_ReviveAlign_LoadHandholds {
	static void Prefix(GameObject g) {
		try {
			ReviveAlign.OnLevelPlaced(g);
		} catch (Exception ex) {
			MPMain.LogError($"[MP ReviveAlign] {ex}");
		}
	}
}
