using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.NetWork;
using WKMPMod.Util;
using static WorldLoader;

namespace WKMPMod.World;

/// <summary>
/// 捷径汇聚 (战役, 临时方案; 原 ShortcutMerge 插件):
/// 走捷径 (缠绕水槽 / 垃圾焚烧口等) 时游戏新建生成分支, 后续关卡在另一套坐标中生成, 与走正路的玩家互相看不见.
/// 实测同种子下捷径汇入后的关卡序列与主路线一致, 只差一个整体 平移 + 绕 Y 旋转.
/// 处理: 记录主路线各关卡位姿; 走捷径的玩家进入汇入点 (捷径出口安全区之后第一个与主路线同名的关卡, 如下水道)
/// 并站稳 1 秒后, 把整个世界 (连同玩家) 刚体变换到主路线坐标, 之后与走正路的玩家处于同一坐标系.
/// 房主用 shortcutmerge 指令开关 (写入大厅数据, 客机跟随).
/// 以后如果改为按汇入点逆序生成捷径 (不再整体移动世界), 可移除本模块.
/// </summary>
public class ShortcutMergeModule : Singleton<ShortcutMergeModule>, ISyncModule {
	private const int VERIFY_LEVELS = 3;          // 汇入后用于校验变换的关卡数
	private const float VERIFY_TOLERANCE = 1.0f;   // 校验允许的位置误差 (米)
	private const float SETTLE_TIME = 1.0f;        // 连续着地多久才视为站稳 (秒)
	private const float CHECK_INTERVAL = 0.25f;

	private static readonly AccessTools.FieldRef<CL_GameManager, Vector3> CheckPositionField =
		AccessTools.FieldRefAccess<CL_GameManager, Vector3>("lastPlayerDistanceCheckPosition");
	private static readonly AccessTools.FieldRef<ENT_Player, Vector3> PlayerVelField =
		AccessTools.FieldRefAccess<ENT_Player, Vector3>("vel");

	private struct Pose {
		public string name;
		public Vector3 position;
		public Quaternion rotation;
	}

	// 主路线按顺序的关卡位姿 (世界坐标)
	private readonly List<Pose> _mainRoute = new();
	private WorldLoader? _routeOwner;
	// 每个捷径分支的处理结果: aligned / rejected
	private readonly Dictionary<string, string> _handled = new();
	private bool _running;
	private float _timer;
	private float _stableTime;
	private float _lastSettleCheck = -1f;
	private LevelInfo? _loggedAnchorLevel;

	private ShortcutMergeModule() {
		EventBusGame.OnGenerateLevelsStart += OnGenerateStart;
		EventBusGame.OnGenerateLevelsEnd += OnGenerateEnd;
	}

	#region[ISyncModule]

	public string ModuleName => "ShortcutMerge";
	public bool IsEnabled { get; set; } = true;

	public void OnResetMap() {
		_mainRoute.Clear();
		_routeOwner = null;
		ResetBranches();
	}

	public void OnLeave() => ResetBranches();
	public void OnEnd() => OnResetMap();

	private void ResetBranches() {
		_handled.Clear();
		_running = false;
	}

	/// <summary>
	/// 当前房间是否开启 (房主的大厅数据; 房主未写入时按开启)
	/// </summary>
	public static bool ModeOn {
		get {
			if (!MPCore.IsInLobby || !MPGameModeManager.IsCampaignMode) return false;
			string? value = MPSteamworks.Instance?.LobbyData?.GetValueOrDefault(MPKeys.SHORTCUT_MERGE);
			return string.IsNullOrEmpty(value) || !bool.TryParse(value, out bool on) || on;
		}
	}

	/// <summary>
	/// 本机是否处于与主路线不同的坐标系 (在捷径分支上且尚未对齐). 供 mass 同步等判断高度是否可比
	/// </summary>
	public static bool InSeparateFrame {
		get {
			var branch = WorldLoader.instance?.currentBranch;
			if (branch == null || branch.generationBranch == null) return false;
			var module = Instance;
			return module._running || !module._handled.TryGetValue(branch.id, out var state) || state != "aligned";
		}
	}

