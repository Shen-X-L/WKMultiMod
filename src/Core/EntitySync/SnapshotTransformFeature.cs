using System.Collections.Generic;
using UnityEngine;
using WKMPMod.Components;
using WKMPMod.Data;
using WKMPMod.RemotePlayers;

namespace WKMPMod.EntitySync;

/// <summary>
/// 实体运动层 (entitymode smooth/full 时取代 RootTransformSyncFeature).
/// 所有者: 发送 发送时刻 + 位置 + 旋转 + 速度 + 血量; 按离玩家的距离分级 (近 10 Hz / 中 5 Hz / 远 2 Hz), 静止不发,
///         停下时补发一次速度为 0 的包.
/// 非所有者: 快照按发送时刻排入缓冲, 渲染时间 = 估计的所有者当前时刻 - 缓冲延迟 (约 1.5 个发送间隔 + 抖动, 自适应),
///         在两个快照之间按速度做 Hermite 插值; 缓冲用完按最后速度外推, 最多 1 个间隔.
///         所有者时刻由 "接收时刻 - 发送时刻" 的最小值估计 (每个所有者一份), 不需要共享时钟.
/// 参考: Mirror SnapshotInterpolation, Valve 插值延迟.
/// </summary>
public class SnapshotTransformFeature : IStateSyncFeature, IFrameUpdateFeature {
	#region[常量]

	private const float POS_EPSILON_SQR = 0.01f;       // 0.1 m
	private const float ROT_EPSILON_DEG = 1f;
	private const float HEALTH_EPSILON = 0.01f;
	private const float STOP_SPEED_SQR = 0.0025f;      // 0.05 m/s
	private const float NEAR_DISTANCE = 25f;
	private const float MID_DISTANCE = 60f;
	private const float TELEPORT_DISTANCE = 20f;       // 相邻快照位移超过视为瞬移, 直接跳
	private const int MAX_SNAPSHOTS = 16;
	private const float HEARTBEAT_INTERVAL = 1f;     // 静止时的心跳间隔 (小于接管超时 2 秒)

	#endregion

	private struct Snapshot {
		public float time;      // 所有者时钟
		public Vector3 pos;
		public Quaternion rot;
		public Vector3 vel;
	}

	/// <summary>
	/// 每个所有者的时钟差估计 (接收时刻 - 发送时刻 的下界, 缓慢上浮以适应延迟变化)
	/// </summary>
	private sealed class ClockEstimate {
		public bool init;
		public float offset;
		public float lastSample;
		public float jitter;
	}

	private static readonly Dictionary<IDType, ClockEstimate> _clocks = new();

	private readonly NetworkedGameEntity _netEntity;
	private readonly Transform _transform;
	private readonly GameEntity _entity;

	// 所有者端
	private Vector3 _lastSentPos;
	private Quaternion _lastSentRot;
	private float _lastSentHealth = float.NaN;
	private float _lastSentTime = -1f;
	private Vector3 _lastSentVel;
	private Vector3 _checkPos;
	private float _checkTime = -1f;
	private Vector3 _currentVel;
	private int _ticksSinceSend;

	// 非所有者端
	private readonly List<Snapshot> _buffer = new();
	private float _interval = 0.2f;

	public SnapshotTransformFeature(NetworkedGameEntity netEntity) {
		_netEntity = netEntity;
		_transform = netEntity.transform;
		_entity = netEntity.Entity;
		_lastSentPos = _checkPos = _transform.position;
		_lastSentRot = _transform.rotation;
		_lastSentHealth = _entity != null ? _entity.health : float.NaN;
	}

	#region[ISyncFeature]

	public string FeatureId => "SnapshotTransform";
	public byte StateIndex { get; set; }

	public void OnReset() {
		_buffer.Clear();
		_lastSentPos = _checkPos = _transform.position;
		_lastSentRot = _transform.rotation;
		_lastSentVel = Vector3.zero;
		_lastSentHealth = _entity != null ? _entity.health : float.NaN;
	}

	public void OnDestroy() { }

	#endregion

	#region[所有者: 发送]

