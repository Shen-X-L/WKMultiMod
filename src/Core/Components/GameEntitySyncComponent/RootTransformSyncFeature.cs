using global::WKMPMod.Data;
using global::WKMPMod.NetWork;
using UnityEngine;

namespace WKMPMod.Components;

public class RootTransformSyncFeature : ISyncFeature {
	// 模块索引与模块名
	public byte FeatureIndex { get; set; }
	public string FeatureId => "RootTransform";

	// 所属实体
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

	public bool IsDirty {
		get {
			float currentHp = _entity != null ? _entity.health : float.NaN;
			return (_transform.position - _lastPosition).sqrMagnitude > PositionEpsilonSqr
				|| Quaternion.Angle(_transform.rotation, _lastRotation) > RotationEpsilonDegrees
				|| float.IsNaN(currentHp) != float.IsNaN(_lastHealth)
				|| (!float.IsNaN(currentHp) && Mathf.Abs(currentHp - _lastHealth) > HealthEpsilon);
		}
	}

	public RootTransformSyncFeature(NetworkedGameEntity netEntity) {
		_netEntity = netEntity;
		_transform = netEntity.transform;
		_entity = netEntity.Entity;

		_lastPosition = _targetPosition = _transform.position;
		_lastRotation = _targetRotation = _transform.rotation;
		_lastHealth = _entity != null ? _entity.health : float.NaN;
	}

	public void WriteState(DataWriter writer) {
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

		if (_entity != null && !float.IsNaN(hp)) 
			_entity.health = hp;

		if (!_netEntity.IsNetworkControlled && !MPSteamworks.IsHost) 
			_netEntity.SetNetworkControl(true);
	}

	public void OnUpdate(float deltaTime) {
		if (!_netEntity.IsNetworkControlled) return;
		_transform.position = Vector3.Lerp(_transform.position, _targetPosition, deltaTime * LERP_SPEED);
		_transform.rotation = Quaternion.Slerp(_transform.rotation, _targetRotation, deltaTime * LERP_SPEED);
	}

	public void OnReset() {
		_lastPosition = _targetPosition = _transform.position;
		_lastRotation = _targetRotation = _transform.rotation;
		_lastHealth = _entity != null ? _entity.health : float.NaN;
	}
}