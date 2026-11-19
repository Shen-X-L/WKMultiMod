using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.NetWork;
using WKMPMod.RemotePlayers;
using WKMPMod.World;

namespace WKMPMod.Components;

/// <summary>
/// 藤壶触手同步 (Barnacle_Tongue_Basic).
/// 原版每台机器各自随机选附着点 (触手朝向 + 随机偏移的射线)、随机悬挂 10-40 秒; 触手上有可攀爬抓点, 碰到触手的被抓住.
/// - 伸出: 自动伸出只由所有者决定, 非所有者跳过本地搜索, 按收到的附着点 (藤壶局部坐标) 与悬挂时长伸出;
///   事件触发的伸出 (ReachTongues) 谁触发谁发.
/// - 收回: 悬挂到时、触手被打断, 谁先发生谁发.
/// - 抓人各自本地判定: 只有本机玩家会被本机触手抓住 (远程玩家模型不抓, 避免同一次被抓算两次伤害);
///   被抓的人发出, 其他人把触手画到他身上并暂停这根触手的 AI, 直到松开.
/// - 瞄准玩家的触手 (困难模式 / aimForPlayer) 在所有者端也会瞄准更近的远程玩家.
/// 非所有者的消息先发给所有者, 由所有者转发给订阅者; 没有所有者时保持原版.
/// </summary>
public class BarnacleTongueSyncFeature : ICustomSyncFeature, IFrameUpdateFeature {
	#region[反射与查找表]

	internal static readonly AccessTools.FieldRef<Barnacle_Tongue_Core, Barnacle_Tongue_Interface_Module_Basic> Interface =
		AccessTools.FieldRefAccess<Barnacle_Tongue_Core, Barnacle_Tongue_Interface_Module_Basic>("barnacleInterface");
	internal static readonly AccessTools.FieldRef<Barnacle_Tongue_Basic, Vector3> AimPoint =
		AccessTools.FieldRefAccess<Barnacle_Tongue_Basic, Vector3>("aimPoint");
	internal static readonly AccessTools.FieldRef<Barnacle_Tongue_Basic, Vector3> TargetPoint =
		AccessTools.FieldRefAccess<Barnacle_Tongue_Basic, Vector3>("targetPoint");
	internal static readonly AccessTools.FieldRef<Barnacle_Tongue_Basic, Vector3> ReachPoint =
		AccessTools.FieldRefAccess<Barnacle_Tongue_Basic, Vector3>("reachPoint");
	internal static readonly AccessTools.FieldRef<Barnacle_Tongue_Basic, Vector3> AimTarget =
		AccessTools.FieldRefAccess<Barnacle_Tongue_Basic, Vector3>("aimTarget");
	internal static readonly AccessTools.FieldRef<Barnacle_Tongue_Basic, float> ReachTime =
		AccessTools.FieldRefAccess<Barnacle_Tongue_Basic, float>("reachTime");
	internal static readonly AccessTools.FieldRef<Barnacle_Tongue_Basic, bool> TargetFound =
		AccessTools.FieldRefAccess<Barnacle_Tongue_Basic, bool>("targetFound");
	internal static readonly AccessTools.FieldRef<Barnacle_Tongue_Basic, bool> WanderTarget =
		AccessTools.FieldRefAccess<Barnacle_Tongue_Basic, bool>("wanderTarget");
	internal static readonly AccessTools.FieldRef<Barnacle_Tongue_Basic, Barnacle_Tongue_Core.TongueState> State =
		AccessTools.FieldRefAccess<Barnacle_Tongue_Basic, Barnacle_Tongue_Core.TongueState>("state");
	internal static readonly AccessTools.FieldRef<Barnacle_Tongue_Basic, GameEntity> Grabbed =
		AccessTools.FieldRefAccess<Barnacle_Tongue_Basic, GameEntity>("grabbedEntity");
	internal static readonly AccessTools.FieldRef<Barnacle_Tongue_Basic, Transform> VisualObject =
		AccessTools.FieldRefAccess<Barnacle_Tongue_Basic, Transform>("visualObject");
	internal static readonly AccessTools.FieldRef<Barnacle_Tongue_Basic, Material> TongueMaterial =
		AccessTools.FieldRefAccess<Barnacle_Tongue_Basic, Material>("tongueMaterial");

	private static readonly Dictionary<DEN_Barnacle, BarnacleTongueSyncFeature> _byBarnacle = new();

