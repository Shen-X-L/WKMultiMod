using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace WKMultiMod.src.Component;

// MultiPlayerComponent: 管理玩家的网络同步位置和旋转
public class MultiPlayerComponent : MonoBehaviour {
	public int id;  // 玩家ID, 用于在网络中识别不同的玩家实例

	// 更新玩家位置的方法
	public void UpdatePosition(Vector3 newPosition) {
		// 实际更新游戏对象的位置
		transform.position = newPosition;
	}

	// 更新玩家旋转的方法
	public void UpdateRotation(Vector3 newRotation) {
		// 设置游戏对象的欧拉角旋转
		transform.eulerAngles = newRotation;
	}
}

// MultiPlayerComponent: 管理玩家手部的网络同步位置和旋转
public class MultiPlayerHandComponent : MonoBehaviour {
	public int id;  // 玩家ID, 用于在网络中识别不同的玩家实例
	public int hand; // 手部标识, 0表示左手, 1表示右手

	// 更新手部位置的方法
	public void UpdateLoaclPosition(Vector3 newLocalPosition) {
		// 实际更新游戏手部的位置
		transform.localPosition = newLocalPosition;
	}
}

// BillboardComponent: 使文本框始终面向摄像机
public class LootAtComponent : MonoBehaviour {
	private Camera mainCamera;
	void LateUpdate() {
		// 持续检查并尝试获取主摄像机
		if (mainCamera == null) {
			mainCamera = Camera.main;

			// 如果仍然找不到，则跳过本帧
			if (mainCamera == null) {
				// Debug.LogWarning("Waiting for Main Camera..."); 
				return;
			}
		}

		// 使 Transform (文本框) 朝向摄像机
		transform.rotation = mainCamera.transform.rotation;

		// 使 Transform (文本框) y轴 朝向摄像机
		//transform.LookAt(transform.position + mainCamera.transform.rotation * Vector3.forward,
		//				 mainCamera.transform.rotation * Vector3.up);
	}
}