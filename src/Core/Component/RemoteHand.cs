using System.Collections;
using UnityEngine;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.Util;
using static UnityEngine.GraphicsBuffer;
using Vector3 = UnityEngine.Vector3;

namespace WKMPMod.Component;
// MultiPlayerHandComponent: 管理玩家手部的网络同步位置
public class RemoteHand : MonoBehaviour {
	[Header("手部设置")]
	[SerializeField] public byte handType;

	[Header("平滑移动设置")]
	[Tooltip("瞬移阈值: 超过此距离直接传送")]
	[SerializeField] public float teleportThreshold = 50f;

	[Tooltip("最大平滑距离: 超过此距离使用快速平滑")]
	[SerializeField] public float fastSmoothDistance = 10f;

	[Header("拖拽设置 (被敌对物体拉拽) ")]
	[Tooltip("基础拖拽力度")]
	[SerializeField] public float baseGrabStrength = 0.2f;

	[Tooltip("拖拽吸附点偏移")]
	[SerializeField] public Vector3 grabAttachOffset = Vector3.up * -0.5f;

	[Tooltip("拖拽最大有效距离")]
	[SerializeField] public float maxGrabDistance = 10f;

	[Tooltip("拖拽超时时间 (秒) ")]
	private const float GRAB_TIMEOUT_SECONDS = 1.0f;

	// 玩家ID,用于识别玩家
	public IDType playerId;
	// 状态标志
	private bool _isTeleporting;           // 是否正在传送
	private float _lastGrabTimestamp;      // 上次拖拽时间戳
	private bool _isBeingGrabbed;           // 是否正在被拖拽
	private bool _wasBeingGrabbed;          // 上一帧是否被拖拽 (用于检测状态变化)
	private bool _wasBeingHanged;         // 是否被抓取 (由外部设置, 可能影响位置同步逻辑)

	// 位置同步
	private Vector3 _targetWorldPosition;  // 目标世界坐标
	private Vector3 _smoothVelocity;       // 平滑移动速度

	// 动态力度
	private float _currentGrabStrength;

	//private TickTimer _debugTick = new TickTimer(0.5f); // 500ms更新一次位置, 避免过于频繁的更新

	#region[Unity生命周期函数]

	// 每帧更新位置
	void LateUpdate() {
		// 如果是传送状态,不进行平滑移动
		if (_isTeleporting) return;

		// 检查当前位置与目标位置的距离
		float distanceToTarget = Vector3.Distance(transform.position, _targetWorldPosition);

		// 如果距离超过阈值,直接瞬移
		if (distanceToTarget > teleportThreshold) {
			TeleportToPosition(_targetWorldPosition);
			return;
		}

		// 平滑移动到目标位置
		if (transform.position != _targetWorldPosition) {
			float smoothTime = CalculateDynamicSmoothTime(distanceToTarget);

			transform.position = Vector3.SmoothDamp(
				transform.position,     // 当前位置
				_targetWorldPosition,   // 目标位置
				ref _smoothVelocity,    // 速度引用
				smoothTime,             // 平滑时间
				float.MaxValue,         // 最大速度
				Time.deltaTime          // 时间增量
			);

			// 低速强制移动
			if (_smoothVelocity.magnitude < 0.5f && distanceToTarget > 0.05f) {
				Vector3 direction = (_targetWorldPosition - transform.position).normalized;
				_smoothVelocity = direction * 1f;
			}
		}
	}

	// 稳定帧施加力
	private void FixedUpdate() {
		// 拖拽超时检测
		if (_isBeingGrabbed && Time.time - _lastGrabTimestamp > GRAB_TIMEOUT_SECONDS) {
			_isBeingGrabbed = false;
			MPEventBusGame.NotifyRemoteGrab(playerId, _isBeingGrabbed);
		}

		// 应用拖拽物理
		if (_isBeingGrabbed) {
			ApplyPullPhysics();
		}
	}

	#endregion

