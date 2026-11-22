using HarmonyLib;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;
using WKMPMod.Components;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.NetWork;
using WKMPMod.RemotePlayers;
using WKMPMod.World;

namespace WKMPMod.EntitySync;

/// <summary>
/// 只追本机玩家的怪 (鼠王 DEN_RatKing / 遗骸 DEN_Remains_Active, entitymode full):
/// 原版 AI 写死目标为 ENT_Player.GetPlayer(), 联机时只有所有者那边真正跑 AI, 所以怪永远只追所有者.
/// - 所有者: 比较各玩家距离, 有别人比自己近 5 m 以上并持续 1 秒 (或自己已死) 时, 把所有权转交给最近的人 →
///   由被追的人本地运行原版 AI (追他、对他判定击杀). 遗骸: 进入危险半径的最近玩家立刻接手 (保证贴近它的人会被判定).
/// - 非所有者: 网络接管期间跳过原版 Update / TickUpdate (见 Patch_ChaseOwner), 位置由所有者驱动.
/// - 遗骸 "被看着就不动" (与作者讨论的方案):
///   · 非所有者看见它的瞬间, 本机先把它定在当前显示位置 (不再跟随网络插值), 并告诉所有者 "开始看" (之后每 0.5 秒心跳);
///   · 所有者: 有人在看 (1.2 秒内收到过心跳) 时取消每个 tick 的移动, 并像原版被看见一样摆贴墙 / 悬浮姿势;
///   · 移开视线: 告诉所有者 "停止看", 本机对齐到所有者的位置; 差距 > 2 m 或中间有墙 (转角) 时不立刻对齐,
///     等它下一次移动 (所有者位置变化) 或 3 秒后再对齐, 让这次 "跳" 混在正常的瞬移里.
/// - 诊断 (默认开启, 前缀 [MP Remains]): 每只遗骸在本机玩家 60 m 内时每 0.5 秒一行状态, 以及定住 / 对齐 / 换所有者等事件.
/// </summary>
public class ChaseOwnerFeature : ICustomSyncFeature, IFrameUpdateFeature {
	private const float CHECK_INTERVAL = 0.5f;
	private const float HANDOVER_MARGIN = 5f;
	private const float HANDOVER_DELAY = 1f;
	private const float DANGER_HANDOVER_MARGIN = 1f;   // 危险半径内: 比所有者近 1 m 即立刻接手
	private const float WATCH_HEARTBEAT = 0.5f;
	private const float WATCH_TIMEOUT = 1.2f;
	private const float ALIGN_MAX_JUMP = 2f;
	private const float ALIGN_DEFER_MAX = 3f;
	private const float LOG_INTERVAL = 0.5f;
	private const float LOG_RANGE = 60f;

	private enum Message : byte { WatchStart = 1, WatchStop = 2, WatchBeat = 3 }

	private static readonly AccessTools.FieldRef<DEN_Remains_Active, bool> KillingField =
		AccessTools.FieldRefAccess<DEN_Remains_Active, bool>("isKillingPlayer");
	private static readonly AccessTools.FieldRef<DEN_Remains_Active, bool> CanBeSeenField =
		AccessTools.FieldRefAccess<DEN_Remains_Active, bool>("canBeSeen");
	private static readonly AccessTools.FieldRef<DEN_Remains_Active, float> HazardField =
		AccessTools.FieldRefAccess<DEN_Remains_Active, float>("proximityHazardCount");
	private static readonly AccessTools.FieldRef<DEN_Remains_Active, Rigidbody> RigidField =
		AccessTools.FieldRefAccess<DEN_Remains_Active, Rigidbody>("rigid");
	private static readonly MethodInfo? OnBecomeVisibleMethod =
		AccessTools.Method(typeof(DEN_Remains_Active), "OnBecomeVisible");

	private readonly NetworkedGameEntity _net;
	private readonly DEN_Remains_Active? _remains;
	private float _checkTimer;
	private IDType _candidate;
	private float _candidateSince;

	// 所有者端: 正在看它的其他玩家 → 最后一次收到心跳的时间
	private readonly Dictionary<IDType, float> _watchers = new();
	private bool _remoteWatched;

