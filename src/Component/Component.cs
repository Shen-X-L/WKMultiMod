using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using WKMultiMod.src.Core;
using WKMultiMod.src.Data;
using static WKMultiMod.src.Core.MPCore;
using static WKMultiMod.src.Data.PlayerData;

namespace WKMultiMod.src.Component;

// MultiPlayerComponent: 管理玩家的网络同步位置和旋转
public class RemotePlayerComponent : MonoBehaviour {
	private Vector3 _targetPosition;    // 目标位置
	private Vector3 _velocity = Vector3.zero;   // 当前速度,用于平滑插值
	private bool _isTeleporting = false;

	void Update() {
		// 如果是传送状态,不进行平滑移动
		if (_isTeleporting) return;

		if (transform.position != _targetPosition) {
			// 动态计算平滑时间,确保最低速度
			float distance = Vector3.Distance(transform.position, _targetPosition); // 计算距离
			float smoothTime = Mathf.Max(0.1f, distance / 20f);	// 确保有最小速度

			transform.position = Vector3.SmoothDamp(
				transform.position,	// 当前位置
				_targetPosition,    // 目标位置
				ref _velocity,      // 速度引用
				smoothTime,         // 平滑时间
				float.MaxValue,     // 最大速度
				Time.deltaTime      // 时间增量
			);

			// 强制最低速度 2格/秒
			if (_velocity.magnitude < 2.0f && distance > 0.1f) {
				// 计算方向并设置最低速度
				Vector3 direction = (_targetPosition - transform.position).normalized;
				_velocity = direction * 2.0f;
			}
		}
	}

	// 更新位置(平滑移动)
	public void UpdatePosition(Vector3 newPosition) {
		_isTeleporting = false;  // 确保不是传送状态
		_targetPosition = newPosition;
	}

	// 立即更新旋转
	public void UpdateRotation(Quaternion newRotation) {
		transform.rotation = newRotation;
	}

	// 立即传送
	public void Teleport(Vector3 position, Quaternion? rotation = null) {
		_isTeleporting = true;

		// 立即设置位置
		transform.position = position;
		_targetPosition = position;  // 同步目标位置
		_velocity = Vector3.zero;    // 重置速度

		// 设置旋转(如果提供了)
		if (rotation.HasValue) {
			transform.rotation = rotation.Value;
		}

		// 传送完成后重置状态(可以延迟一帧确保不会立即开始平滑)
		StartCoroutine(ResetTeleportFlag());
	}

	// 传送结束后等待一帧
	private IEnumerator ResetTeleportFlag() {
		yield return null;  
		_isTeleporting = false;
	}

}


// MultiPlayerHandComponent: 管理玩家手部的网络同步位置
public class RemoteHandComponent : MonoBehaviour {
	public HandType hand;    // 手部标识 (0: 左手, 1: 右手)
	private bool _isTeleporting = false;    // 是否进行了传送
	private Vector3 _targetWorldPosition;   // 目标世界位置
	private Vector3 _velocity = Vector3.zero;   // 当前速度,用于平滑插值

	// 每帧更新位置
	void Update() {
		// 如果是传送状态,不进行平滑移动
		if (_isTeleporting) return;

		Vector3 targetPosition = _targetWorldPosition;

		if (transform.position != targetPosition) {
			float distance = Vector3.Distance(transform.position, targetPosition);
			float smoothTime = Mathf.Clamp(distance / 10f, 0.05f, 0.2f);

			transform.position = Vector3.SmoothDamp(
				transform.position,
				targetPosition,
				ref _velocity,
				smoothTime,
				float.MaxValue,
				Time.deltaTime
			);

			// 强制最低速度 0.5格/秒
			if (_velocity.magnitude < 0.5f && distance > 0.05f) {
				Vector3 direction = (targetPosition - transform.position).normalized;
				_velocity = direction * 0.5f;
			}
		}
	}



	// 从HandData更新手状态(Container调用这个方法)
	public void UpdateFromHandData(HandData handData) {

		// 抓住对象：使用网络传来的世界位置
		_targetWorldPosition = handData.Position;
		// 重置传送标志
		_isTeleporting = false;
	}

	// 直接调用
	public void UpdatePosition(Vector3 worldPosition) {
		_isTeleporting = false;  // 确保不是传送状态
		_targetWorldPosition = worldPosition;
	}

	// 立即传送
	public void Teleport(Vector3 wouldPosition) {
		_isTeleporting = true;

		// 立即设置位置
		transform.position = wouldPosition;
		_targetWorldPosition = wouldPosition;  // 同步目标位置
		_velocity = Vector3.zero;    // 重置速度

		// 传送完成后重置状态(可以延迟一帧确保不会立即开始平滑)
		StartCoroutine(ResetTeleportFlag());
	}

	// 传送结束后等待一帧
	private IEnumerator ResetTeleportFlag() {
		yield return null;
		_isTeleporting = false;
	}
}

// BillboardComponent: 使文本框始终面向摄像机
public class LootAtComponent : MonoBehaviour {
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
public class PlayerNameTag : MonoBehaviour {
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