using Steamworks.Data;
using System.Collections.Generic;
using UnityEngine;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.NetWork;
using WKMPMod.Util;
using WKMPMod.World;
using static WKMPMod.Data.MPWriterPool;

namespace WKMPMod.EntitySync;

public enum PropSyncAction : byte {
	Claim = 1,      // 抓起者 → 主机: 申请成为该物体的所有者
	Owner = 2,      // 主机 → 全体 (或单发给申请者): 该物体当前的所有者
	State = 3,      // 所有者 → 全体: 发送时刻 + 位置 + 旋转 + 速度 + 角速度
	Release = 4,    // 所有者 → 全体: 放手后静止, 最终位姿; 回到无主
}

/// <summary>
/// 可拖动物体同步 (entitymode full; M2 独占所有权 + M3 拖动, 第一版).
/// - 按需登记: 平时不扫描、不发包. 本机玩家抓起时才申请所有权; 主机先到先得裁决, 抢输的一方松手.
/// - 所有者: 抓着或仍在运动时约 20 Hz 发送状态; 放手后静止 0.5 秒 (或 10 秒上限) 发最终位姿并释放.
/// - 非所有者: 收到状态后刚体改为 kinematic, 按所有者时钟做缓冲插值 (同 SnapshotTransformFeature);
///   释放后放到最终位姿并恢复物理; 有所有者时不能抓 (CanInteract = false), 多人同抓 / 挂在物体上留到下一版.
/// - 物体身份: 关卡名 + 在关卡内的层级路径 (含同级序号) 的哈希, 两边同一关卡预制体生成的结构一致.
/// </summary>
public class PropSyncModule : Singleton<PropSyncModule>, ISyncModule {
	#region[常量]

	private const float SEND_INTERVAL = 0.05f;
	private const float REST_SPEED = 0.05f;
	private const float REST_TIME = 0.5f;
	private const float MAX_FREE_TIME = 10f;     // 放手后最多发送的时长
	private const float REMOTE_TIMEOUT = 3f;     // 非所有者: 超过该时间没有状态则视为释放
	private const float RESCAN_INTERVAL = 1f;

	#endregion

	private sealed class PropNet {
		public ulong id;
		public CL_Prop prop;
		public Rigidbody rb;
		public IDType owner;
		// 所有者端
		public int localHands;
		public float sendTimer;
		public float restTimer;
		public float freeTime;
		// 非所有者端
		public bool controlled;
		public float lastRecv;
		public readonly PropMotion motion = new();
	}

	private readonly Dictionary<ulong, PropNet> _props = new();
	private readonly Dictionary<ulong, CL_Prop> _index = new();
	private readonly Dictionary<int, ulong> _idCache = new();
	private float _lastScan = -10f;
	private readonly List<ulong> _removeKeys = new();

	private PropSyncModule() { }

	#region[ISyncModule]

	public string ModuleName => "PropSync";
	public bool IsEnabled { get; set; } = true;

	public static bool Active => MPCore.CanSync && EntityMode.Current == EntityMode.Mode.Full;

	public void OnResetMap() => Clear();
	public void OnLeave() => Clear();
	public void OnEnd() => Clear();

	private void Clear() {
		foreach (var net in _props.Values) if (net.controlled) StopControl(net, keepPose: true);
		_props.Clear();
		_index.Clear();
		_idCache.Clear();
	}

