using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using WKMultiMod.src.Core;
using WKMultiMod.src.Data;
using static WKMultiMod.src.Core.MultiPlayerCore;
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
	private bool _isFree = true;            // 是否空闲(未抓住对象)
	private Vector3 _targetWorldPosition;   // 目标世界位置
	private Vector3 _velocity = Vector3.zero;   // 当前速度,用于平滑插值
	public Vector3 DefaultLocalPosition { get; private set; }

	void Start() {
		// 根据左右手设置不同的默认位置
		DefaultLocalPosition = hand == PlayerData.HandType.Left
			? new Vector3(-0.4f, 0.5f, 0.4f)
			: new Vector3(0.4f, 0.5f, 0.4f);
	}

	// 每帧更新位置
	void Update() {
		// 如果是传送状态,不进行平滑移动
		if (_isTeleporting) return;

		Vector3 targetPosition = GetCurrentTargetPosition();

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

	// 获取当前目标位置
	private Vector3 GetCurrentTargetPosition() {
		if (_isFree) {
			// 空闲状态：回到默认位置(相对于父对象)
			return (transform.parent != null)
						? transform.parent.TransformPoint(DefaultLocalPosition)
						: DefaultLocalPosition;
		} else {
			// 抓住对象状态：使用指定的世界位置
			return _targetWorldPosition;
		}
	}

	// 从HandData更新手状态(Container调用这个方法)
	public void UpdateFromHandData(HandData handData) {
		// 更新空闲状态
		_isFree = handData.IsFree;

		if (!_isFree) {
			// 抓住对象：使用网络传来的世界位置
			_targetWorldPosition = handData.Position;
		}

		// 重置传送标志
		_isTeleporting = false;
	}

	// 直接调用
	public void UpdatePosition(Vector3 worldPosition) {
		_isTeleporting = false;  // 确保不是传送状态
		_isFree = false;  // 更新位置意味着抓住对象
		_targetWorldPosition = worldPosition;
	}

	// 立即传送
	public void Teleport(Vector3 wouldPosition) {
		_isTeleporting = true;
		_isFree = true;

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

	// 设置默认位置(可以动态调整,调试用)
	public void SetDefaultLocalPosition(Vector3 localPosition) {
		DefaultLocalPosition = localPosition;

		// 如果当前是空闲状态,立即更新目标
		if (_isFree) {
			_velocity = Vector3.zero;
		}
	}
}

// BillboardComponent: 使文本框始终面向摄像机
public class LootAtComponent : MonoBehaviour {
	private Camera mainCamera;
	void LateUpdate() {
		// 持续检查并尝试获取主摄像机
		if (mainCamera == null) {
			mainCamera = Camera.main;

			// 如果仍然找不到,则跳过本帧
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