	private static float Now => Time.realtimeSinceStartup;

	/// <summary>
	/// 离最近玩家 (本机 + 远程) 的距离决定每几个同步周期发一次: 近 1 / 中 2 / 远 5 (同步周期 0.1 s)
	/// </summary>
	private int RequiredTicks() {
		Vector3 p = _transform.position;
		float best = float.MaxValue;
		var local = ENT_Player.playerObject;
		if (local != null) best = (local.transform.position - p).sqrMagnitude;
		foreach (var container in RPManager.Instance.Players.Values) {
			var obj = container?.PlayerObject;
			if (obj == null) continue;
			float d = (obj.transform.position - p).sqrMagnitude;
			if (d < best) best = d;
		}
		if (best < NEAR_DISTANCE * NEAR_DISTANCE) return 1;
		if (best < MID_DISTANCE * MID_DISTANCE) return 2;
		return 5;
	}

	private float _dirtyEvalTime = -1f;
	private bool _dirtyValue;

	/// <summary>
	/// 同一同步周期内会被调用多次 (扫描判断、计数、写入), 结果按周期缓存, 避免计数器重复累加
	/// </summary>
	public bool IsDirty {
		get {
			float now = Now;
			if (now - _dirtyEvalTime < 0.05f) return _dirtyValue;
			_dirtyEvalTime = now;
			_dirtyValue = EvaluateDirty(now);
			return _dirtyValue;
		}
	}

	private bool EvaluateDirty(float now) {
		// 估计当前速度 (与上次检查之间的位移), 用于判断"刚停下"
		if (_checkTime > 0f && now > _checkTime) _currentVel = (_transform.position - _checkPos) / (now - _checkTime);
		_checkPos = _transform.position;
		_checkTime = now;

		float hp = _entity != null ? _entity.health : float.NaN;
		bool healthChanged = float.IsNaN(hp) != float.IsNaN(_lastSentHealth)
			|| (!float.IsNaN(hp) && Mathf.Abs(hp - _lastSentHealth) > HEALTH_EPSILON);
		bool moved = (_transform.position - _lastSentPos).sqrMagnitude > POS_EPSILON_SQR
			|| Quaternion.Angle(_transform.rotation, _lastSentRot) > ROT_EPSILON_DEG;
		bool stopped = _lastSentVel.sqrMagnitude > STOP_SPEED_SQR && _currentVel.sqrMagnitude <= STOP_SPEED_SQR;
		if (healthChanged || stopped) return true;
		// 静止时每秒补发一次心跳: 否则非所有者 2 秒收不到包就解除接管, 本地 AI 接手后再被拉回 (鼠王 "瞬移")
		if (now - _lastSentTime > HEARTBEAT_INTERVAL) return true;
		if (!moved) return false;
		return ++_ticksSinceSend >= RequiredTicks();
	}

	public void WriteState(DataWriter writer) {
		float now = Now;
		Vector3 pos = _transform.position;
		Vector3 vel = _lastSentTime > 0f && now > _lastSentTime ? (pos - _lastSentPos) / (now - _lastSentTime) : Vector3.zero;
		if (_currentVel.sqrMagnitude <= STOP_SPEED_SQR) vel = Vector3.zero;
		_lastSentPos = pos;
		_lastSentRot = _transform.rotation;
		_lastSentVel = vel;
		_lastSentTime = now;
		_lastSentHealth = _entity != null ? _entity.health : float.NaN;
		_ticksSinceSend = 0;

		writer.Put(now);
		writer.Put(pos);
		writer.Put(_lastSentRot);
		writer.Put(vel);
		writer.Put(_lastSentHealth);
	}

	#endregion

	#region[非所有者: 接收与插值]