	public void OnSyncUpdate(float deltaTime) {
		if (_props.Count == 0) return;
		bool active = Active;
		float now = Time.realtimeSinceStartup;
		_removeKeys.Clear();
		foreach (var net in _props.Values) {
			bool gone = net.prop == null || !net.prop.gameObject.activeInHierarchy || net.prop.dead;
			if (net.owner == MPSteamworks.UserSteamId) {
				if (gone || !active) {
					// 物体被打碎 / 卸载 / 模式关闭: 释放
					if (active) SendRelease(net);
					_removeKeys.Add(net.id);
					continue;
				}
				OwnerTick(net, deltaTime);
			} else if (net.controlled) {
				// 所有者离开或长时间没有状态: 停在当前位置并恢复物理
				if (gone || !active || now - net.lastRecv > REMOTE_TIMEOUT || !MPSteamworks.ContainsMemberID(net.owner)) {
					if (!gone) MPMain.LogInfo($"[MP PropSync] {Name(net)}: no state from {net.owner}, released here");
					StopControl(net, keepPose: true);
					net.owner = 0;
					_removeKeys.Add(net.id);
				}
			} else if (net.owner == 0 || !MPSteamworks.ContainsMemberID(net.owner)) {
				_removeKeys.Add(net.id);
			}
		}
		foreach (var key in _removeKeys) _props.Remove(key);
	}

	#endregion

	#region[物体身份]

	public ulong IdOf(CL_Prop prop) {
		if (prop == null) return 0;
		int instanceId = prop.gameObject.GetInstanceID();
		if (_idCache.TryGetValue(instanceId, out ulong cached)) return cached;
		var level = prop.GetComponentInParent<M_Level>();
		ulong id = level == null ? 0 : MPUtil.Hash64($"{level.levelName}|{MPUtil.BuildFullPath(prop.transform, level.transform)}");
		_idCache[instanceId] = id;
		if (id != 0) _index[id] = prop;
		return id;
	}