	// 非所有者端: 本机看着时定住的显示位姿
	private bool _localVisible;
	private bool _frozen;
	private Vector3 _frozenPos;
	private Quaternion _frozenRot;
	private float _beatTimer;
	private bool _deferAlign;
	private Vector3 _deferNetPos;
	private float _deferSince;
	private float _movedWhileWatched;   // 本机看着期间显示位置的累计移动 (应为 0)

	// 诊断
	private float _logTimer;
	private Vector3 _lastLogPos;
	private bool _hasLastLogPos;

	public ChaseOwnerFeature(NetworkedGameEntity net) {
		_net = net;
		_remains = net.Entity as DEN_Remains_Active;
	}

	public string FeatureId => "ChaseOwner";
	public byte CustomIndex { get; set; }

	public void OnReset() {
		_candidate = 0;
		_watchers.Clear();
		_remoteWatched = false;
		_frozen = false;
		_deferAlign = false;
	}

	public void OnDestroy() { }

	/// <summary>
	/// 所有者: 现在是否有其他玩家正看着这只遗骸
	/// </summary>
	public bool RemoteWatched => _remoteWatched;

	private string Tag => $"{(_net.Entity != null ? _net.Entity.name : "?")}#{_net.networkId % 100000}";

	private static string V(Vector3 v) => string.Format(CultureInfo.InvariantCulture, "({0:0.0}, {1:0.0}, {2:0.0})", v.x, v.y, v.z);

	public void OnUpdate(float deltaTime) {
		if (!MPCore.CanSync || !_net.HasOwner || _net.Entity == null || _net.Entity.dead) return;
		if (_net.IsOwnedByMe) {
			if (_frozen) EndFreeze("became owner", align: false);
			UpdateWatchers();
			_checkTimer += deltaTime;
			if (_checkTimer >= CHECK_INTERVAL) {
				_checkTimer = 0f;
				CheckHandover();
			}
		} else if (_remains != null && _net.IsNetworkControlled) {
			UpdateNonOwner(deltaTime);
		}
		if (_remains != null) LogState(deltaTime);
	}

	#region[所有者]

	private void UpdateWatchers() {
		if (_remains == null) return;
		bool watched = false;
		if (_watchers.Count > 0) {
			var stale = new List<IDType>();
			foreach (var pair in _watchers) {
				if (Time.time - pair.Value > WATCH_TIMEOUT || !MPSteamworks.ContainsMemberID(pair.Key)) stale.Add(pair.Key);
				else watched = true;
			}
			foreach (var key in stale) {
				_watchers.Remove(key);
				MPMain.LogInfo($"[MP Remains] {Tag}: watcher {key} timed out");
			}
		}
		if (watched == _remoteWatched) return;
		_remoteWatched = watched;
		if (watched && !CanBeSeenField(_remains) && !KillingField(_remains)) {
			// 像原版被本机看见一样: 停下并摆贴墙 / 悬浮姿势 (姿势随位置同步给所有人)
			var rigid = RigidField(_remains);
			try { OnBecomeVisibleMethod?.Invoke(_remains, null); } catch (System.Exception ex) { MPMain.LogWarning($"[MP Remains] {Tag}: pose failed: {ex.Message}"); }
			if (rigid != null) rigid.linearVelocity = Vector3.zero;
		}
		MPMain.LogInfo($"[MP Remains] {Tag}: {(watched ? "watched by " + string.Join(",", _watchers.Keys) + ", frozen" : "nobody else watching, free to move")} at {V(_net.transform.position)}");
	}

