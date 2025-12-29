using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace WKMultiMod.src.Component;

// BillboardComponent: 使文本框始终面向摄像机
public class LootAt : MonoBehaviour {
	private Camera mainCamera;

	[Header("Scaling Settings")]
	public bool maintainScreenSize = true;
	public float baseScale = 1.0f; // 初始缩放比例
	public float minScale = 0.5f;  // 最小缩放

	void LateUpdate() {
		// 持续检查并尝试获取主摄像机
		if (mainCamera == null) {
			mainCamera = Camera.main;

			// 如果仍然找不到,则跳过本帧
			if (mainCamera == null) {
				return;
			}
		}

		// 使 Transform (文本框) 朝向摄像机
		transform.rotation = mainCamera.transform.rotation;

		// 缩放抵消透视
		if (maintainScreenSize) {
			// 计算物体到相机的距离
			float distance = Vector3.Distance(transform.position, mainCamera.transform.position);

			// 核心公式：缩放值 = 距离 * 基础大小 * 修正系数
			float newScale = distance * baseScale * 0.1f;

			// 限制最小值，防止离太近时消失
			newScale = Mathf.Max(newScale, minScale);

			transform.localScale = new Vector3(newScale, newScale, newScale);
		}
	}
}

// 这个组件用来修改玩家名字
public class RemoteTag : MonoBehaviour {
	private TextMesh _textMesh;

	void Awake() {
		_textMesh = GetComponent<TextMesh>();
	}

	// 提供一个公开方法供外部调用
	public void SetText(string newText) {
		if (_textMesh != null) {
			_textMesh.text = newText;
		}
	}
}