	private CL_Prop? Resolve(ulong id) {
		if (_index.TryGetValue(id, out var prop) && prop != null) return prop;
		if (Time.unscaledTime - _lastScan < RESCAN_INTERVAL) return null;
		_lastScan = Time.unscaledTime;
		foreach (var p in Object.FindObjectsByType<CL_Prop>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)) IdOf(p);
		return _index.TryGetValue(id, out prop) && prop != null ? prop : null;
	}

	private PropNet? GetNet(ulong id, CL_Prop? prop = null) {
		if (_props.TryGetValue(id, out var net)) {
			if (net.prop == null && (prop ??= Resolve(id)) != null) { net.prop = prop; net.rb = prop.GetRigidbody(); }
			return net;
		}
		prop ??= Resolve(id);
		net = new PropNet { id = id, prop = prop!, rb = prop != null ? prop.GetRigidbody() : null! };
		_props[id] = net;
		return net;
	}

	private static string Name(PropNet net) => net.prop != null ? net.prop.name : net.id.ToString("X16");

	#endregion

	#region[本机抓取 (Harmony 调用)]

	/// <summary>
	/// 别人是该物体的所有者时不能抓
	/// </summary>
	public bool CanLocalGrab(CL_Prop prop) {
		if (!Active) return true;
		ulong id = IdOf(prop);
		return id == 0 || !_props.TryGetValue(id, out var net) || net.owner == 0 || net.owner == MPSteamworks.UserSteamId
			|| !MPSteamworks.ContainsMemberID(net.owner);
	}

	public void OnLocalGrab(CL_Prop prop) {
		if (!Active) return;
		ulong id = IdOf(prop);
		if (id == 0) return;
		var net = GetNet(id, prop)!;
		net.localHands++;
		net.restTimer = 0f;
		net.freeTime = 0f;
		if (net.owner == MPSteamworks.UserSteamId) return;
		// 预测: 先当作自己的, 主机裁决不同则松手
		if (net.controlled) StopControl(net, keepPose: true);
		net.owner = MPSteamworks.UserSteamId;
		if (MPSteamworks.IsHost) {
			BroadcastOwner(id, MPSteamworks.UserSteamId);
		} else {
			var writer = GetWriter(MPSteamworks.UserSteamId, MPSteamworks.Instance.HostSteamId, PacketType.PropSync);
			writer.Put((byte)PropSyncAction.Claim);
			writer.Put(id);
			MPSteamworks.Instance.SendToHost(writer, SendType.Reliable);
		}
		MPMain.LogInfo($"[MP PropSync] Grabbed {prop.name}: claiming");
	}

	public void OnLocalDrop(CL_Prop prop) {
		if (!Active) return;
		ulong id = IdOf(prop);
		if (id == 0 || !_props.TryGetValue(id, out var net)) return;
		net.localHands = Mathf.Max(0, net.localHands - 1);
		net.restTimer = 0f;
		net.freeTime = 0f;
	}

	/// <summary>
	/// 非所有者端由网络驱动的物体: 跳过原版远距离锁 (会改写 isKinematic)
	/// </summary>
	public bool IsControlled(CL_Prop prop) {
		if (_props.Count == 0 || !Active || !_idCache.TryGetValue(prop.gameObject.GetInstanceID(), out ulong id) || id == 0) return false;
		return _props.TryGetValue(id, out var net) && net.controlled;
	}

	/// <summary>
	/// 非所有者端每帧插值 (Update 后调用)
	/// </summary>
	public void ApplyMotion(CL_Prop prop) {
		if (_props.Count == 0 || !_idCache.TryGetValue(prop.gameObject.GetInstanceID(), out ulong id) || !_props.TryGetValue(id, out var net) || !net.controlled) return;
		if (net.motion.TryEvaluate(net.owner, out var pos, out var rot)) prop.transform.SetPositionAndRotation(pos, rot);
	}

	#endregion

	#region[所有者]

	private void OwnerTick(PropNet net, float deltaTime) {
		var rb = net.rb;
		if (rb == null) return;
		bool moving = rb.linearVelocity.sqrMagnitude > REST_SPEED * REST_SPEED || rb.angularVelocity.sqrMagnitude > REST_SPEED * REST_SPEED;
		if (net.localHands == 0) {
			net.freeTime += deltaTime;
			net.restTimer = moving ? 0f : net.restTimer + deltaTime;
			if (net.restTimer >= REST_TIME || net.freeTime >= MAX_FREE_TIME) {
				SendRelease(net);
				_removeKeys.Add(net.id);
				return;
			}
		}
		net.sendTimer += deltaTime;
		if (net.sendTimer < SEND_INTERVAL) return;
		net.sendTimer = 0f;
		var writer = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.PropSync);
		writer.Put((byte)PropSyncAction.State);
		writer.Put(net.id);
		writer.Put(Time.realtimeSinceStartup);
		writer.Put(net.prop.transform.position);
		writer.Put(net.prop.transform.rotation);
		writer.Put(rb.linearVelocity);
		writer.Put(rb.angularVelocity);
		MPSteamworks.Instance.Broadcast(writer, SendType.Unreliable | SendType.NoNagle);
	}

	private void SendRelease(PropNet net) {
		net.owner = 0;
		if (net.prop == null) return;
		var writer = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.PropSync);
		writer.Put((byte)PropSyncAction.Release);
		writer.Put(net.id);
		writer.Put(net.prop.transform.position);
		writer.Put(net.prop.transform.rotation);
		MPSteamworks.Instance.Broadcast(writer, SendType.Reliable);
		MPMain.LogInfo($"[MP PropSync] Released {Name(net)} at rest");
	}

	private void BroadcastOwner(ulong id, IDType owner) {
		var writer = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.PropSync);
		writer.Put((byte)PropSyncAction.Owner);
		writer.Put(id);
		writer.Put(owner);
		MPSteamworks.Instance.Broadcast(writer, SendType.Reliable);
	}

	#endregion

	#region[非所有者]

	private void StartControl(PropNet net) {
		if (net.controlled || net.prop == null || net.rb == null) return;
		// 所有者抓起时会 Unstick (插在墙上的木板等): 这边同样拔出, 播放同样的音效
		if (net.prop.stuck) net.prop.Unstick();
		net.rb.isKinematic = true;
		net.controlled = true;
		net.motion.Reset();
	}

	private void StopControl(PropNet net, bool keepPose) {
		if (!net.controlled) return;
		net.controlled = false;
		if (net.prop == null || net.rb == null) return;
		net.rb.isKinematic = false;
		net.rb.linearVelocity = Vector3.zero;
		net.rb.angularVelocity = Vector3.zero;
	}

	/// <summary>
	/// 自己预测抓起但主机判给了别人: 松开抓着它的手
	/// </summary>
	private static void DropLocalHands(CL_Prop prop) {
		var player = ENT_Player.playerObject;
		if (player == null || prop == null) return;
		for (int i = 0; i < player.hands.Length; i++) {
			var hand = player.hands[i];
			if (hand.grabTarget == prop || ReferenceEquals(hand.currentInteract, prop)) player.StopInteraction(i);
		}
	}

	#endregion

	#region[收包]

	[MPPacketHandler(PacketType.PropSync)]
	public static void HandlePropSync(IDType senderId, DataReader reader) {
		var module = Instance;
		if (!Active) return;
		var action = (PropSyncAction)reader.GetByte();
		ulong id = reader.GetULong();
		switch (action) {
			case PropSyncAction.Claim: module.OnClaim(senderId, id); break;
			case PropSyncAction.Owner: module.OnOwner(id, reader.GetIDType()); break;
			case PropSyncAction.State: module.OnState(senderId, id, reader); break;
			case PropSyncAction.Release: module.OnRelease(senderId, id, reader.GetVector3(), reader.GetQuaternion()); break;
		}
	}

	private void OnClaim(IDType sender, ulong id) {
		if (!MPSteamworks.IsHost) return;
		var net = GetNet(id)!;
		IDType current = net.owner;
		if (current == 0 || current == sender || !MPSteamworks.ContainsMemberID(current)) {
			OnOwner(id, sender);
			BroadcastOwner(id, sender);
			return;
		}
		// 已有所有者: 只告诉申请者
		var writer = GetWriter(MPSteamworks.UserSteamId, sender, PacketType.PropSync);
		writer.Put((byte)PropSyncAction.Owner);
		writer.Put(id);
		writer.Put(current);
		MPSteamworks.Instance.SendToPeer(sender, writer, SendType.Reliable);
		MPMain.LogInfo($"[MP PropSync] {Name(net)}: {sender} asked, already owned by {current}");
	}

	private void OnOwner(ulong id, IDType owner) {
		var net = GetNet(id)!;
		bool wasMine = net.owner == MPSteamworks.UserSteamId;
		net.owner = owner;
		if (owner == MPSteamworks.UserSteamId) {
			if (net.controlled) StopControl(net, keepPose: true);
			return;
		}
		if (wasMine && net.prop != null) {
			// 抢输: 松手, 物体交给对方驱动
			net.localHands = 0;
			DropLocalHands(net.prop);
			MPMain.LogInfo($"[MP PropSync] {Name(net)}: {owner} grabbed it first, letting go");
		}
	}

	private void OnState(IDType sender, ulong id, DataReader reader) {
		float sendTime = reader.GetFloat();
		Vector3 pos = reader.GetVector3();
		Quaternion rot = reader.GetQuaternion();
		Vector3 vel = reader.GetVector3();
		reader.GetVector3();   // 角速度: 暂只用于将来的外推
		var net = GetNet(id);
		if (net == null || net.prop == null) return;
		// 只接受当前所有者的状态 (所有权包可能晚到: 无主时先认发送者)
		if (net.owner == MPSteamworks.UserSteamId) return;
		if (net.owner == 0) net.owner = sender;
		if (net.owner != sender) return;
		if (!net.controlled) {
			StartControl(net);
			net.prop.transform.SetPositionAndRotation(pos, rot);
		}
		net.lastRecv = Time.realtimeSinceStartup;
		net.motion.Add(sender, sendTime, pos, rot, vel);
	}

	private void OnRelease(IDType sender, ulong id, Vector3 pos, Quaternion rot) {
		if (!_props.TryGetValue(id, out var net)) {
			// 没见过它的状态 (当时不在附近): 只摆正位置
			var prop = Resolve(id);
			if (prop != null && !prop.dead) prop.transform.SetPositionAndRotation(pos, rot);
			return;
		}
		if (net.owner != sender && net.owner != 0) return;
		if (net.prop != null) net.prop.transform.SetPositionAndRotation(pos, rot);
		StopControl(net, keepPose: true);
		net.owner = 0;
		_props.Remove(id);
	}

	#endregion
}

