using System;
using UnityEngine;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.Util;
using static ENT_Player;
using Quaternion = UnityEngine.Quaternion;
using Vector3 = UnityEngine.Vector3;

namespace WKMPMod.Component;

//仅获取本地玩家信息并触发事件给其他系统使用
//仅在联机时创建一个实例
public class LocalPlayer : MonoBehaviour {
	private const float POSITION_CHANGE_THRESHOLD_SQR = 0.0025f; // 0.05单位的平方
	private const float ROTATION_CHANGE_THRESHOLD_DEG = 0.5f;    // 最小旋转角度

	// 网络发送控制
	public bool ShouldSendData { get; set; } = false;  // 改为属性,更清晰

	// 玩家标识
	public ulong UserId { get; private set; }          // 本地玩家SteamID

	// 状态缓存
	private Vector3 _lastPosition;
	private Quaternion _lastRotation;
	private Vector3 _lastLeftHandPosition;
	private Vector3 _lastRightHandPosition;

	// 定时器
	private TickTimer _sendDataTimer;//本地玩家数据频率器, 定时发送玩家数据
	private TickTimer _teleportCooldownTimer;//传输状态定时器, 期间内传送标记为真

	// 缓存引用
	private ENT_Player _cachedPlayer;
	private Hand[] _cachedHands;

	public void Start() {
		InitializeTimers();
		CachePlayerReferences();
	}

	public void Update() {
		// 不需要发送时停止更新, 该值由 联机管理类 控制
		if (!ShouldSendData)
			return;
		// 发送本地玩家数据
		TrySendLocalPlayerData();
	}
	#region 初始化方法

	// 初始化定时器
	private void InitializeTimers() {
		_sendDataTimer = new TickTimer(30);  // 20Hz
		_teleportCooldownTimer = new TickTimer(1.0f);         // 传送冷却1秒
	}

	// 缓存玩家引用
	public void CachePlayerReferences() {
		_cachedPlayer = ENT_Player.GetPlayer();
		if (_cachedPlayer != null) {
			_cachedHands = _cachedPlayer.hands;
		}
	}

	// 重置状态缓存
	public void Initialize(ulong userId) {
		UserId = userId;
		ResetStateCache();
	}

	#endregion

	#region 核心逻辑

	// 尝试发送本地玩家数据
	private void TrySendLocalPlayerData() {
		// 频率限制
		if (!_sendDataTimer.TryTick())
			return;

		// 尝试创建玩家数据
		if (!TryCreateLocalPlayerData(out PlayerData playerData))
			return;

		// 设置传送标记(传送冷却期间标记为传送)
		playerData.IsTeleport = !_teleportCooldownTimer.IsTickReached;

		// 通过事件总线发送数据
		MPEventBusGame.NotifyPlayerMove(playerData);
	}

	// 尝试创建本地玩家数据
	public bool TryCreateLocalPlayerData(out PlayerData data) {
		data = default;

		// 验证玩家引用
		if (!ValidatePlayerReferences())
			return false;

		// 检查是否有显著变化
		if (!HasSignificantChanges())
			return false;

		// 创建数据包
		data = CreatePlayerDataPacket();

		// 更新缓存
		UpdateStateCache();

		return true;
	}
	#endregion

	#region 辅助方法

	// 验证或获取玩家引用
	private bool ValidatePlayerReferences() {
		if (_cachedPlayer == null) {
			_cachedPlayer = ENT_Player.GetPlayer();
			if (_cachedPlayer == null) {
				MPMain.LogError(Localization.Get("LocalPlayer", "DataAcquisitionException"));
				return false;
			}
			_cachedHands = _cachedPlayer.hands;
		}

		if (_cachedHands == null || _cachedHands.Length < 2) {
			MPMain.LogError(Localization.Get("LocalPlayer", "HandDataAcquisitionException"));
			return false;
		}

		return true;
	}

	// 检验
	private bool HasSignificantChanges() {
		// 使用平方距离和点积优化性能
		bool hasPositionChange =
			(_cachedPlayer.transform.position - _lastPosition).sqrMagnitude >=
			POSITION_CHANGE_THRESHOLD_SQR;

		bool hasRotationChange =
			!IsRotationSimilar(_cachedPlayer.transform.rotation, _lastRotation, ROTATION_CHANGE_THRESHOLD_DEG);

		bool hasLeftHandChange =
			(_cachedHands[(int)HandType.Left].GetHoldWorldPosition() - _lastLeftHandPosition).sqrMagnitude >=
			POSITION_CHANGE_THRESHOLD_SQR;

		bool hasRightHandChange =
			(_cachedHands[(int)HandType.Right].GetHoldWorldPosition() - _lastRightHandPosition).sqrMagnitude >=
			POSITION_CHANGE_THRESHOLD_SQR;

		return hasPositionChange || hasRotationChange || hasLeftHandChange || hasRightHandChange;
	}

	// 创建玩家数据包
	private PlayerData CreatePlayerDataPacket() {
		return new PlayerData {
			playId = UserId,
			TimestampTicks = DateTime.UtcNow.Ticks,
			Position = _cachedPlayer.transform.position,
			Rotation = _cachedPlayer.transform.rotation,
			LeftHand = new HandData {
				Position = _cachedHands[(int)HandType.Left].GetHoldWorldPosition()
			},
			RightHand = new HandData {
				Position = _cachedHands[(int)HandType.Right].GetHoldWorldPosition()
			}
		};
	}

	// 更新上次网络发包状态
	private void UpdateStateCache() {
		_lastPosition = _cachedPlayer.transform.position;
		_lastRotation = _cachedPlayer.transform.rotation;
		_lastLeftHandPosition = _cachedHands[(int)HandType.Left].GetHoldWorldPosition();
		_lastRightHandPosition = _cachedHands[(int)HandType.Right].GetHoldWorldPosition();
	}

	// 重设上次网络发包状态
	private void ResetStateCache() {
		_lastPosition = Vector3.zero;
		_lastRotation = Quaternion.identity;
		_lastLeftHandPosition = Vector3.zero;
		_lastRightHandPosition = Vector3.zero;
	}
	#endregion

	#region 工具方法
	/// 优化版的旋转相似性检查(避免Quaternion.Angle的开方运算)
	private bool IsRotationSimilar(Quaternion a, Quaternion b, float thresholdDegrees) {
		// 使用点积判断,比Quaternion.Angle更快
		float cosThreshold = Mathf.Cos(thresholdDegrees * Mathf.Deg2Rad * 0.5f);
		float dot = Mathf.Abs(Quaternion.Dot(a, b));
		return dot > cosThreshold;
	}

	// 触发传送事件
	public void TriggerTeleport() {
		_teleportCooldownTimer.Reset();
	}
	#endregion

}

