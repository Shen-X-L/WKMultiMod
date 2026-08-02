using HarmonyLib;
using Steamworks.Data;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.NetWork;
using WKMPMod.RemotePlayer;
using WKMPMod.Util;
using static ENT_Player;
using static WKMPMod.Data.MPWriterPool;
using Quaternion = UnityEngine.Quaternion;
using Vector3 = UnityEngine.Vector3;

namespace WKMPMod.Component;

//仅获取本地玩家信息并触发事件给其他系统使用
//仅在联机时创建一个实例
public class LocalPlayer : MonoSingleton<LocalPlayer> {

	#region[字段和属性 - 更新数据状态缓存]

	// 状态缓存
	private PlayerData _lastPlayerData;
	private string[] handItemPrefabNames = new string[2];
	private List<IDType> _farPlayersBuffer = new List<IDType>(16);
	public List<IDType> _nearPlayersBuffer = new List<IDType>(16);  // 近处玩家,大部分数据可以仅对near发送
	public Dictionary<string, string> _playerData = new();  // 玩家额外数据字典(背包状态 perk状态等)
	public const float LIMIT_SENDING_DISTANCE = 2400.0f;             // 超过该距离时仅保证最小更新频率发送数据

	#endregion

	#region[字段和属性 - 本地其他数据]

	// 玩家标识
	public IDType UserId { get; private set; }          // 本地玩家SteamID
	public string FactoryId { get; set; }// 预制体工厂ID
	public Color32 PlayerColor { get; private set; }

	#endregion

	#region[字段和属性 - 玩家间交互]

	// 状态存储: 谁正在对本地玩家施加交互
	private readonly HashSet<IDType> _playersGrabbingMe = new();    // 被该Id的玩家拖拽
	private readonly HashSet<IDType> _playersHangingMe = new();   // 被该Id的玩家抓取

	#endregion

	#region[字段和属性 - 定时器]

	// 定时器
	private TickTimer _sendDataTimer;//本地玩家数据频率器, 定时发送玩家数据
	private TickTimer _teleportCooldownTimer;//传输状态定时器, 期间内传送标记为真
	private TickTimer _minUpdateFrequencyTimer = new TickTimer(10.0f, true);//最小更新频率定时器

	#endregion

	#region[字段和属性 - 缓存引用]

	// 缓存引用
	private ENT_Player _cachedPlayer;
	private Hand[] _cachedHands;

	#endregion

	#region[字段和属性 - 其他]

	// 网络发送控制
	public bool ShouldSendData { get; set; } = false;

	private static readonly AccessTools.FieldRef<Item, HandItem> _handItemField =
		AccessTools.FieldRefAccess<Item, HandItem>("handItem");

	#endregion

	#region[Unity生命周期函数]

	public void Start() {
		InitializeTimers();
		CachePlayerReferences();
		MPEventBusGame.OnRemoteHangStateChanged += HandleRemoteHangChanged;
		MPEventBusGame.OnRemoteGrabStateChanged += HandleRemoteGrabChanged;
	}

	public void Update() {
		// 不需要发送时停止更新, 该值由 联机管理类 控制
		if (!ShouldSendData) return;
		// 发送本地玩家数据
		TrySendLocalPlayerData();
	}

	protected override void OnDestroy() {
		base.OnDestroy();
		// 组件销毁时必须注销事件, 防止内存泄漏
		MPEventBusGame.OnRemoteHangStateChanged -= HandleRemoteHangChanged;
		MPEventBusGame.OnRemoteGrabStateChanged -= HandleRemoteGrabChanged;
	}
	#endregion

	#region[初始化方法]

