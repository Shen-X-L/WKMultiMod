using Steamworks;
using System;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Events;
using WKMPMod.Asset;
using WKMPMod.Component;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.NetWork;
using WKMPMod.Util;
using static UI_TabGroup;
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
	public RemoteEntity[] RemoteEntities { get; private set; }
	private ObjectTagger[] _objectTaggers;
	private Collider[] _colliders;

	// 死亡后0.5秒内不接受更新, 避免瞬移和动画冲突
	private bool _isDead = false;
	private TickTimer _deathTick = new TickTimer(0.5f);

	// 玩家模型信息数据
	public string prefabId;
	public Color32 PlayerColor { get; private set; } = new Color32(255, 255, 255, 255);

	// 队伍信息, 默认为 "default", 可以通过玩家数据更新
	public string team;
	public FlattenedRule actionRule;
	#region[RAII函数]

	// 构造函数 - 只设置基本信息
	public RPContainer(IDType playerId, string prefabId) {
		PlayerId = playerId;
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

	#endregion
	#region[新创建组件函数]

	// 初始化远程实体组件引用
	public void InitializeAllComponent(GameObject instance) {
		_remotePlayer = instance.GetComponentInChildren<Component.RemotePlayer>();
		_remoteTag = instance.GetComponentInChildren<RemoteTag>();
		RemoteEntities = instance.GetComponentsInChildren<RemoteEntity>();
		_objectTaggers = instance.GetComponentsInChildren<ObjectTagger>();
		_colliders = instance.GetComponentsInChildren<Collider>();

		// 处理左右手:获取所有 RemoteHand,然后通过内部字段区分
		RemoteHand[] hands = instance.GetComponentsInChildren<RemoteHand>();
		foreach (var hand in hands) {
			if (hand.handType == 0) _remoteLeftHand = hand;
			else if (hand.handType == 1) _remoteRightHand = hand;
		}

		Transform[] allChildren = instance.GetComponentsInChildren<Transform>(true);

		foreach (Transform child in allChildren) {
			// 如果还没有该组件, 则添加
			if (child.TryGetComponent<RPContainerRef>(out var containerRef)) {
				containerRef.container = this;
			} else {
				var parent = child.gameObject.AddComponent<RPContainerRef>();
				parent.container = this;
			}
		}
	}

	// 初始化远程实体组件数据
	private void InitializeAllComponentData() {
		// 标签组件初始化命名
		_remoteTag.Initialize(PlayerId, PlayerName);
		// 实体标签赋予玩家Id
		if (RemoteEntities != null) {
			foreach (var entity in RemoteEntities) {
				entity.playerId = PlayerId;
			}
		}
		_remotePlayer?.playerId = PlayerId;
		_remoteLeftHand?.playerId = PlayerId;
		_remoteRightHand?.playerId = PlayerId;
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
		RemoteEntities = null;
	}

	#endregion
	#region[数据更新]

	/// <summary>
	/// 通过数据进行位置更新
	/// </summary>
	public void HandlePlayerData(ref PlayerData playerData) {
		// 死亡后0.5秒内不接受更新, 避免瞬移和动画冲突
		if (_isDead && !_deathTick.IsTickReached || PlayerObject == null || _remotePlayer == null) {
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
			_remoteLeftHand.TeleportToPosition(leftTarget);

			Vector3 rightTarget = playerData.RightHand.Position;
			_remoteRightHand.TeleportToPosition(rightTarget);
		} else {
			// 使用插值更新
			_remotePlayer.UpdateFromPlayerData(playerData.Position, playerData.Rotation);
			_remoteLeftHand.UpdateFromHandData(ref playerData.LeftHand, MPSteamworks.UserSteamId);
			_remoteRightHand.UpdateFromHandData(ref playerData.RightHand, MPSteamworks.UserSteamId);
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

		GameObject deathParticle = MPAssetManager.GetFXPrefab(effectName)
								?? CL_AssetManager.GetAssetGameObject(effectName);

		if (deathParticle != null) {
			GameObject.Instantiate(deathParticle, playerPosition, playerRotation);
		}

		PlayerObject.SetActive(false);
		_isDead = true;
		// 死亡后重置死亡计时器, 1秒内不接受更新, 避免瞬移和动画冲突
		_deathTick.Reset();
	}

	/// <summary>
	/// 更新玩家名字 - 同时更新标签组件的名字显示
	/// </summary>
	public void UpdatePlayerName(string newName) {
		PlayerName = newName;
		_remoteTag.PlayerName = PlayerName;
	}

	/// <summary>
	/// 更改玩家颜色
	/// </summary>
	public void ApplyColor(Color32 color) {
		PlayerColor = new Color32(color.r, color.g, color.b, 255);
		RPFactoryManager.Instance.ApplyPlayerColor(prefabId, PlayerObject, PlayerColor);
	}

	#endregion
	#region[队伍/规则函数]

	/// <summary>
	/// 处理队伍变更 - 更新队伍信息并刷新规则引用
	/// </summary>
	public void HandleTeamChanged(string newTeam) {
		this.team = newTeam.Trim().ToLower();
		RefreshRuleReference();
	}

	/// <summary>
	/// 刷新规则引用 - 从 TeamRuleManager 获取当前队伍的规则引用并更新本地缓存
	/// </summary>
	public void RefreshRuleReference() {
		// O(1) 抓取引用
		actionRule = TeamRuleManager.GetActiveRuleRef(team == "" ? MPKeys.DEFAULT_TEAM : team);

		// 更新名牌显示
		if (_remoteTag != null)
			_remoteTag.gameObject.SetActive(actionRule.tagShow);

		// 更新玩家间交互权限
		ChangeGrabOrHang(MPCore.IsGrabOrHangState);

		// 更新PVP权限
		foreach (var entity in RemoteEntities)
			entity.pvpEnabled = actionRule.pvp;

		// 更新碰撞权限
		foreach (var colliders in _colliders) {
			if (!colliders.isTrigger) {  // 只处理非触发器的实体碰撞体
				colliders.enabled = actionRule.collision; // true 开启碰撞, false 关闭碰撞
			}
		}
	}

	/// <summary>
	/// 处理动作权限变更 - 根据当前规则和权限状态更新玩家对象的标签组件, 以控制玩家的交互能力
	/// </summary>
	public void ChangeGrabOrHang(ENT_Player.InteractType interactType) {
		foreach (var tagger in _objectTaggers) {
			if (interactType == ENT_Player.InteractType.grab && actionRule?.grab == true)
				tagger.AddTag(MPKeys.GRAB_TAGGER);
			else
				tagger.RemoveTag(MPKeys.GRAB_TAGGER);

			if (interactType == ENT_Player.InteractType.hanging && actionRule?.hang == true)
				tagger.AddTag(MPKeys.HANGING_TAGGER);
			else
				tagger.RemoveTag(MPKeys.HANGING_TAGGER);
		}
	}

	#endregion
}

