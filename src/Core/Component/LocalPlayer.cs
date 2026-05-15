using Steamworks.Data;
using System;
using UnityEngine;
using UnityEngine.XR;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.NetWork;
using WKMPMod.Util;
using static ENT_Player;
using static WKMPMod.Data.MPWriterPool;
using Quaternion = UnityEngine.Quaternion;
using Vector3 = UnityEngine.Vector3;

namespace WKMPMod.Component;

//仅获取本地玩家信息并触发事件给其他系统使用
//仅在联机时创建一个实例
public class LocalPlayer : MonoSingleton<LocalPlayer> {
	private const float POSITION_CHANGE_THRESHOLD_SQR = 0.0025f; // 0.05单位的平方
	private const float ROTATION_CHANGE_THRESHOLD_DEG = 0.5f;    // 最小旋转角度

	// 网络发送控制
	public bool ShouldSendData { get; set; } = false;  // 改为属性,更清晰

	// 玩家标识
	public ulong UserId { get; private set; }          // 本地玩家SteamID
	public string FactoryId { get; set; }   // 预制体工厂ID
	public string DefaulFactoryId { get; set; } = "default"; // 默认工厂ID,如果没有指定工厂ID则使用这个

	// 状态缓存
	private Vector3 _lastPosition;
	private Quaternion _lastRotation;
	private Vector3 _lastLeftHandPosition;
	private Vector3 _lastRightHandPosition;

	// 定时器
	private TickTimer _sendDataTimer;//本地玩家数据频率器, 定时发送玩家数据
	private TickTimer _teleportCooldownTimer;//传输状态定时器, 期间内传送标记为真
	private TickTimer _minUpdateFrequencyTimer = new TickTimer(10.0f);//最小更新频率定时器

	// 缓存引用
	private ENT_Player _cachedPlayer;
	private ENT_Player.Hand[] _cachedHands;

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
	#region[初始化方法]

