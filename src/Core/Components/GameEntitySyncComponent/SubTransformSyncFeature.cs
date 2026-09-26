using UnityEngine;
using WKMPMod.Components;
using WKMPMod.Data;

public class SubTransformSyncFeature : ISyncFeature {
	public byte FeatureIndex { get; set; }
	public string FeatureId { get; }
	public Transform SubTransform { get; private set; }

	private Vector3 _lastLocalPos;
	private Quaternion _lastLocalRot;
	private Vector3 _targetLocalPos;
	private Quaternion _targetLocalRot;

	private const float PosEpsilonSqr = 0.001f;
	private const float LERP_SPEED = 15f;

	// 主机端检查脏数据：局部位置或旋转是否变化
	public bool IsDirty => SubTransform != null && (
		(SubTransform.localPosition - _lastLocalPos).sqrMagnitude > PosEpsilonSqr ||
		Quaternion.Angle(SubTransform.localRotation, _lastLocalRot) > 0.5f
	);

	public SubTransformSyncFeature(Transform subTransform) {
		SubTransform = subTransform;
		FeatureId = $"SubTransform_{subTransform?.name}";
		if (subTransform != null) {
			_lastLocalPos = _targetLocalPos = subTransform.localPosition;
			_lastLocalRot = _targetLocalRot = subTransform.localRotation;
		}
	}

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

	public void OnUpdate(float deltaTime) {
		if (SubTransform == null) return;
		// 客户端逐帧平滑插值
		SubTransform.localPosition = Vector3.Lerp(SubTransform.localPosition, _targetLocalPos, deltaTime * LERP_SPEED);
		SubTransform.localRotation = Quaternion.Slerp(SubTransform.localRotation, _targetLocalRot, deltaTime * LERP_SPEED);
	}

	public void OnReset() {
		if (SubTransform == null) return;
		_lastLocalPos = _targetLocalPos = Vector3.zero;
		_lastLocalRot = _targetLocalRot = Quaternion.identity;
	}
}