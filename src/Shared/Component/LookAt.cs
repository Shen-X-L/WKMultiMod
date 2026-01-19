using UnityEngine;

namespace WKMPMod.Component;

// BillboardComponent: 使文本框始终面向摄像机
public class LootAt : MonoBehaviour {
	private Camera? mainCamera;

	[Header("锁定大小")]
	public bool maintainScreenSize = true;
	[Header("初始缩放比例")]
	public float baseScale = 0.05f; // 初始缩放比例

	void LateUpdate() {
		if (mainCamera == null) {
			mainCamera = Camera.main;
			if (mainCamera == null) return;
		}

		transform.rotation = mainCamera.transform.rotation;

		if (maintainScreenSize) {
			float distance = Vector3.Distance(transform.position, mainCamera.transform.position);

			// 你的分段函数逻辑
			float scaleMultiplier;
			if (distance < 10.0f) {
				// y = 0.8x + 2
				scaleMultiplier = (0.8f * distance) + 2f;
			} else {
				// y = x
				scaleMultiplier = distance;
			}

			// 应用基础大小调节
			float finalScale = scaleMultiplier * baseScale;

			transform.localScale = new Vector3(finalScale, finalScale, finalScale);
		}
	}
}