	// 初始化定时器
	private void InitializeTimers() {
		_sendDataTimer = new TickTimer(MPConfig.DataSendFrequency);  // 20Hz
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
	public void Initialize(ulong userId,string factoryId) {

		UserId = userId;
		DefaulFactoryId = factoryId;
		FactoryId = factoryId;
		ResetStateCache();
	}

	#endregion

	#region[核心逻辑]

	// 尝试发送本地玩家数据
	private void TrySendLocalPlayerData() {
		// 频率限制
		if (!_sendDataTimer.TryTick())
			return;

		// 验证玩家引用
		if (!ValidatePlayerReferences())
			return;

		// 如果玩家死亡则不发送数据
		if (_cachedPlayer.IsDead())
			return;

		// 尝试创建玩家数据
		if (!TryCreateLocalPlayerData(out PlayerData playerData))
			return;

		// 设置传送标记(传送冷却期间标记为传送)
		playerData.IsTeleport = !_teleportCooldownTimer.IsTickReached;

		// 获取数据写入器
		var writer = GetWriter(MPSteamworks.Instance.UserSteamId, MPProtocol.BroadcastId, PacketType.PlayerDataUpdate);
		// 进行数据写入
		writer.Put(playerData);
		// 触发Steam数据发送 广播所有人 (转为byte[] 使用不可靠+立即发送)
		MPSteamworks.Instance.Broadcast(writer, SendType.Unreliable | SendType.NoNagle);
	}

	// 尝试创建本地玩家数据
	public bool TryCreateLocalPlayerData(out PlayerData data) {
		data = default;

		// 获取当前实际数据
		Vector3 currentPos = _cachedPlayer.transform.position;
		Quaternion currentRot = _cachedPlayer.transform.rotation;
		Vector3 currentLHand = _cachedHands[(int)HandType.Left].GetHoldWorldPosition();
		Vector3 currentRHand = _cachedHands[(int)HandType.Right].GetHoldWorldPosition();

		bool isKeepAliveTick = _minUpdateFrequencyTimer.TryTick();
		// 检查是否有显著变化:位置,旋转,手部位置任一超过阈值都视为有变化
		bool hasChanged = ((currentPos - _lastPosition).sqrMagnitude >= POSITION_CHANGE_THRESHOLD_SQR)
			|| !IsRotationSimilar(currentRot, _lastRotation, ROTATION_CHANGE_THRESHOLD_DEG)
			|| ((currentLHand - _lastLeftHandPosition).sqrMagnitude >= POSITION_CHANGE_THRESHOLD_SQR)
			|| ((currentRHand - _lastRightHandPosition).sqrMagnitude >= POSITION_CHANGE_THRESHOLD_SQR);

		if (isKeepAliveTick || hasChanged) {
			// 创建数据包
			data = new PlayerData {
				playId = UserId,
				TimestampTicks = DateTime.UtcNow.Ticks,
				Position = currentPos,
				Rotation = currentRot,
				LeftHand = new PlayerData.HandData { Position = currentLHand },
				RightHand = new PlayerData.HandData { Position = currentRHand }
			};

			// 如果是因为位移触发的发送，重置保底计时器，避免短时间内重复发送
			if (hasChanged) {
				_minUpdateFrequencyTimer.Reset();
			}

			// 更新缓存并返回
			_lastPosition = currentPos;
			_lastRotation = currentRot;
			_lastLeftHandPosition = currentLHand;
			_lastRightHandPosition = currentRHand;
			return true;
		}

		return false;
	}

	/// <summary>
	/// 强制向指定目标发送一次当前位置数据
	/// 此方法不检查位移阈值,不重置发送定时器,使用可靠传输
	/// </summary>
	/// <param name="targetId">目标玩家的 SteamId</param>
	public void ForceSyncToTarget(ulong targetId) {
		// 验证玩家引用是否有效
		if (!ValidatePlayerReferences()) return;

		// 这里不更新 _lastPosition 等缓存,以免干扰正常频率的阈值判定
		PlayerData forcedData = new PlayerData {
			playId = UserId,
			TimestampTicks = DateTime.UtcNow.Ticks,
			Position = _cachedPlayer.transform.position,
			Rotation = _cachedPlayer.transform.rotation,
			LeftHand = new PlayerData.HandData {
				Position = _cachedHands[(int)HandType.Left].GetHoldWorldPosition()
			},
			RightHand = new PlayerData.HandData {
				Position = _cachedHands[(int)HandType.Right].GetHoldWorldPosition()
			},
			// 即使正在冷却，强制同步通常也视为某种状态对齐，可根据需求设为 true 或保持逻辑
			IsTeleport = !_teleportCooldownTimer.IsTickReached
		};

		var writer = GetWriter(MPSteamworks.Instance.UserSteamId, targetId, PacketType.PlayerDataUpdate);

		// 使用高性能写入
		writer.Put(forcedData);

		MPSteamworks.Instance.SendToPeer(targetId, writer, SendType.Reliable);
	}

	#endregion

	#region[辅助函数]

	// 验证或获取玩家引用
	private bool ValidatePlayerReferences() {
		if (_cachedPlayer == null) {
			_cachedPlayer = ENT_Player.GetPlayer();
			if (_cachedPlayer == null) {
				MPMain.LogError(Localization.Get("LocalPlayer.DataAcquisitionException"));
				return false;
			}
			_cachedHands = _cachedPlayer.hands;
		}

		if (_cachedHands == null || _cachedHands.Length < 2) {
			MPMain.LogError(Localization.Get("LocalPlayer.HandDataAcquisitionException"));
			return false;
		}

		return true;
	}

	// 检查是否有显著变化
	private bool HasSignificantChanges(Vector3 pos, Quaternion rot, Vector3 lHand, Vector3 rHand) {
		if ((pos - _lastPosition).sqrMagnitude >= POSITION_CHANGE_THRESHOLD_SQR) return true;
		if (!IsRotationSimilar(rot, _lastRotation, ROTATION_CHANGE_THRESHOLD_DEG)) return true;
		if ((lHand - _lastLeftHandPosition).sqrMagnitude >= POSITION_CHANGE_THRESHOLD_SQR) return true;
		if ((rHand - _lastRightHandPosition).sqrMagnitude >= POSITION_CHANGE_THRESHOLD_SQR) return true;
		return false;
	}

	// 更新上次网络发包状态
	private void UpdateStateCache(Vector3 pos, Quaternion rot, Vector3 lHand, Vector3 rHand) {
		_lastPosition = pos;
		_lastRotation = rot;
		_lastLeftHandPosition = lHand;
		_lastRightHandPosition = rHand;
	}

	// 重设上次网络发包状态
	private void ResetStateCache() {
		_lastPosition = Vector3.zero;
		_lastRotation = Quaternion.identity;
		_lastLeftHandPosition = Vector3.zero;
		_lastRightHandPosition = Vector3.zero;
	}
	#endregion

	#region[工具函数]
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