	public void ReadyUpdate(float deltaTime) {
		if (_running) return;
		_timer += deltaTime;
		if (_timer < CHECK_INTERVAL) return;
		_timer = 0f;
		try {
			Check();
		} catch (Exception ex) {
			MPMain.LogError($"[MP ShortcutMerge] {ex}");
		}
	}

	#endregion

	#region[主路线记录]

	private void OnGenerateStart(WorldLoader loader, BranchInfo branch, GenerationParameters genParams) {
		// 读档复活会从存档关卡重建 branch-root, 清空捷径处理状态
		if (genParams != null && (genParams.generationType == GenerationParameters.GenerationType.saveLoad
			|| genParams.generationType == GenerationParameters.GenerationType.generateUntilFound))
			ResetBranches();
	}

	/// <summary>
	/// 主分支生成完成后刷新记录 (含续接生成; 读档重建与捷径分支不记录)
	/// </summary>
	private void OnGenerateEnd(WorldLoader loader, BranchInfo branch, GenerationParameters genParams) {
		if (loader == null || branch == null || branch.generationBranch != null) return;
		if (genParams != null && (genParams.generationType == GenerationParameters.GenerationType.saveLoad
			|| genParams.generationType == GenerationParameters.GenerationType.generateUntilFound)) return;
		if (_routeOwner != loader) {
			_mainRoute.Clear();
			_routeOwner = loader;
		}
		var current = new List<Pose>();
		foreach (var info in branch.levelTracker) {
			var level = info?.GetLevel();
			if (level == null) continue;
			current.Add(new Pose { name = level.levelName, position = level.transform.position, rotation = level.transform.rotation });
		}
		if (current.Count == 0) return;
		// 续接生成时前面的关卡可能已被卸载: 以首个关卡名对齐后合并
		int start = _mainRoute.FindIndex(p => p.name == current[0].name);
		if (start < 0) {
			_mainRoute.Clear();
		} else {
			_mainRoute.RemoveRange(start, _mainRoute.Count - start);
		}
		_mainRoute.AddRange(current);
		MPMain.LogInfo($"[MP ShortcutMerge] Main route recorded: {_mainRoute.Count} levels ({_mainRoute[0].name} .. {_mainRoute[_mainRoute.Count - 1].name})");
	}

	/// <summary>
	/// 世界整体平移 (高空重定位) 时同步平移记录
	/// </summary>
	public void OnWorldOffset(float amount) {
		for (int i = 0; i < _mainRoute.Count; i++) {
			var p = _mainRoute[i];
			p.position += Vector3.up * amount;
			_mainRoute[i] = p;
		}
	}

	#endregion

	#region[汇聚]