	/// <summary>
	/// 触手所属藤壶的同步模块与触手序号; 生物同步关闭 / 没有所有者时返回 false (保持原版)
	/// </summary>
	public static bool TryGet(Barnacle_Tongue_Basic tongue, out BarnacleTongueSyncFeature feature, out byte index) {
		feature = null;
		index = 0;
		if (tongue == null || !MPCore.CanSync || !EnemySyncModule.Instance.IsEnabled) return false;
		var module = Interface(tongue);
		var barnacle = module?.GetBarnacle();
		if (barnacle == null || !_byBarnacle.TryGetValue(barnacle, out feature) || !feature._net.HasOwner) return false;
		int i = module.tongues.IndexOf(tongue);
		if (i < 0 || i > 255) return false;
		index = (byte)i;
		return true;
	}

	#endregion

	private enum Message : byte { Extend = 0, Retract = 1, Grab = 2 }

	private const float MAX_REMOTE_HOLD = 60f;
	private const float REST_SNAP = 0.005f;

	private readonly DEN_Barnacle _barnacle;
	private readonly NetworkedGameEntity _net;
	// 被远程玩家抓住的触手 → 抓住者与开始时间
	private readonly Dictionary<byte, (IDType holder, float since)> _heldByRemote = new();
	// 正抓着本机玩家的触手
	private readonly HashSet<byte> _heldHere = new();
	private static bool _applying;

	public bool IsOwner => _net.IsOwnedByMe;

	public BarnacleTongueSyncFeature(DEN_Barnacle barnacle, NetworkedGameEntity net) {
		_barnacle = barnacle;
		_net = net;
		_byBarnacle[barnacle] = this;
	}

	#region[ISyncFeature 基础实现]

	public string FeatureId => "BarnacleTongue";
	public byte CustomIndex { get; set; }

	public void OnReset() {
		_heldByRemote.Clear();
		_heldHere.Clear();
	}

	public void OnDestroy() {
		OnReset();
		if (_barnacle != null && _byBarnacle.TryGetValue(_barnacle, out var f) && f == this) _byBarnacle.Remove(_barnacle);
	}

	#endregion

	#region[IFrameUpdateFeature]

	/// <summary>
	/// 抓住者离开房间, 或太久没收到松开: 触手收回
	/// </summary>
	public void OnUpdate(float deltaTime) {
		if (_heldByRemote.Count == 0 || Time.frameCount % 60 != 0) return;
		foreach (var pair in new List<KeyValuePair<byte, (IDType holder, float since)>>(_heldByRemote)) {
			bool expired = Time.time - pair.Value.since > MAX_REMOTE_HOLD;
			if (MPSteamworks.ContainsMemberID(pair.Value.holder) && !expired) continue;
			_heldByRemote.Remove(pair.Key);
			if (TryGetTongue(pair.Key, out var tongue)) Apply(tongue.Retract);
		}
	}

	#endregion

	#region[发送]

	private bool TryGetTongue(byte index, out Barnacle_Tongue_Basic tongue) {
		tongue = null;
		if (_barnacle == null || _barnacle.tongueInterface is not Barnacle_Tongue_Interface_Module_Basic module || index >= module.tongues.Count) return false;
		tongue = module.tongues[index];
		return tongue != null;
	}

	/// <summary>
	/// 所有者直接广播给订阅者; 非所有者发给所有者, 由所有者转发
	/// </summary>
	private void Send(Message message, byte index, IDType origin, System.Action<DataWriter> body) {
		void Write(DataWriter writer) {
			writer.Put((byte)message);
			writer.Put(index);
			writer.Put(origin);
			body?.Invoke(writer);
		}
		if (_net.IsOwnedByMe) EnemySyncModule.Instance.BroadcastCustomFeature(_net.networkId, CustomIndex, Write);
		else if (_net.HasOwner) EnemySyncModule.Instance.SendCustomFeature(_net.CurrentOwnerId, _net.networkId, CustomIndex, Write);
	}

	#endregion

	#region[本地事件 (由补丁调用)]

	/// <summary>
	/// 触手 AI. 返回 false 跳过: 被远程玩家抓住期间; 自动搜索只由所有者执行
	/// </summary>
	public bool AllowAI(Barnacle_Tongue_Basic tongue, byte index) {
		if (_heldByRemote.ContainsKey(index)) return false;
		return State(tongue) != Barnacle_Tongue_Core.TongueState.searching || _net.IsOwnedByMe;
	}

