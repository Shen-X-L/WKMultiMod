using UnityEngine;
using WKMPMod.Components;
using WKMPMod.Data;

public class SubTransformSyncFeature : IStateSyncFeature, IFrameUpdateFeature {

	#region[运行时状态]

	// 引用
	private readonly NetworkedGameEntity _netEntity;
	private Transform _subTransform;

	// 更新前位置 接收数据后目标更新位置
	private Vector3 _lastLocalPos;
	private Quaternion _lastLocalRot;
	private Vector3 _targetLocalPos;
	private Quaternion _targetLocalRot;
	// 更新范围判定
	private const float PosEpsilonSqr = 0.001f;
	private const float LERP_SPEED = 15f;

	#endregion

	public SubTransformSyncFeature(Transform subTransform, NetworkedGameEntity netEntity) {
		_subTransform = subTransform;
		_netEntity = netEntity;
		if (subTransform != null) {
			_lastLocalPos = _targetLocalPos = subTransform.localPosition;
			_lastLocalRot = _targetLocalRot = subTransform.localRotation;
		}
	}

	#region[ISyncFeature 基础实现]

	public string FeatureId => "SubTransform";
	public void OnReset() {
		if (_subTransform == null) return;
		_lastLocalPos = _targetLocalPos = Vector3.zero;
		_lastLocalRot = _targetLocalRot = Quaternion.identity;
	}
	public void OnDestroy() { _subTransform = null; }

	#endregion

	#region[IStateSyncFeature 状态同步实现]

	public byte StateIndex { get; set; }
	public bool IsDirty => _subTransform != null 
		&& ((_subTransform.localPosition - _lastLocalPos).sqrMagnitude > PosEpsilonSqr 
			|| Quaternion.Angle(_subTransform.localRotation, _lastLocalRot) > 0.5f);

	public void WriteState(DataWriter writer) {
		if (_subTransform == null) return;
		_lastLocalPos = _subTransform.localPosition;
		_lastLocalRot = _subTransform.localRotation;
		writer.Put(_lastLocalPos);
		writer.Put(_lastLocalRot);
	}

	public void ReadState(DataReader reader) {
		_targetLocalPos = reader.GetVector3();
		_targetLocalRot = reader.GetQuaternion();
	}

	#endregion

	#region[IFrameUpdateFeature 帧更新实现]

	public void OnUpdate(float deltaTime) {
		if (!_netEntity.IsNetworkControlled) return;
		_subTransform.localPosition = Vector3.Lerp(_subTransform.localPosition, _targetLocalPos, deltaTime * LERP_SPEED);
		_subTransform.localRotation = Quaternion.Slerp(_subTransform.localRotation, _targetLocalRot, deltaTime * LERP_SPEED);
	}

	#endregion

	#region [IInitialSyncFeature 初始化状态实现]

	public byte InitialIndex { get; set; }

	public void WriteInitialState(DataWriter writer) {
		if (_subTransform == null) {
			writer.Put(Vector3.zero);
			writer.Put(Quaternion.identity);
			return;
		}
		writer.Put(_subTransform.localPosition);
		writer.Put(_subTransform.localRotation);
	}

	public void ReadInitialState(DataReader reader) {
		if (_subTransform == null) return;
		_targetLocalPos = reader.GetVector3();
		_targetLocalRot = reader.GetQuaternion();

		// 初始状态直接 Snap, 并同步更新历史缓存
		_subTransform.localPosition = _targetLocalPos;
		_subTransform.localRotation = _targetLocalRot;
		_lastLocalPos = _targetLocalPos;
		_lastLocalRot = _targetLocalRot;
	}

	#endregion
}