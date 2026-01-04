using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using WKMultiMod.Data;
using static WKMultiMod.Data.PlayerData;

namespace WKMultiMod.Component;
// MultiPlayerHandComponent: 管理玩家手部的网络同步位置
public class RemoteHand : MonoBehaviour {
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