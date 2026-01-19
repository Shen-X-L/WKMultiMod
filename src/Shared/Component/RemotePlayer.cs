using System.Collections;
using UnityEngine;

namespace WKMPMod.Component;
// MultiPlayerComponent: 管理玩家的网络同步位置和旋转
public class RemotePlayer : MonoBehaviour {
	private bool _isTeleporting = false;
	private Vector3 _targetPosition;    // 目标位置
	private Vector3 _velocity = Vector3.zero;   // 当前速度,用于平滑插值

	void Update() {
		// 如果是传送状态,不进行平滑移动
		if (_isTeleporting) return;

		if (transform.position != _targetPosition) {
			// 动态计算平滑时间,确保最低速度
			float distance = Vector3.Distance(transform.position, _targetPosition); // 计算距离
			float smoothTime = Mathf.Max(0.1f, distance / 20f); // 确保有最小速度

			transform.position = Vector3.SmoothDamp(
				transform.position, // 当前位置
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