	private void CheckHandover() {
		if (_remains != null && KillingField(_remains)) return;   // 正在扭脖子: 不换人
		Vector3 pos = _net.transform.position;
		var local = ENT_Player.GetPlayer();
		bool localAlive = local != null && !local.IsDead();
		float localDist = localAlive ? Vector3.Distance(local.transform.position, pos) : float.MaxValue;

		IDType nearest = 0;
		float nearestDist = float.MaxValue;
		foreach (var pair in RPManager.Instance.Players) {
			var obj = pair.Value?.PlayerObject;
			if (obj == null || !obj.activeInHierarchy || !MPSteamworks.ContainsMemberID(pair.Key)) continue;
			float d = Vector3.Distance(obj.transform.position, pos);
			if (d < nearestDist) {
				nearestDist = d;
				nearest = pair.Key;
			}
		}
		if (nearest == 0) {
			_candidate = 0;
			return;
		}
		// 遗骸: 进入危险半径的最近玩家立刻接手 (原版扭脖子只判定所有者本机)
		bool danger = _remains != null && nearestDist < _remains.proximityRadius && nearestDist + DANGER_HANDOVER_MARGIN < localDist;
		if (!danger && nearestDist + HANDOVER_MARGIN >= localDist) {
			_candidate = 0;
			return;
		}
		if (_candidate != nearest) {
			_candidate = nearest;
			_candidateSince = Time.time;
		}
		if (!danger && localAlive && Time.time - _candidateSince < HANDOVER_DELAY) return;
		_candidate = 0;
		string reason = !localAlive ? "owner dead" : danger ? $"inside danger radius {_remains!.proximityRadius:0}" : "nearer for 1 s";
		MPMain.LogInfo($"[MP ChaseOwner] {Tag}: {nearest} is nearer ({nearestDist:0.#} m vs {(localAlive ? localDist.ToString("0.#") + " m" : "dead")}, {reason}), handing over");
		EnemySyncModule.Instance.RequestOwnerTransfer(_net.networkId, nearest);
	}

	#endregion

	#region[非所有者]

	private void UpdateNonOwner(float deltaTime) {
		var remains = _remains!;
		var t = _net.transform;
		// SnapshotTransformFeature 已在本帧先写入网络插值位置
		Vector3 netPos = t.position;
		bool visible = remains.meshRenderer != null && remains.meshRenderer.isVisible;

		if (visible && !_localVisible) {
			_frozen = true;
			_deferAlign = false;
			_frozenPos = netPos;
			_frozenRot = t.rotation;
			_movedWhileWatched = 0f;
			_beatTimer = 0f;
			Send(Message.WatchStart);
			MPMain.LogInfo($"[MP Remains] {Tag}: seen here, frozen at {V(_frozenPos)} (owner {_net.CurrentOwnerId})");
		} else if (!visible && _localVisible) {
			Send(Message.WatchStop);
			EndFreeze("looked away", align: true, netPos);
		}
		_localVisible = visible;

		if (_frozen) {
			_beatTimer += deltaTime;
			if (_beatTimer >= WATCH_HEARTBEAT) {
				_beatTimer = 0f;
				Send(Message.WatchBeat);
			}
			t.SetPositionAndRotation(_frozenPos, _frozenRot);
			return;
		}
		if (_deferAlign) {
			// 等它下一次移动 (所有者那边位置变了) 或超时再对齐
			if ((netPos - _deferNetPos).sqrMagnitude > 0.05f * 0.05f || Time.time - _deferSince > ALIGN_DEFER_MAX) {
				_deferAlign = false;
				MPMain.LogInfo($"[MP Remains] {Tag}: deferred align done after {Time.time - _deferSince:0.0} s, {V(_frozenPos)} -> {V(netPos)}");
				return;
			}
			t.SetPositionAndRotation(_frozenPos, _frozenRot);
		}
	}

	private void EndFreeze(string why, bool align, Vector3 netPos = default) {
		if (!_frozen) return;
		_frozen = false;
		if (!align) {
			MPMain.LogInfo($"[MP Remains] {Tag}: freeze ended ({why})");
			return;
		}
		float gap = Vector3.Distance(_frozenPos, netPos);
		bool blocked = gap > 0.1f && Physics.Linecast(_frozenPos + Vector3.up * 0.5f, netPos + Vector3.up * 0.5f, ~0, QueryTriggerInteraction.Ignore);
		if (gap > ALIGN_MAX_JUMP || blocked) {
			_deferAlign = true;
			_deferNetPos = netPos;
			_deferSince = Time.time;
			MPMain.LogInfo($"[MP Remains] {Tag}: {why}, moved {_movedWhileWatched:0.00} m while watched; owner position {gap:0.0} m away{(blocked ? " behind a wall" : "")}, align deferred");
		} else {
			MPMain.LogInfo($"[MP Remains] {Tag}: {why}, moved {_movedWhileWatched:0.00} m while watched; aligned {gap:0.0} m to owner position");
		}
	}

	private void Send(Message message) {
		if (!_net.HasOwner) return;
		EnemySyncModule.Instance.SendCustomFeature(_net.CurrentOwnerId, _net.networkId, CustomIndex, w => w.Put((byte)message));
	}

	#endregion

	#region[收包]

	public void HandleCustomMessage(IDType senderId, DataReader reader, bool needReply) {
		var message = (Message)reader.GetByte();
		if (!_net.IsOwnedByMe) return;
		switch (message) {
			case Message.WatchStart:
				_watchers[senderId] = Time.time;
				MPMain.LogInfo($"[MP Remains] {Tag}: {senderId} started watching");
				break;
			case Message.WatchBeat:
				_watchers[senderId] = Time.time;
				break;
			case Message.WatchStop:
				_watchers.Remove(senderId);
				MPMain.LogInfo($"[MP Remains] {Tag}: {senderId} stopped watching");
				break;
		}
	}

	public void HandleCustomReply(IDType senderId) { }

	#endregion

	#region[诊断]

	private void LogState(float deltaTime) {
		_logTimer += deltaTime;
		if (_logTimer < LOG_INTERVAL) return;
		_logTimer = 0f;
		var remains = _remains!;
		var local = ENT_Player.GetPlayer();
		Vector3 pos = _net.transform.position;
		float localDist = local != null ? Vector3.Distance(local.transform.position, pos) : -1f;
		if (localDist < 0f || localDist > LOG_RANGE) {
			_hasLastLogPos = false;
			return;
		}
		float moved = _hasLastLogPos ? Vector3.Distance(_lastLogPos, pos) : 0f;
		_lastLogPos = pos;
		_hasLastLogPos = true;
		bool visible = remains.meshRenderer != null && remains.meshRenderer.isVisible;
		if (_frozen) _movedWhileWatched += moved;
		string role;
		if (_net.IsOwnedByMe) {
			var ages = new List<string>();
			foreach (var pair in _watchers) ages.Add($"{pair.Key % 10000}:{Time.time - pair.Value:0.0}s");
			role = $"OWNER active={remains.active} seenHere={CanBeSeenField(remains)} remoteWatched={_remoteWatched} watchers=[{string.Join(",", ages)}] " +
				$"hazard={HazardField(remains):0.00} killing={KillingField(remains)}";
		} else {
			role = $"remote owner={_net.CurrentOwnerId % 10000} controlled={_net.IsNetworkControlled} visibleHere={visible} frozen={_frozen} deferAlign={_deferAlign}";
		}
		var others = new List<string>();
		foreach (var pair in RPManager.Instance.Players) {
			var obj = pair.Value?.PlayerObject;
			if (obj != null) others.Add($"{pair.Key % 10000}:{Vector3.Distance(obj.transform.position, pos):0.0}m");
		}
		MPMain.LogInfo($"[MP Remains] {Tag} {role} pos={V(pos)} moved={moved:0.00}m me={localDist:0.0}m others=[{string.Join(",", others)}]");
	}

	#endregion

	/// <summary>
	/// Harmony 用: 该实体在本机是否由网络驱动 (此时跳过原版 AI)
	/// </summary>
	public static bool IsDrivenRemotely(Component entity) =>
		EntityMode.Current == EntityMode.Mode.Full && entity.TryGetComponent<NetworkedGameEntity>(out var net) && net.IsNetworkControlled;

	public static ChaseOwnerFeature? Find(Component entity) {
		if (!entity.TryGetComponent<NetworkedGameEntity>(out var net)) return null;
		foreach (var feature in net.Features) if (feature is ChaseOwnerFeature chase) return chase;
		return null;
	}
}