	public void OnExtend(Barnacle_Tongue_Basic tongue, byte index) {
		if (_applying) return;
		Vector3 local = _barnacle.transform.InverseTransformPoint(TargetPoint(tongue));
		float reachTime = ReachTime(tongue);
		bool wander = WanderTarget(tongue);
		Send(Message.Extend, index, MPSteamworks.UserSteamId, w => { w.Put(local); w.Put(reachTime); w.Put(wander); });
	}

	public void OnRetract(byte index, bool heldLocalPlayer, bool forced) {
		if (_applying) return;
		bool released = _heldHere.Remove(index) || heldLocalPlayer;
		if (_heldByRemote.Remove(index)) forced = true; // 打断了别人身上的触手
		bool releaseAll = forced || released;
		Send(Message.Retract, index, MPSteamworks.UserSteamId, w => w.Put(releaseAll));
	}

	/// <summary>
	/// 触手扫描碰到的物体. 返回 false 跳过: 远程玩家模型不抓 (由他本人那边判定); 其他物体只由所有者抓
	/// </summary>
	public bool AllowScan(Barnacle_Tongue_Basic tongue, byte index) {
		if (_heldByRemote.ContainsKey(index)) return false;
		var module = Interface(tongue);
		if (!Physics.Linecast(tongue.tongue.position, ReachPoint(tongue), out var hit, module.playerMask)) return true;
		var entity = hit.collider.GetComponent<GameEntity>();
		if (entity == null || entity == ENT_Player.GetPlayer()) return true;
		if (entity is RemotePlayer || entity is RemoteProp) return false;
		return _net.IsOwnedByMe;
	}

	public void AfterScan(Barnacle_Tongue_Basic tongue, byte index) {
		if (State(tongue) != Barnacle_Tongue_Core.TongueState.grabbing) return;
		if (Grabbed(tongue) == null || Grabbed(tongue) != ENT_Player.GetPlayer() || !_heldHere.Add(index)) return;
		Send(Message.Grab, index, MPSteamworks.UserSteamId, null);
	}

	/// <summary>
	/// 收在原位的触手把末端/目标点对齐到触手模型的精确位置.
	/// 原版收回后末端与模型位置只差浮点误差, 高处 (几百米) 能通过原版的"不为零"判断但对 LookRotation 仍是零向量,
	/// Unity 每帧记一条 "Look rotation viewing vector is zero". 非所有者端触手等所有者的伸出消息时会长时间停在这个状态.
	/// </summary>
	public static void SnapResting(Barnacle_Tongue_Basic tongue) {
		if (Grabbed(tongue) != null || WanderTarget(tongue)) return;
		var visual = VisualObject(tongue);
		if (visual == null) return;
		Vector3 rest = visual.position;
		if ((TargetPoint(tongue) - rest).sqrMagnitude > REST_SNAP * REST_SNAP) return;
		if ((ReachPoint(tongue) - rest).sqrMagnitude > REST_SNAP * REST_SNAP) return;
		TargetPoint(tongue) = rest;
		ReachPoint(tongue) = rest;
	}

	/// <summary>
	/// 触手 Update 之后: 被远程玩家抓住的触手画到他身上
	/// </summary>
	public void AfterUpdate(Barnacle_Tongue_Basic tongue, byte index) {
		if (!_heldByRemote.TryGetValue(index, out var held)) return;
		if (_barnacle.dead) {
			_heldByRemote.Remove(index);
			return;
		}
		if (!RPManager.Instance.Players.TryGetValue(held.holder, out var container) || container.PlayerObject == null) return;
		var visual = VisualObject(tongue);
		if (visual == null) return;
		Vector3 point = container.PlayerObject.transform.position + Vector3.up * 0.5f;
		ReachPoint(tongue) = point;
		float length = Vector3.Distance(visual.position, point);
		if (length > 0.001f) visual.rotation = Quaternion.LookRotation(point - visual.position);
		visual.localScale = new Vector3(1f, 1f, length);
		TongueMaterial(tongue)?.SetFloat("_Length", length);
	}