	// 初始化定时器
	private void InitializeTimers() {
		_sendDataTimer = new TickTimer(MPConfig.DataSendFrequency, true);  // 20Hz
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
	public void Initialize(IDType userId, string factoryId, Color32 playerColor) {
		UserId = userId;
		FactoryId = factoryId;
		PlayerColor = playerColor;
		_lastPlayerData = default;

		_playersGrabbingMe.Clear();
		_playersHangingMe.Clear();
	}

	#endregion

	#region[网络事件回调]

	/// <summary>
	/// 被其他玩家拖拽
	/// </summary>
	private void HandleRemoteHangChanged(IDType remoteId, bool isActive) {
		if (isActive) _playersHangingMe.Add(remoteId);
		else _playersHangingMe.Remove(remoteId);
	}

	/// <summary>
	/// 被其他玩家抓住
	/// </summary>
	private void HandleRemoteGrabChanged(IDType remoteId, bool isActive) {
		if (isActive) _playersGrabbingMe.Add(remoteId);
		else _playersGrabbingMe.Remove(remoteId);
	}

	#endregion

	#region[数据发送核心逻辑]

	/// <summary>
	/// 尝试发送本地玩家数据 (结合距离剔除算法)
	/// 接收函数: <see cref="MPPacketHandlers.HandlePlayerDataUpdate"/>
	/// </summary>
	private void TrySendLocalPlayerData() {
		bool tickNormal = _sendDataTimer.TryTick();
		bool tickMinFreq = _minUpdateFrequencyTimer.TryTick();

		// 如果没有任何定时器触发, 返回
		if (!tickNormal && !tickMinFreq) return;

		if (!ValidatePlayerReferences()) return;

		if (_cachedPlayer.IsDead()) return;

		// 瞬移补偿判定
		float dx = _cachedPlayer.transform.position.x - _lastPlayerData.PosX;
		float dy = _cachedPlayer.transform.position.y - _lastPlayerData.PosY;
		float dz = _cachedPlayer.transform.position.z - _lastPlayerData.PosZ;

		// 检查是否发生大于50米的位移 (50 * 50 = 2500)
		if (_lastPlayerData.TimestampTicks != 0 && (dx * dx + dy * dy + dz * dz) >= 2500.0f) {
			// 强制本帧作为保底帧发送, 确保原本在近处(现在变成远处)的玩家A能收到这个离去包
			tickMinFreq = true;
			_minUpdateFrequencyTimer.Reset(); // 重新开始计算10秒
		}

		// 如果没有显著变换 && 不强制发送, 返回
		if (!CheckLocalPlayerUpdates(tickMinFreq))
			return;

		// 获取距离分层列表 (将本地玩家当前位置作为中心点)
		// 2400m/玩家数量, 最小100m, 远距离玩家仅保证最小频率发送数据
		RPManager.Instance.GetPlayersByDistance(
			_lastPlayerData.Position,
			Math.Max(LIMIT_SENDING_DISTANCE/RPManager.Instance.Players.Count,100.0f),
			ref _farPlayersBuffer,
			ref _nearPlayersBuffer
		);


		var writer = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.PlayerDataUpdate);
		writer.Put(_lastPlayerData);
		writer.Put(false);

		// 发送给近距离玩家 常规频率定时器 && 位置发生了变化, 或者保底更新频率到了
		if (tickNormal) {
			foreach (ulong targetId in _nearPlayersBuffer) {
				MPSteamworks.Instance.SendToPeer(targetId, writer, SendType.Unreliable | SendType.NoNagle);
			}
		}

		var writerFreq = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.PlayerDataUpdate);
		writerFreq.Put(_lastPlayerData);
		writerFreq.Put(true);
		writerFreq.Put(PlayerDataDic());

