
using UnityEngine;

namespace WKMPMod.Component;

public class SimpleArmIK : MonoBehaviour {
	[Header("目标设置")]
	public Transform target;          // 这里拖入挂有 RemoteHand 的物体
	public float originalLength = 1f; // 骨骼在 Scale Y = 1 时的原始长度（单位：米）

	[Header("限制")]
	public float minScale = 0.1f;     // 最小缩放，防止模型塌陷
	public float maxScale = 10.0f;     // 最大缩放，防止拉伸过长

	private void Start() {
		// 如果你没有手动填长度，这里尝试计算手臂到手部初始位置的距离
		if (originalLength <= 0 && target != null) {
			originalLength = Vector3.Distance(transform.position, target.position);
		}
	}

	// 使用 LateUpdate 确保在 RemoteHand 的 Update 更新位置后执行
	private void LateUpdate() {
		if (target == null) return;

		// 1. 获取指向目标的向量
		Vector3 direction = target.position - transform.position;
		float currentDistance = direction.magnitude;

		if (currentDistance < 0.0001f) return;

		// 2. 旋转：让骨骼的 Y 轴指向目标
		// 注意：Unity 默认 LookRotation 是让 Z 轴指向目标
		// 我们通过从 Vector3.up (Y) 旋转到 direction 来实现 Y 轴指向
		transform.rotation = Quaternion.FromToRotation(Vector3.up, direction);

		// 3. 缩放：计算需要的 Y 轴缩放值
		// 公式：当前距离 / 原始长度 = 应有的缩放比例
		float targetScaleY = currentDistance / originalLength;

		// 应用限制
		targetScaleY = Mathf.Clamp(targetScaleY, minScale, maxScale);

		// 保持 X 和 Z 轴比例为 1，只缩放 Y
		transform.localScale = new Vector3(1, targetScaleY, 1);
	}
}
