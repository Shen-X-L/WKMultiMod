using UnityEngine;
using UnityEngine.UIElements;

namespace WKMPMod.Component;

// BillboardComponent: 使文本框始终面向摄像机
public class LookAt : MonoBehaviour {
	private Camera? mainCamera;

	[Header("锁定大小")]
	public bool maintainScreenSize = true;
	[Header("初始缩放比例")]
	public float baseScale = 0.1f; // 初始缩放比例
	[Header("用户设置缩放比例")]
	public float userScale = 1f;

	void LateUpdate() {
		if (mainCamera == null) {
			mainCamera = Camera.main;
			if (mainCamera == null) return;
		}

		// 锁定旋转为面向摄像机
		transform.rotation = mainCamera.transform.rotation;

		if (maintainScreenSize) {
			float distance = Vector3.Distance(transform.position, mainCamera.transform.position);

			// 分段函数逻辑 y = 0.8x + 2 (x<10) y = x (x>=10)
			float scaleMultiplier = Mathf.Max(distance, (0.8f * distance) + 2f);

			// 目标期望的最终全局大小 (World Scale)
			float finalScale = scaleMultiplier * baseScale * userScale;

			//transform.localScale = new Vector3(finalScale, finalScale, finalScale);

			// 如果有父节点 计算父节点的全局缩放 若无父节点则为 1
			Vector3 parentScale = transform.parent != null ? transform.parent.lossyScale : Vector3.one;

			// 防止父节点 Scale 为 0 时导致 NaN 导致物体消失
			float safeX = Mathf.Abs(parentScale.x) > 0.0001f ? parentScale.x : 1f;
			float safeY = Mathf.Abs(parentScale.y) > 0.0001f ? parentScale.y : 1f;
			float safeZ = Mathf.Abs(parentScale.z) > 0.0001f ? parentScale.z : 1f;

			// 一次性直接赋值局部缩放
			transform.localScale = new Vector3(finalScale / safeX, finalScale / safeY, finalScale / safeZ);
		}
	}
}
