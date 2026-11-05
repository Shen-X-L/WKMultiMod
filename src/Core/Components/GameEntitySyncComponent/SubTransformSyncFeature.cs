using UnityEngine;
using WKMPMod.Components;
using WKMPMod.Data;

public class SubTransformSyncFeature : IStateSyncFeature, IFrameUpdateFeature {
	public Transform SubTransform { get; private set; }
	// 更新前位置 接收数据后目标更新位置
	private Vector3 _lastLocalPos;
	private Quaternion _lastLocalRot;
	private Vector3 _targetLocalPos;
	private Quaternion _targetLocalRot;
	// 更新范围判定
	private const float PosEpsilonSqr = 0.001f;
	private const float LERP_SPEED = 15f;

	public SubTransformSyncFeature(Transform subTransform) {
		SubTransform = subTransform;
		FeatureId = $"SubTransform_{subTransform?.name}";
		if (subTransform != null) {
			_lastLocalPos = _targetLocalPos = subTransform.localPosition;
			_lastLocalRot = _targetLocalRot = subTransform.localRotation;
		}
	}

	#region[ISyncFeature 基础实现]

	public string FeatureId { get; }
	public void OnReset() {
		if (SubTransform == null) return;
		_lastLocalPos = _targetLocalPos = Vector3.zero;
		_lastLocalRot = _targetLocalRot = Quaternion.identity;
	}
	public void OnDestroy() { SubTransform = null; }

	#endregion

	#region[IStateSyncFeature 状态同步实现]

	public byte StateIndex { get; set; }
	public bool IsDirty => SubTransform != null 
		&& ((SubTransform.localPosition - _lastLocalPos).sqrMagnitude > PosEpsilonSqr 
			|| Quaternion.Angle(SubTransform.localRotation, _lastLocalRot) > 0.5f);

	public void WriteState(DataWriter writer) {
		if (SubTransform == null) return;
		_lastLocalPos = SubTransform.localPosition;
		_lastLocalRot = SubTransform.localRotation;
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
		if (SubTransform == null) return;
		SubTransform.localPosition = Vector3.Lerp(SubTransform.localPosition, _targetLocalPos, deltaTime * LERP_SPEED);
		SubTransform.localRotation = Quaternion.Slerp(SubTransform.localRotation, _targetLocalRot, deltaTime * LERP_SPEED);
	}
	
	#endregion
}