	private void Check() {
		var loader = WorldLoader.instance;
		if (loader == null || !WorldLoader.isLoaded || CL_GameManager.gMan == null || CL_GameManager.IsLoading()) return;
		if (CL_GameManager.gMan.IsReviving() || CL_GameManager.isDead() || !ModeOn) return;

		var branch = loader.currentBranch;
		// 只处理由生成器分支 (捷径) 生成的分支
		if (branch == null || branch.generationBranch == null || _handled.ContainsKey(branch.id) || _mainRoute.Count == 0) return;

		var levels = branch.levelTracker;
		int anchor = FindAnchor(levels, out int mainIndex);
		if (anchor < 0) return; // 分支可能还未生成到汇入点

		int currentIndex = levels.IndexOf(branch.currentLevel);
		if (currentIndex < 0) return;
		// 只在进入汇入点 (捷径出口安全区之后与主路线同名的第一关) 或之后对齐:
		// 捷径出口安全区与主路线安全区是不同的关卡, 在那里对齐会让一起走捷径的人互相看不见, 也会浮空
		if (currentIndex < anchor) {
			_stableTime = 0f;
			_lastSettleCheck = -1f;
			_loggedAnchorLevel = null;
			return;
		}
		if (_loggedAnchorLevel != branch.currentLevel) {
			_loggedAnchorLevel = branch.currentLevel;
			var player = ENT_Player.GetPlayer();
			MPMain.LogInfo($"[MP ShortcutMerge] Entered {branch.currentLevel.GetLevel()?.levelName} (merge point {levels[anchor].GetLevel()?.levelName}), " +
				$"player y {(player != null ? player.transform.position.y : float.NaN):0.#}: aligning once standing on the ground");
		}
		// 必须双脚着地且未攀爬 (连续一段时间), 避免攀爬/跳跃途中整体移动世界
		if (!PlayerSettled()) return;

		if (!TryBuildTransform(levels, anchor, mainIndex, out var rotation, out var offset, out string report)) {
			_handled[branch.id] = "rejected";
			MPMain.LogWarning($"[MP ShortcutMerge] Branch {branch.id}: alignment rejected. {report}");
			return;
		}
		_handled[branch.id] = "aligned";
		MPCore.Instance.StartCoroutine(AlignWorld(loader, branch, rotation, offset, report));
	}

	/// <summary>
	/// 玩家是否已站稳: 着地、没有手处于攀爬/抓握状态, 并持续 SETTLE_TIME (飞行作弊时视为站稳)
	/// </summary>
	private bool PlayerSettled() {
		var player = ENT_Player.GetPlayer();
		float now = Time.unscaledTime;
		// 两次检查间隔过长 (中途未检查) 时不累计, 防止一进关卡就判定为已站稳
		float dt = _lastSettleCheck < 0f || now - _lastSettleCheck > 0.5f ? 0f : now - _lastSettleCheck;
		_lastSettleCheck = now;
		if (player == null) {
			_stableTime = 0f;
			return false;
		}
		bool settled = player.noclip || (player.IsGrounded() && !player.IsHanging() && !player.hands.Any(h => h != null && h.IsHolding()));
		_stableTime = settled ? _stableTime + dt : 0f;
		return _stableTime >= SETTLE_TIME;
	}

	/// <summary>
	/// 第一个与主路线同名 (且后续关卡也同名) 的非过渡关卡
	/// </summary>
	private int FindAnchor(List<LevelInfo> levels, out int mainIndex) {
		mainIndex = -1;
		for (int i = 0; i < levels.Count; i++) {
			var level = levels[i]?.GetLevel();
			if (level == null || level.levelName.StartsWith("Campaign_Interlude", StringComparison.Ordinal)) continue;
			int m = _mainRoute.FindIndex(p => p.name == level.levelName);
			if (m < 0) continue;
			// 需要后续若干关卡也一致, 避免偶然同名
			int need = Math.Min(VERIFY_LEVELS, Math.Min(levels.Count - i, _mainRoute.Count - m));
			if (need < 2) return -1;
			bool same = true;
			for (int k = 1; k < need; k++) {
				if (levels[i + k]?.GetLevel()?.levelName != _mainRoute[m + k].name) {
					same = false;
					break;
				}
			}
			if (!same) continue;
			mainIndex = m;
			return i;
		}
		return -1;
	}