	public void ReadState(DataReader reader) {
		var snapshot = new Snapshot {
			time = reader.GetFloat(),
			pos = reader.GetVector3(),
			rot = reader.GetQuaternion(),
			vel = reader.GetVector3(),
		};
		float hp = reader.GetFloat();
		if (_entity != null && !float.IsNaN(hp)) _entity.health = hp;

		UpdateClock(snapshot.time);

		if (_buffer.Count > 0) {
			var last = _buffer[_buffer.Count - 1];
			if (snapshot.time <= last.time) return; // 乱序/重复
			float gap = snapshot.time - last.time;
			if (gap < 1f) _interval = Mathf.Lerp(_interval, gap, 0.1f);
			// 瞬移, 或停发很久后 (静止 / 接管超时) 重新开始: 不跨越插值, 直接从新位置开始
			if ((snapshot.pos - last.pos).sqrMagnitude > TELEPORT_DISTANCE * TELEPORT_DISTANCE || gap > 1f) _buffer.Clear();
		}
		if (_buffer.Count == 0) {
			_transform.position = snapshot.pos;
			_transform.rotation = snapshot.rot;
		}
		_buffer.Add(snapshot);
		if (_buffer.Count > MAX_SNAPSHOTS) _buffer.RemoveAt(0);
	}

	private ClockEstimate Clock {
		get {
			IDType owner = _netEntity.CurrentOwnerId;
			if (!_clocks.TryGetValue(owner, out var clock)) {
				clock = new ClockEstimate();
				_clocks[owner] = clock;
			}
			return clock;
		}
	}

	private void UpdateClock(float sendTime) {
		var clock = Clock;
		float now = Now;
		float sample = now - sendTime;
		if (!clock.init) {
			clock.init = true;
			clock.offset = sample;
		} else {
			// 下界滤波: 更小的样本立即采用; 否则每秒最多上浮 0.05 s (适应延迟变大 / 时钟漂移)
			float dt = Mathf.Max(0f, now - clock.lastSample);
			clock.offset = Mathf.Min(sample, clock.offset + 0.05f * dt);
			clock.jitter = Mathf.Lerp(clock.jitter, Mathf.Abs(sample - clock.offset), 0.1f);
		}
		clock.lastSample = now;
	}

	public void OnUpdate(float deltaTime) {
		if (!_netEntity.IsNetworkControlled || _buffer.Count == 0) return;
		var clock = Clock;
		float delay = Mathf.Clamp(_interval * 1.5f + clock.jitter * 2f + 0.03f, 0.08f, 0.6f);
		float renderTime = Now - clock.offset - delay;

		// 丢掉比渲染时间早两个以上的旧快照 (保留一个作插值起点)
		while (_buffer.Count > 2 && _buffer[1].time <= renderTime) _buffer.RemoveAt(0);

		var first = _buffer[0];
		if (renderTime <= first.time || _buffer.Count == 1) {
			// 只有一个快照 / 还没到第一个快照: 从它按速度外推 (最多 1 个间隔)
			float ahead = Mathf.Clamp(renderTime - first.time, 0f, _interval);
			Apply(first.pos + first.vel * ahead, first.rot);
			return;
		}

		var a = _buffer[0];
		var b = _buffer[1];
		if (renderTime >= b.time) {
			// 缓冲用完: 按最后速度外推, 最多 1 个间隔
			var lastSnap = _buffer[_buffer.Count - 1];
			float ahead = Mathf.Clamp(renderTime - lastSnap.time, 0f, _interval);
			Apply(lastSnap.pos + lastSnap.vel * ahead, lastSnap.rot);
			return;
		}

		float span = b.time - a.time;
		float s = span > 0.0001f ? (renderTime - a.time) / span : 1f;
		Apply(Hermite(a.pos, a.vel * span, b.pos, b.vel * span, s), Quaternion.Slerp(a.rot, b.rot, s));
	}

	private void Apply(Vector3 pos, Quaternion rot) {
		_transform.position = pos;
		_transform.rotation = rot;
	}

	private static Vector3 Hermite(Vector3 p0, Vector3 m0, Vector3 p1, Vector3 m1, float s) {
		float s2 = s * s, s3 = s2 * s;
		return (2f * s3 - 3f * s2 + 1f) * p0 + (s3 - 2f * s2 + s) * m0 + (-2f * s3 + 3f * s2) * p1 + (s3 - s2) * m1;
	}

	#endregion
}
