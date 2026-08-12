using UnityEngine;

namespace WKMPMod.Component;

public class SimpleArmIK : MonoBehaviour {
	[Header("目标设置")]
	public Transform? target;          // 目标物体
	public float originalLength = 1f; // 骨骼在 Scale Y = 1 时的原始长度(单位:米)

	[Header("限制")]
	public float minScale = 0.1f;     // 最小缩放,防止模型塌陷
	public float maxScale = 10.0f;     // 最大缩放,防止拉伸过长

	private void Start() {
		// 如果没有手动填长度,这里尝试计算手臂到手部初始位置的距离
		if (originalLength <= 0 && target != null) {
			originalLength = Vector3.Distance(transform.position, target.position);
		}
	}

	private void LateUpdate() {
		if (target == null) return;

		// 1. 将起点和终点转换到 父节点本地空间 下计算
		Vector3 localStart = transform.parent.InverseTransformPoint(transform.position);
		Vector3 localTarget = transform.parent.InverseTransformPoint(target.position);

		// 2. 获取父节点本地空间下的指向向量与距离
		Vector3 localDirection = localTarget - localStart;
		float localDistance = localDirection.magnitude;

		if (localDistance < 0.0001f) return;

		// 3. 局部旋转：让骨骼的局部 Y 轴指向本地空间的目标方向
		transform.localRotation = Quaternion.FromToRotation(Vector3.up, localDirection);

		// 4. 局部缩放：计算局部 Y 轴所需的缩放比例
		float targetScaleY = localDistance / originalLength;

		// 应用限制
		targetScaleY = Mathf.Clamp(targetScaleY, minScale, maxScale);

		// 保持 X 和 Z 轴比例为 1,只缩放局部 Y
		transform.localScale = new Vector3(1, targetScaleY, 1);
	}
}