		// 发送给远距离玩家 仅在最小频率定时器(如10秒一次)触发时发送
		if (tickMinFreq) {
			foreach (ulong targetId in _nearPlayersBuffer)
				MPSteamworks.Instance.SendToPeer(targetId, writerFreq);
			foreach (ulong targetId in _farPlayersBuffer)
				MPSteamworks.Instance.SendToPeer(targetId, writerFreq);
		}
	}

	// 获取玩家数据, 更新缓存状态, 并返回是否发生了变化
	public bool CheckLocalPlayerUpdates(bool forceUpdate) {
		GetHandData(0, forceUpdate, out var currentLHand);
		GetHandData(1, forceUpdate,out var currentRHand);

		var data = new PlayerData {
			playId = UserId,
			TimestampTicks = DateTime.UtcNow.Ticks,
			Position = _cachedPlayer.transform.position,
			Rotation = _cachedPlayer.transform.rotation,
			LeftHand = currentLHand,
			RightHand = currentRHand,
			IsTeleport = !_teleportCooldownTimer.IsTickReached
		};

		// 获取是否改变, 如果改变, 更新旧坐标
		if (_lastPlayerData.UpdateIfChanged(data, forceUpdate)) {
			return true;
		}

		return false;
	}

	/// <summary>
	/// 获取手部数据
	/// </summary>
	/// <param name="handIndex">手部索引</param>
	/// <param name="forceUpdate">是否强制更新</param>
	/// <param name="data">返回数据</param>
	public void GetHandData(int handIndex, bool forceUpdate, out PlayerData.HandData data) {
		ENT_Player.Hand hand = handIndex == 0 ? _cachedHands[0] : _cachedHands[1];

		// 获取坐标
		var currentItem = hand.inventoryHand.currentItem;
		Transform itemTransform = currentItem != null ? _handItemField(currentItem)?.transform : null;
		Vector3 position = itemTransform?.position ?? hand.GetHoldWorldPosition();

		data = new PlayerData.HandData {
			Position = position,
		};

		IDType targetRemoteId = 0;

		switch (hand.interactState) {
			// 无抓取时, 带有持有物预制体ID
			case InteractType.none: {
				var itemPrefabName = hand.inventoryHand.currentItem?.prefabName ?? RemoteHand.NONE_ITEM_NAME;

				if (handItemPrefabNames[handIndex] != itemPrefabName || forceUpdate) {
					data.handItemUpdate = true;
					data.itemPrefabName = itemPrefabName;
				} else {
					data.handItemUpdate = false;
				}

				data.interactState = (byte)InteractType.none;
				break;
			}
			// 拖拽玩家时, 带有被抓玩家ID和目标点
			case InteractType.grab: {
				if (hand.grabTarget?.gameObject.TryGetComponent<RPContainerRef>(out var parent) == true) {
					targetRemoteId = parent.container.PlayerId;
					if (targetRemoteId != 0 && _playersGrabbingMe.Contains(targetRemoteId)) {
						data.interactState = (byte)InteractType.none;
						data.itemPrefabName = RemoteHand.NONE_ITEM_NAME;
						ReleaseAndRepelLocalHand(handIndex, targetRemoteId);
					} else {
						data.interactState = (byte)InteractType.grab;
						data.targetId = targetRemoteId;
						data.DesiredPosition = _cachedPlayer.camTransform.position + _cachedPlayer.camTransform.forward * 1.8f;
					}
				} else {
					data.interactState = (byte)InteractType.grab;
				}
				break;
			}
			// 抓取玩家时, 带有被抓玩家ID
			case InteractType.hanging: {
				if (hand.handhold?.gameObject.TryGetComponent<RPContainerRef>(out var parent) == true) {
					targetRemoteId = parent.container.PlayerId;
					if (targetRemoteId != 0 && _playersHangingMe.Contains(targetRemoteId)) {
						data.interactState = (byte)InteractType.none;
						data.itemPrefabName = RemoteHand.NONE_ITEM_NAME;
						ReleaseAndRepelLocalHand(handIndex, targetRemoteId);
					} else {
						data.interactState = (byte)InteractType.hanging;
						data.targetId = targetRemoteId;
					}
				} else {
					data.interactState = (byte)InteractType.hanging;
				}
				break;
			}
			default: {
				data.interactState = (byte)hand.interactState;
				break;
			}
		}
	}

	// 玩家数据字典(背包物品,是否携带道具等)
	public Dictionary<string, string> PlayerDataDic() {
		_playerData.Clear();
		foreach (var (item, count) in InventoryManager.GetInventoryItems(checkBag:true, checkHands: false, checkPouches: true)) {
			_playerData[item] = count.ToString();
		}

		var playerCharacter = _cachedPlayer.GetComponent<CharacterController>();
		var height = _cachedPlayer.transform.localScale.y * playerCharacter.height;
		var radius = Math.Sqrt(playerCharacter.transform.localScale.x * playerCharacter.transform.localScale.z) * playerCharacter.radius;
		_playerData[MPKeys.PLAYER_SCALE] = $"{height},{radius}";

		return _playerData;
	}

	#endregion

	#region[辅助函数]

	// 验证或获取玩家引用
	private bool ValidatePlayerReferences() {
		if (_cachedPlayer == null) {
			_cachedPlayer = GetPlayer();
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

	#endregion

	#region[工具函数]

	// 触发传送事件
	public void TriggerTeleport() {
		_teleportCooldownTimer.Reset();
	}

	public void ReleaseAndRepelLocalHand(int handIndex, IDType targetRemoteId) {
		_cachedPlayer.StopInteraction(handIndex);
		_cachedPlayer.AddForce(-_cachedPlayer.camTransform.forward, "RepelByRemote");
		MPEventBusGame.NotifyPlayerStopInteraction(targetRemoteId);
	}


	#endregion

	#region[API函数]

	public static bool IsHoldingMe(IDType targetRemoteId) {
		if (Instance._cachedPlayer == null) return false;
		return Instance._playersGrabbingMe.Contains(targetRemoteId) || Instance._playersHangingMe.Contains(targetRemoteId);
	}

	public void SetPlayerColor(Color32 color) {
		PlayerColor = color;
	}

	#endregion
}