	// 施加拖拽吸引力
	private void ApplyPullPhysics() {
		var player = ENT_Player.GetPlayer();
		if (player == null) return;

		// 计算距离 手部吸附点 -> 玩家
		float distance = Vector3.Distance(
			transform.position + grabAttachOffset,
			player.transform.position);

		// 距离超过10米断开
		if (distance > 10.0f) {
			player.SetGrappled(false);
			_isBeingGrabbed = false;
			return;
		}

		player.SetGrappled(true);

		// 计算距离因子 泊松分布, 距离适中时力度最大
		float k = distance / 2.0f;
		float distanceFactor = k * Mathf.Exp(1.0f - k);
		// 远距离衰减
		if (distance > 4.0f) distanceFactor *= (5.0f - distance) / 2.0f;

		// 平滑力度
		_currentGrabStrength = Mathf.Lerp(
			_currentGrabStrength,
			baseGrabStrength,
			Time.fixedDeltaTime * 0.8f
		);

		// 计算力
		Vector3 pullDirection = (transform.position - player.transform.position).normalized;
		Vector3 finalVelocity = pullDirection * Time.fixedDeltaTime * _currentGrabStrength * distanceFactor;

		player.TonguePull(finalVelocity);
	}

	// 根据距离计算平滑时间
	private float CalculateDynamicSmoothTime(float distance) {
		// 如果距离很远,使用更快的平滑
		if (distance > fastSmoothDistance) {
			// 使用对数曲线,距离越远平滑时间越短
			return Mathf.Clamp(Mathf.Log(distance) * 0.1f, 0.05f, 0.3f);
		}

		// 正常距离使用原来的计算方法
		return Mathf.Clamp(distance / 10f, 0.05f, 0.1f);
	}

	// 从HandData更新手位置(Container调用这个方法)
	/// <summary>
	/// 根据网络数据更新手部位置和状态
	/// </summary>
	/// <param name="handData">手部数据</param>
	/// <param name="targetPlayerId">该玩家ID是否被抓取/被拖拽(一般传入本玩家ID)</param>
	public void UpdateFromHandData(ref PlayerData.HandData handData, IDType targetPlayerId = 0) {
		_isTeleporting = false; // 重置传送标志
		_targetWorldPosition = handData.Position;  // 使用网络传来的世界位置

		//if (_debugTick.TryTick())
		//	MPMain.Debug($"UpdateFromHandData: " +
		//		$"PlayerId={playerId}, HandType={handType}, " +
		//		$"TargetPlayerId={handData.targetId}, InteractState={handData.interactState}, " +
		//		$"IsBeGrabbing={handData.IsBeGrabbing(targetPlayerId)}, " +
		//		$"IsBeHanging={handData.IsBeHanging(targetPlayerId)}");

		if (handData.IsBeGrabbing(targetPlayerId)) {
			_targetWorldPosition = handData.DesiredPosition;
			_isBeingGrabbed = true;
			_lastGrabTimestamp = Time.time;
		} else {
			_isBeingGrabbed = false;
		}

		// 被抓取状态变化通知
		if (handData.IsBeHanging(targetPlayerId) != _wasBeingHanged) {
			MPEventBusGame.NotifyRemoteHang(playerId, !_wasBeingHanged);
			_wasBeingHanged = !_wasBeingHanged;
		}

		// 被拖拽状态变化通知
		if (_isBeingGrabbed != _wasBeingGrabbed) {
			MPEventBusGame.NotifyRemoteGrab(playerId, _isBeingGrabbed);
			_wasBeingGrabbed = _isBeingGrabbed;
		}
	}

	// 立即传送
	public void TeleportToPosition(Vector3 worldPosition) {
		_isTeleporting = true;

		// 立即设置位置 重置速度
		transform.position = worldPosition;
		_targetWorldPosition = worldPosition;
		_smoothVelocity = Vector3.zero;

		// 传送完成后重置状态(延迟一帧确保不会立即开始平滑)
		StartCoroutine(ResetTeleportFlag());
	}

	// 传送结束后等待一帧
	private IEnumerator ResetTeleportFlag() {
		yield return null;
		_isTeleporting = false;
	}

}