/// <summary>
/// 非所有者端的快照缓冲插值 (与 SnapshotTransformFeature 相同的做法: 每个所有者一份时钟差估计, 缓冲延迟, Hermite 插值, 外推最多 1 个间隔)
/// </summary>
public sealed class PropMotion {
	private struct Snap {
		public float time;
		public Vector3 pos;
		public Quaternion rot;
		public Vector3 vel;
	}

	private sealed class Clock {
		public bool init;
		public float offset;
		public float lastSample;
		public float jitter;
	}

	private static readonly Dictionary<IDType, Clock> _clocks = new();
	private readonly List<Snap> _buffer = new();
	private float _interval = 0.05f;

	private static float Now => Time.realtimeSinceStartup;

	public void Reset() => _buffer.Clear();

	public void Add(IDType owner, float time, Vector3 pos, Quaternion rot, Vector3 vel) {
		var clock = GetClock(owner);
		float now = Now;
		float sample = now - time;
		if (!clock.init) {
			clock.init = true;
			clock.offset = sample;
		} else {
			clock.offset = Mathf.Min(sample, clock.offset + 0.05f * Mathf.Max(0f, now - clock.lastSample));
			clock.jitter = Mathf.Lerp(clock.jitter, Mathf.Abs(sample - clock.offset), 0.1f);
		}
		clock.lastSample = now;

		if (_buffer.Count > 0) {
			var last = _buffer[_buffer.Count - 1];
			if (time <= last.time) return;
			float gap = time - last.time;
			if (gap < 1f) _interval = Mathf.Lerp(_interval, gap, 0.1f);
			if (gap > 1f) _buffer.Clear();
		}
		_buffer.Add(new Snap { time = time, pos = pos, rot = rot, vel = vel });
		if (_buffer.Count > 16) _buffer.RemoveAt(0);
	}