	/// <summary>
	/// 由汇入点计算 捷径坐标 → 主路线坐标 的刚体变换, 并用后续关卡校验
	/// </summary>
	private bool TryBuildTransform(List<LevelInfo> levels, int anchor, int mainIndex,
		out Quaternion rotation, out Vector3 offset, out string report) {
		var a = levels[anchor].GetLevel().transform;
		var target = _mainRoute[mainIndex];

		// 只取绕 Y 的旋转分量, 防止数值误差引入倾斜
		float yaw = Mathf.DeltaAngle(a.rotation.eulerAngles.y, target.rotation.eulerAngles.y);
		rotation = Quaternion.Euler(0f, yaw, 0f);
		offset = target.position - rotation * a.position;

		float maxErr = 0f;
		int checkedCount = 0;
		for (int k = anchor; k < levels.Count && mainIndex + (k - anchor) < _mainRoute.Count; k++) {
			var level = levels[k]?.GetLevel();
			var main = _mainRoute[mainIndex + (k - anchor)];
			if (level == null || level.levelName != main.name) break;
			Vector3 predicted = rotation * level.transform.position + offset;
			maxErr = Mathf.Max(maxErr, Vector3.Distance(predicted, main.position));
			checkedCount++;
		}
		report = $"anchor={a.name.Replace("(Clone)", "")} shortcut={V(a.position)} main={V(target.position)} " +
			$"yaw={yaw:0.##} offset={V(offset)} verified={checkedCount} maxErr={maxErr:0.###}";
		return checkedCount >= 2 && maxErr <= VERIFY_TOLERANCE;
	}

	/// <summary>
	/// 把世界根节点 (含全部关卡/LOD/挂在世界下的实体) 连同玩家一起做刚体变换
	/// </summary>
	private IEnumerator AlignWorld(WorldLoader loader, BranchInfo branch, Quaternion rotation, Vector3 offset, string report) {
		_running = true;
		var player = ENT_Player.GetPlayer();
		var root = loader.transform;
		if (player == null || root == null) {
			_running = false;
			yield break;
		}
		MPMain.LogInfo($"[MP ShortcutMerge] Aligning branch {branch.id} to main route: {report}");

		Vector3 Apply(Vector3 p) => rotation * p + offset;

		var playerTf = player.transform;
		var originalParent = playerTf.parent;
		Vector3 playerBefore = playerTf.position;

		player.Lock();
		playerTf.SetParent(root, true);

		// 不挂在世界根下的游戏实体 (掉落物品等) 单独变换; 远程玩家等挂在其他节点下的不动
		var loose = GameEntity.entities
			.Where(e => e != null && e != player && e.transform.parent == null && e.gameObject.scene == playerTf.gameObject.scene)
			.ToList();

		root.SetPositionAndRotation(Apply(root.position), rotation * root.rotation);
		foreach (var entity in loose) {
			entity.transform.SetPositionAndRotation(Apply(entity.transform.position), rotation * entity.transform.rotation);
			var rb = entity.GetComponent<Rigidbody>();
			if (rb != null && !rb.isKinematic) rb.linearVelocity = rotation * rb.linearVelocity;
		}
		// 天空盒不挂在世界根下
		if (SkyboxManager.activeSkyboxes != null) {
			foreach (var sky in SkyboxManager.activeSkyboxes) {
				if (sky != null && sky.transform.parent == null)
					sky.transform.SetPositionAndRotation(Apply(sky.transform.position), rotation * sky.transform.rotation);
			}
		}

		Physics.SyncTransforms();
		yield return new WaitForFixedUpdate();

		foreach (var info in branch.levelTracker) info?.GetLevel()?.CycleColliders();
		Physics.SyncTransforms();
		yield return new WaitForFixedUpdate();

		playerTf.SetParent(originalParent, true);
		PlayerVelField(player) = rotation * PlayerVelField(player);
		// 平移距离检查点, 避免爬升距离因整体位移跳变
		var gm = CL_GameManager.gMan;
		if (gm != null) CheckPositionField(gm) = Apply(CheckPositionField(gm));
		player.UnLock();
		Physics.SyncTransforms();
		yield return new WaitForFixedUpdate();

		MPMain.LogInfo($"[MP ShortcutMerge] Aligned. player {V(playerBefore)} -> {V(playerTf.position)}, loose entities={loose.Count}");
		_running = false;
	}

	private static string V(Vector3 v) =>
		string.Format(CultureInfo.InvariantCulture, "({0:0.##}, {1:0.##}, {2:0.##})", v.x, v.y, v.z);

	#endregion
}
