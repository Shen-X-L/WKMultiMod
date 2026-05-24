using Steamworks;
using System;
using UnityEngine;
using UnityEngine.Events;
using WKMPMod.Asset;
using WKMPMod.Component;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.Util;
using static Steamworks.InventoryItem;
using Object = UnityEngine.Object;

namespace WKMPMod.RemotePlayer;

// 单个玩家的容器类
public class RPContainer {
	public ulong PlayerId { get; set; }
	public string PlayerName { get; set; }
	public GameObject PlayerObject { get; private set; }

	// 本体组件
	private Component.RemotePlayer _remotePlayer;
	private RemoteHand _remoteLeftHand;
	private RemoteHand _remoteRightHand;
	private RemoteTag _remoteTag;
	private RemoteEntity[] _remoteEntities;

	// 死亡后0.5秒内不接受更新, 避免瞬移和动画冲突
	private bool _isDead = false;
	private TickTimer _deathTick = new TickTimer(0.5f);

	// 玩家模型信息数据
	public string prefabId;

	public PlayerData PlayerData {
		get {
			var data = new PlayerData {
				playId = this.PlayerId,
				TimestampTicks = DateTime.UtcNow.Ticks,
				IsTeleport = true,
			};
			data.Position = PlayerObject.transform.position;
			data.Rotation = PlayerObject.transform.rotation;

			data.LeftHand = new PlayerData.HandData { };
			data.RightHand = new PlayerData.HandData { };
			return data;
		}
	}

	// 构造函数 - 只设置基本信息
	public RPContainer(ulong playId, string prefabId) {
		PlayerId = playId;
		PlayerName = new Friend(PlayerId).Name;
		this.prefabId = prefabId;
	}

	// 新初始化方法
	public bool Initialize(GameObject playerInstance, Transform persistentParent = null) {
		if (playerInstance == null) return false;
		try {
			// 创建对象引用并重命名
			PlayerObject = playerInstance;
			PlayerObject.name = $"RemotePlayer_{PlayerName}_{PlayerId}";
			// 设置持久化
			if (persistentParent != null) {
				PlayerObject.transform.SetParent(persistentParent, false);
			}
			// 组件初始化
			InitializeAllComponent(PlayerObject);
			InitializeAllComponentData();
			// 设为原点
			var temp = new PlayerData {
				IsTeleport = true,
				Position = new Vector3(0, -2, 0),
			};
			HandlePlayerData(ref temp);
			// 直接送去视野外进行隐藏
			PlayerObject.transform.position = new Vector3(0, -9999f, 0);
			// Debug
			MPMain.LogInfo(Localization.Get("RPContainer.MappingSucceeded", PlayerId.ToString()));
			return true;
		} catch (Exception ex) {
			// Debug
			MPMain.LogError(Localization.Get("RPContainer.MappingFailed", PlayerId.ToString(), ex.Message));

			if (PlayerObject != null) Object.Destroy(PlayerObject);

			return false;
		}
	}

	#region[新创建组件函数]

	// 初始化远程实体组件引用
	public void InitializeAllComponent(GameObject instance) {
		_remotePlayer = instance.GetComponentInChildren<Component.RemotePlayer>();
		_remoteTag = instance.GetComponentInChildren<RemoteTag>();
		_remoteEntities = instance.GetComponentsInChildren<RemoteEntity>();

		// 处理左右手:获取所有 RemoteHand,然后通过内部字段区分
		RemoteHand[] hands = instance.GetComponentsInChildren<RemoteHand>();
		foreach (var hand in hands) {
			if (hand.hand == HandType.Left) _remoteLeftHand = hand;
			else if (hand.hand == HandType.Right) _remoteRightHand = hand;
		}
	}

	// 初始化远程实体组件数据
	private void InitializeAllComponentData() {
		// 标签组件初始化命名
		_remoteTag.Initialize(PlayerId, PlayerName);
		// 实体标签赋予玩家Id
		if (_remoteEntities != null) {
			foreach (var entity in _remoteEntities) {
				entity.playerId = PlayerId;
			}
		}
	}

	#endregion

	#region[新对象清理函数]

	// 销毁方法 - 清理所有资源
	public void Destroy() {
		// 清理引用
		PlayerObject = null;
		_remotePlayer = null;
		_remoteLeftHand = null;
		_remoteRightHand = null;
		_remoteTag = null;
		_remoteEntities = null;
	}

	#endregion

	#region[数据更新]

	/// <summary>
	/// 通过数据进行位置更新
	/// </summary>
	public void HandlePlayerData(ref PlayerData playerData) {
		// 死亡后0.5秒内不接受更新, 避免瞬移和动画冲突
		if (_isDead && !_deathTick.IsTickReached) {
			return;
		}

		if (_isDead && _deathTick.IsTickReached) {
			PlayerObject.SetActive(true);
			_isDead = false;
		}

		if (playerData.IsTeleport) {
			// 使用组件的传送方法
			_remotePlayer.Teleport(playerData.Position, playerData.Rotation);

			Vector3 leftTarget = playerData.LeftHand.Position;
			_remoteLeftHand.Teleport(leftTarget);

			Vector3 rightTarget = playerData.RightHand.Position;
			_remoteRightHand.Teleport(rightTarget);
		} else {
			// 使用插值更新
			_remotePlayer.UpdateFromPlayerData(playerData.Position, playerData.Rotation);
			_remoteLeftHand.UpdateFromHandData(ref playerData.LeftHand);
			_remoteRightHand.UpdateFromHandData(ref playerData.RightHand);
		}
	}

	/// <summary>
	/// 通过数据进行头部文字更新
	/// </summary>
	public void HandleNameTag(string text) {
		if (string.IsNullOrEmpty(text)) { return; }
		if (_remoteTag == null) {
			MPMain.LogError(Localization.Get("RPContainer.NameTagComponentMissing"));
			return;
		}
		_remoteTag.Message = text;
		return;
	}

	/// <summary>
	/// 处理死亡 - 目前仅隐藏对象,后续可以添加死亡动画等
	/// </summary>
	public void HandleDeath() {
		// 生成死亡特效
		var playerPosition = PlayerObject.transform.position;
		var playerRotation = PlayerObject.transform.rotation;
		// 动态获取当前模型的死亡特效配置
		ICustomModelExtension extension = RPFactoryManager.GetExtension(this.prefabId);
		string effectName = extension?.DeathEffectAssetName ?? MPAssetManager.DEATH_OBJECT_NAME;

		GameObject deathParticle = MPAssetManager.GetAssetGameObject(effectName)
								?? CL_AssetManager.GetAssetGameObject(effectName);

		if (deathParticle != null) {
			GameObject.Instantiate(deathParticle, playerPosition, playerRotation);
		}

		PlayerObject.SetActive(false);
		_isDead = true;
		// 死亡后重置死亡计时器, 1秒内不接受更新, 避免瞬移和动画冲突
		_deathTick.Reset();
	}
	#endregion
}