	public bool TryEvaluate(IDType owner, out Vector3 pos, out Quaternion rot) {
		pos = default;
		rot = default;
		if (_buffer.Count == 0) return false;
		var clock = GetClock(owner);
		float delay = Mathf.Clamp(_interval * 1.5f + clock.jitter * 2f + 0.03f, 0.06f, 0.4f);
		float renderTime = Now - clock.offset - delay;
		while (_buffer.Count > 2 && _buffer[1].time <= renderTime) _buffer.RemoveAt(0);

		var a = _buffer[0];
		if (renderTime <= a.time || _buffer.Count == 1) {
			float ahead = Mathf.Clamp(renderTime - a.time, 0f, _interval);
			pos = a.pos + a.vel * ahead;
			rot = a.rot;
			return true;
		}
		var b = _buffer[1];
		if (renderTime >= b.time) {
			var last = _buffer[_buffer.Count - 1];
			float ahead = Mathf.Clamp(renderTime - last.time, 0f, _interval);
			pos = last.pos + last.vel * ahead;
			rot = last.rot;
			return true;
		}
		float span = b.time - a.time;
		float s = span > 0.0001f ? (renderTime - a.time) / span : 1f;
		float s2 = s * s, s3 = s2 * s;
		pos = (2f * s3 - 3f * s2 + 1f) * a.pos + (s3 - 2f * s2 + s) * (a.vel * span) + (-2f * s3 + 3f * s2) * b.pos + (s3 - s2) * (b.vel * span);
		rot = Quaternion.Slerp(a.rot, b.rot, s);
		return true;
	}

	private static Clock GetClock(IDType owner) {
		if (!_clocks.TryGetValue(owner, out var clock)) {
			clock = new Clock();
			_clocks[owner] = clock;
		}
		return clock;
	}
}