	/// <summary>
	/// 所有者的瞄准: 原版只瞄本机玩家, 远程玩家更近时改为瞄他 (不计速度提前量)
	/// </summary>
	public void AfterFindTarget(Barnacle_Tongue_Basic tongue) {
		if (!_net.IsOwnedByMe) return;
		bool aims = (CL_GameManager.IsHardmode() && tongue.hardModeAdjustsDifficulty) || tongue.aimForPlayer;
		if (!aims) return;
		var module = Interface(tongue);
		Vector3 origin = tongue.tongue.position;
		Vector3 forward = tongue.aimEye != null ? tongue.aimEye.forward : tongue.tongue.forward;

		float best = float.MaxValue;
		var local = ENT_Player.playerObject;
		if (local != null && Vector3.Distance(local.transform.position, origin) < module.scanDistance
			&& Vector3.Angle(forward, (local.transform.position - origin).normalized) < 60f)
			best = Vector3.Distance(local.transform.position, origin);
		Transform target = null;
		foreach (var container in RPManager.Instance.Players.Values) {
			var remote = container?.PlayerObject?.transform;
			if (remote == null) continue;
			float distance = Vector3.Distance(remote.position, origin);
			if (distance >= module.scanDistance || distance >= best) continue;
			if (Vector3.Angle(forward, (remote.position - origin).normalized) >= 60f) continue;
			best = distance;
			target = remote;
		}
		if (target == null) return;

		// 与原版 FindTarget 的后半段相同, 目标换成远程玩家
		Vector3 direction = (target.position - origin).normalized + Random.onUnitSphere * module.tongueScatter * 0.05f;
		bool visible = _barnacle.sight != null && _barnacle.sight.CanSeeTarget(target);
		AimTarget(tongue) = target.position;
		if (tongue.onlyAimForEntity && !visible) {
			TargetFound(tongue) = false;
		} else if (Physics.Raycast(origin, direction, out var hit, module.scanDistance, module.hitMask)) {
			TargetFound(tongue) = true;
			AimPoint(tongue) = hit.point;
			State(tongue) = Barnacle_Tongue_Core.TongueState.searching;
		}
	}

	#endregion

	#region[ICustomSyncFeature 收包]

	public void HandleCustomMessage(IDType senderId, DataReader reader, bool needReply) {
		var message = (Message)reader.GetByte();
		byte index = reader.GetByte();
		IDType origin = reader.GetULong();
		if (origin == MPSteamworks.UserSteamId) return; // 所有者转发回来的自己的消息

		Vector3 local = default;
		float reachTime = 0f;
		bool wander = false, releaseAll = false;
		switch (message) {
			case Message.Extend:
				local = reader.GetVector3();
				reachTime = reader.GetFloat();
				wander = reader.GetBool();
				break;
			case Message.Retract:
				releaseAll = reader.GetBool();
				break;
		}

		// 所有者: 转发给其他订阅者 (发送者会按 origin 忽略)
		if (_net.IsOwnedByMe && senderId != MPSteamworks.UserSteamId) {
			EnemySyncModule.Instance.BroadcastCustomFeature(_net.networkId, CustomIndex, w => {
				w.Put((byte)message);
				w.Put(index);
				w.Put(origin);
				if (message == Message.Extend) { w.Put(local); w.Put(reachTime); w.Put(wander); }
				else if (message == Message.Retract) w.Put(releaseAll);
			});
		}

		if (_barnacle == null || _barnacle.dead || !TryGetTongue(index, out var tongue)) return;
		switch (message) {
			case Message.Extend:
				// 正抓着本机玩家 / 被别人抓着时不改
				if (_heldHere.Contains(index) || _heldByRemote.ContainsKey(index)) return;
				AimPoint(tongue) = _barnacle.transform.TransformPoint(local);
				TargetFound(tongue) = true;
				WanderTarget(tongue) = wander;
				Apply(tongue.Extend);
				ReachTime(tongue) = reachTime;
				break;
			case Message.Retract: {
				bool wasHeld = _heldByRemote.Remove(index);
				if (_heldHere.Contains(index) && !releaseAll) return; // 本机玩家正被抓着: 只有触手被打断才松开
				var state = State(tongue);
				if (!wasHeld && !_heldHere.Contains(index)
					&& (state == Barnacle_Tongue_Core.TongueState.retracting || state == Barnacle_Tongue_Core.TongueState.searching)) return;
				_heldHere.Remove(index);
				Apply(tongue.Retract);
				break;
			}
			case Message.Grab:
				// 本机玩家同时被这根触手抓住: 各自保留自己的 (罕见)
				if (_heldHere.Contains(index)) return;
				_heldByRemote[index] = (origin, Time.time);
				MPMain.LogInfo($"[MP BarnacleTongue] {_barnacle.name}#{index} grabbed {origin}");
				break;
		}
	}

	public void HandleCustomReply(IDType senderId) { }

	private static void Apply(System.Action action) {
		_applying = true;
		try { action(); } finally { _applying = false; }
	}

	#endregion
}
