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
	//public GameObject LeftHandObject { get; private set; }
	//public GameObject RightHandObject { get; private set; }
	//public GameObject NameTagObject { get; private set; }

	private Component.RemotePlayer _remotePlayer;
	private RemoteHand _remoteLeftHand;
	private RemoteHand _remoteRightHand;
	private RemoteTag _remoteTag;
	private RemoteEntity[] _remoteEntities;
	private int _initializationCount = 5;
	private bool _isDead = false;
	// 死亡后1秒内不接受更新, 避免瞬移和动画冲突
	private TickTimer _deathTick = new TickTimer(1f);
	public PlayerData PlayerData {
		get {
			var data = new PlayerData {
				playId = this.PlayerId,
				TimestampTicks = DateTime.UtcNow.Ticks,
				IsTeleport = true,
			};
			data.Position = PlayerObject.transform.position;
			data.Rotation = PlayerObject.transform.rotation;

			data.LeftHand = new HandData { };
			data.RightHand = new HandData { };
			return data;
		}
	}

	// 构造函数 - 只设置基本信息
	public RPContainer(ulong playId) {
		PlayerId = playId;
		PlayerName = new Friend(PlayerId).Name;
	}

	// 新初始化方法
	public bool Initialize(GameObject playerInstance, Transform persistentParent = null) {
		if (playerInstance == null) return false;
		try {
			// 创建对象引用
			PlayerObject = playerInstance;
			// 设置持久化
			if (persistentParent != null) {
				PlayerObject.transform.SetParent(persistentParent, false);
			}
			// 组件初始化
			InitializeAllComponent(PlayerObject);
			InitializeAllComponentData();
			// 设为原点
			HandlePlayerData(new PlayerData {
				IsTeleport = true,
				Position = new Vector3(0, 0, 0),
			});
			// Debug
			MPMain.LogInfo(Localization.Get(
				"RPContainer", "MappingSucceeded", PlayerId.ToString()));
			return true;
		} catch (Exception ex) {
			// Debug
			MPMain.LogError(Localization.Get(
				"RPContainer", "MappingFailed", PlayerId.ToString(), ex.Message));

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
				entity.PlayerId = PlayerId;
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
	public void HandlePlayerData(PlayerData playerData) {
		// 死亡后1秒内不接受更新, 避免瞬移和动画冲突
		if (_isDead == true && _deathTick.IsTickReached) {
			PlayerObject.SetActive(true);
			_isDead = false;
		}

		// 判断是否处于初始化 5 秒内
		if (playerData.IsTeleport || _initializationCount > 0) {
			playerData.IsTeleport = true;
			--_initializationCount;
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
			_remoteLeftHand.UpdateFromHandData(playerData.LeftHand);
			_remoteRightHand.UpdateFromHandData(playerData.RightHand);
		}
	}

	/// <summary>
	/// 通过数据进行头部文字更新
	/// </summary>
	public void HandleNameTag(string text) {
		if (string.IsNullOrEmpty(text)) { return; }
		if (_remoteTag == null) {
			MPMain.LogError(Localization.Get("RPContainer", "NameTagComponentMissing"));
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
		var deathParticle = MPAssetManager.GetAssetGameObject(MPAssetManager.DEATH_OBJECT_NAME);
		if (deathParticle != null) {
			GameObject.Instantiate(deathParticle, playerPosition, playerRotation);
		}
		PlayerObject.SetActive(false);
		_isDead = true;
		// 死亡后重置死亡计时器, 1秒内不接受更新, 避免瞬移和动画冲突
		_deathTick.Reset();
	}
	#endregion

	#region[旧工具函数]

	// 赋予可攀爬组件
	public static void AddHandHold(GameObject gameObject) {
		// 添加 ObjectTagger 组件
		ObjectTagger tagger = gameObject.AddComponent<ObjectTagger>();
		if (tagger != null) {
			tagger.tags.Add("Handhold");    //攀爬标签
		}

		// 添加 CL_Handhold 组件 (攀爬逻辑)
		CL_Handhold handholdComponent = gameObject.AddComponent<CL_Handhold>();
		if (handholdComponent != null) {
			// 添加停止和激活事件
			handholdComponent.stopEvent = new UnityEvent();
			handholdComponent.activeEvent = new UnityEvent();
		}

		// 确保 渲染器 被赋值, 否则 材质 设置会崩溃
		Renderer objectRenderer = gameObject.GetComponent<Renderer>();
		if (objectRenderer != null) {
			gameObject.GetComponent<CL_Handhold>().handholdRenderer = objectRenderer;
		}
	}

	#endregion
}
