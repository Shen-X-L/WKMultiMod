using System.Collections;
using UnityEngine;
using WKMPMod.Data;
using static UnityEngine.GraphicsBuffer;
using Vector3 = UnityEngine.Vector3;

namespace WKMPMod.Component;
// MultiPlayerHandComponent: 管理玩家手部的网络同步位置
public class RemoteHand : MonoBehaviour {
	[Header("左右手")]
	public HandType hand;	// 手部标识

	[Header("距离设置")]
	[Tooltip("当当前位置与目标位置超过此距离时直接瞬移")]
	public float teleportThreshold = 50f;	// Unity可编辑的瞬移阈值

	[Tooltip("平滑移动的最大距离限制")]
	public float maxSmoothDistance = 10f;	// 超过此距离使用更快的平滑

	private bool _isTeleporting = false;	// 是否进行了传送
	private Vector3 _targetPosition;	// 目标世界位置
	private Vector3 _velocity = Vector3.zero;	// 当前速度,用于平滑插值

	// 每帧更新位置
	void LateUpdate() {
		// 如果是传送状态,不进行平滑移动
		if (_isTeleporting) return;

		// 检查当前位置与目标位置的距离
		float distance = Vector3.Distance(transform.position, _targetPosition);

		// 如果距离超过阈值,直接瞬移
		if (distance > teleportThreshold) {
			Teleport(_targetPosition);
			return;
		}

		if (transform.position != _targetPosition) {
			float smoothTime = CalculateSmoothTime(distance);

			transform.position = Vector3.SmoothDamp(
				transform.position,	// 当前位置
				_targetPosition,	// 目标位置
				ref _velocity,		// 速度引用
				smoothTime,			// 平滑时间
				float.MaxValue,		// 最大速度
				Time.deltaTime		// 时间增量
			);

			// 速度<0.5 且 距离 > 0.05时 强制最低速度 0.5格/秒
			if (_velocity.magnitude < 0.5f && distance > 0.05f) {
				Vector3 direction = (_targetPosition - transform.position).normalized;
				_velocity = direction * 0.5f;
			}
		}
	}

	// 根据距离计算平滑时间
	private float CalculateSmoothTime(float distance) {
		// 如果距离很远,使用更快的平滑
		if (distance > maxSmoothDistance) {
			// 使用对数曲线,距离越远平滑时间越短
			return Mathf.Clamp(Mathf.Log(distance) * 0.1f, 0.05f, 0.3f);
		}

		// 正常距离使用原来的计算方法
		return Mathf.Clamp(distance / 10f, 0.05f, 0.1f);
	}

	// 从HandData更新手位置(Container调用这个方法)
	public void UpdateFromHandData(ref PlayerData.HandData handData) {
		_isTeleporting = false; // 重置传送标志
		_targetPosition = handData.Position;	// 使用网络传来的世界位置
	}

	// 立即传送
	public void Teleport(Vector3 position) {
		_isTeleporting = true;

		// 立即设置位置
		transform.position = position;
		_targetPosition = position;  // 同步目标位置
		_velocity = Vector3.zero;    // 重置速度

		// 传送完成后重置状态(延迟一帧确保不会立即开始平滑)
		StartCoroutine(ResetTeleportFlag());
	}

	// 传送结束后等待一帧
	private IEnumerator ResetTeleportFlag() {
		yield return null;
		_isTeleporting = false;
	}
}