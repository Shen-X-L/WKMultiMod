using global::WKMPMod.Data;
using global::WKMPMod.NetWork;
using UnityEngine;

namespace WKMPMod.Components;

public class RootTransformSyncFeature : IStateSyncFeature, IFrameUpdateFeature {

	#region[运行时状态]

	// 引用
	private readonly NetworkedGameEntity _netEntity;
	private readonly Transform _transform;
	private readonly GameEntity _entity;

	// 更新前位置
	private Vector3 _lastPosition;
	private Quaternion _lastRotation;
	private float _lastHealth = float.NaN;

	// 接收数据后目标更新位置
	private Vector3 _targetPosition;
	private Quaternion _targetRotation;
	private const float LERP_SPEED = 15f;

	// 更新范围判定
	private const float PositionEpsilonSqr = 0.01f;
	private const float RotationEpsilonDegrees = 1.0f;
	private const float HealthEpsilon = 0.01f;

	// 心跳: 静止时也按该间隔发送, 须小于接收方的接管超时 (NetworkedGameEntity.TIMEOUT_DURATION = 2 秒).
	// 否则静止超过 2 秒的生物 (鼠王 / 遗骸 / HUNTER 等) 会被接收方解除接管, 本地 AI 接手后再被下一个包拉回 (瞬移).
	// 用 Time.time: 所有者暂停时游戏时间停止, 心跳随之停止, 接收方 2 秒后照旧放弃接管.
	private const float HEARTBEAT_INTERVAL = 1f;
	private float _lastSendTime = float.NegativeInfinity;

	#endregion

	public RootTransformSyncFeature(NetworkedGameEntity netEntity) {
		_netEntity = netEntity;
		_transform = netEntity.transform;
		_entity = netEntity.Entity;

		_lastPosition = _targetPosition = _transform.position;
		_lastRotation = _targetRotation = _transform.rotation;
		_lastHealth = _entity != null ? _entity.health : float.NaN;
	}

	#region[ISyncFeature 基础实现]

	public string FeatureId => "RootTransform";
	public void OnReset() {
		_lastPosition = _targetPosition = _transform.position;
		_lastRotation = _targetRotation = _transform.rotation;
		_lastHealth = _entity != null ? _entity.health : float.NaN;
	}
	public void OnDestroy() { }

	#endregion

	#region[IStateSyncFeature 状态同步实现]

	// 模块索引与模块名
	public byte StateIndex { get; set; }

	public bool IsDirty {
		get {
			float currentHp = _entity != null ? _entity.health : float.NaN;
			return (_transform.position - _lastPosition).sqrMagnitude > PositionEpsilonSqr
				|| Quaternion.Angle(_transform.rotation, _lastRotation) > RotationEpsilonDegrees
				|| float.IsNaN(currentHp) != float.IsNaN(_lastHealth)
				|| (!float.IsNaN(currentHp) && Mathf.Abs(currentHp - _lastHealth) > HealthEpsilon)
				|| Time.time - _lastSendTime > HEARTBEAT_INTERVAL;
		}
	}
	public void WriteState(DataWriter writer) {
		_lastSendTime = Time.time;
		_lastPosition = _transform.position;
		_lastRotation = _transform.rotation;
		_lastHealth = _entity != null ? _entity.health : float.NaN;

		writer.Put(_lastPosition);
		writer.Put(_lastRotation);
		writer.Put(_lastHealth);
	}

	public void ReadState(DataReader reader) {
		_targetPosition = reader.GetVector3();
		_targetRotation = reader.GetQuaternion();
		float hp = reader.GetFloat();

		if (_entity != null && !float.IsNaN(hp)) _entity.health = hp;
	}

	#endregion

	#region[IFrameUpdateFeature 帧更新实现]

	public void OnUpdate(float deltaTime) {
		if (!_netEntity.IsNetworkControlled) return;
		_transform.position = Vector3.Lerp(_transform.position, _targetPosition, deltaTime * LERP_SPEED);
		_transform.rotation = Quaternion.Slerp(_transform.rotation, _targetRotation, deltaTime * LERP_SPEED);
	}

	#endregion

	#region [IInitialSyncFeature 初始化状态实现]

	public byte InitialIndex { get; set; }

	public void WriteInitialState(DataWriter writer) {
		writer.Put(_transform.position);
		writer.Put(_transform.rotation);
		float currentHp = _entity != null ? _entity.health : float.NaN;
		writer.Put(currentHp);
	}

	public void ReadInitialState(DataReader reader) {
		_targetPosition = reader.GetVector3();
		_targetRotation = reader.GetQuaternion();
		float hp = reader.GetFloat();

		// 新玩家或对象生成时, 直接瞬移到位, 防止从原点插值过去
		if (_transform != null) {
			_transform.position = _targetPosition;
			_transform.rotation = _targetRotation;
			_lastPosition = _targetPosition;
			_lastRotation = _targetRotation;
		}

		if (_entity != null && !float.IsNaN(hp)) {
			_entity.health = hp;
			_lastHealth = hp;
		}
	}

	#endregion
}