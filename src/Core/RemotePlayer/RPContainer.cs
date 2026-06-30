using Steamworks;
using System;
using System.Collections.Generic;
using System.Linq;
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
	public const float PLAYER_CHARACTER_CONTROLLER_HEIGHT = 2.25f;// 玩家本体胶囊高度
	public const float PLAYER_CHARACTER_CONTROLLER_RADIUS = 0.45f;// 玩家本体胶囊半径

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

	private Dictionary<string, string> _memberData = new();

	// 死亡后0.5秒内不接受更新, 避免瞬移和动画冲突
	private bool _isDead = false;
	private TickTimer _deathTick = new TickTimer(0.5f);

	// 玩家模型数据
	public string prefabId => _pending.prefabId;
	public Color32 PlayerColor { get; private set; } = new Color32(255, 255, 255, 255);

	public string team;     // 队伍信息, 默认为 "default", 可以通过玩家数据更新
	public FlattenedRule actionRule;    // 相关规则引用

	// 待应用数据 —— 在模型尚未加载时先缓存起来, 模型就绪后统一应用
	private struct PendingData {
		public string prefabId; // 模型数据
		public Color32? color;  // 主色调
		public float? scaleHeight;  // 缩放高度
		public float? scaleRadius;  // 缩放半径
		public string playerName;   // 自定义名字
		public string team;         // 所在队伍
		public string joinMessage;  // 加入信息
		public string leaveMessage; // 离开信息
	}
	private PendingData _pending;

	// 标记模型是否已经就绪(Initialize 成功)
	public bool IsModelReady { get; private set; } = false;

	#region[RAII函数]

	// 构造函数 - 只设置基本信息
	public RPContainer(IDType playerId) {
		PlayerId = playerId;
		PlayerName = new Friend(PlayerId).Name;
		_pending.playerName = PlayerName;
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
			IsModelReady = true;
			// 将缓存的待应用数据一次性应用到模型
			FlushPendingData();
			// Debug
			MPMain.LogInfo(Localization.Get("RPContainer.MappingSucceeded", PlayerId.ToString()));
			return true;
		} catch (Exception ex) {
			// Debug
			MPMain.LogError(Localization.Get("RPContainer.MappingFailed", PlayerId.ToString(), ex.Message));

			if (PlayerObject != null) Object.Destroy(PlayerObject);
			PlayerObject = null;
			IsModelReady = false;

			return false;
		}
	}

	/// <summary>
	/// 只销毁模型 GameObject 及相关组件引用, 保留玩家身份和待应用数据
	/// 用于换皮(prefabId 变更)时的中间状态
	/// </summary>
	public void DestroyModel() {
		// 把当前已知的非默认状态回存到 pending, 确保重建后仍能恢复
		if (IsModelReady) {
			_pending.color = PlayerColor;
			// scale 无法从 localScale 反推原始语义值, 依赖调用方在换皮前再次传入
		}

		PlayerObject = null;
		_remotePlayer = null;
		_remoteLeftHand = null;
		_remoteRightHand = null;
		_remoteTag = null;
		RemoteEntities = null;
		_objectTaggers = null;
		_colliders = null;
		IsModelReady = false;
		_isDead = false;
	}

	/// <summary>
	/// 模型 + 容器引用全部清空(玩家离开时使用)
	/// </summary>
	public void Destroy() {
		DestroyModel();
		// 清空 pending, 防止内存泄漏
		_pending = default;
	}

	#endregion

	#region[待应用数据写入接口]
	/// <summary>
	/// 读取并清除缓存的加入消息(RPManager 在合适时机调用)
	/// </summary>
	public string ConsumeJoinMessage() {
		var msg = _pending.joinMessage;
		_pending.joinMessage = null;
		return msg;
	}

	/// <summary>
	/// 读取离开消息(不清除, 玩家离线时 RPManager 读取后连同容器一起销毁)
	/// </summary>
	public string GetLeaveMessage() => _pending.leaveMessage;

	/// <summary>
	/// 从 MemberData 字典批量写入所有字段。
	/// 返回是否收到了新的 prefabId（供 RPManager 决定是否重建模型）。
	/// </summary>
	public bool ApplyMemberData(Dictionary<string, string> data) {
		var changes = data
			.Where(kvp => !_memberData.TryGetValue(kvp.Key, out var oldValue) || oldValue != kvp.Value)
			.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

		_memberData = data;

		if (changes.TryGetValue(MPKeys.PLAYER_NAME, out var name)) {
			_pending.playerName = name;
			if (IsModelReady) ApplyName(name);
		}

		if (changes.TryGetValue(MPKeys.PLAYER_COLOR, out var colorStr)
			&& MPUtil.TryParsePlayerColor(colorStr, out var color)) {

			_pending.color = color;
			if (IsModelReady) ApplyColor(color);
		}

		if (changes.TryGetValue(MPKeys.PLAYER_SCALE, out var scaleStr)) {
			var args = scaleStr.Split(',');
			if (args.Length >= 2
				&& float.TryParse(args[0], out var height)
				&& float.TryParse(args[1], out var radius)) {

				_pending.scaleHeight = height;
				_pending.scaleRadius = radius;
				if (IsModelReady) ApplyScale(height, radius);
			}
		}

		if (changes.TryGetValue(MPKeys.TEAM, out var team)) {
			_pending.team = team;
			if (IsModelReady) ApplyTeam(team);
		}

		if (changes.TryGetValue(MPKeys.JOIN_MESSAGE, out var join))
			_pending.joinMessage = join;

		if (changes.TryGetValue(MPKeys.LEAVE_MESSAGE, out var leave))
			_pending.leaveMessage = leave;

		// prefabId 最后处理，返回"是否需要重建模型"给 Manager 判断
		if (changes.TryGetValue(MPKeys.PREFAB_ID, out var pid)) {
			_pending.prefabId = pid;
			return true;
		}
		return false;
	}

	#endregion

	#region[内部数据应用]

	/// <summary>
	/// 模型就绪后, 将所有缓存数据一次性刷入
	/// </summary>
	private void FlushPendingData() {
		if (_pending.playerName != null) ApplyName(_pending.playerName);
		if (_pending.color.HasValue) ApplyColor(_pending.color.Value);
		if (_pending.scaleHeight.HasValue && _pending.scaleRadius.HasValue)
			ApplyScale(_pending.scaleHeight.Value, _pending.scaleRadius.Value);
		if (_pending.team != null) ApplyTeam(_pending.team);
		// joinMessage / leaveMessage 不在此处消费, 由 RPManager 主动读取
	}

	private void ApplyColor(Color32 color) {
		PlayerColor = new Color32(color.r, color.g, color.b, 255);
		RPFactoryManager.Instance.ApplyPlayerColor(prefabId, PlayerObject, PlayerColor);
	}

	private void ApplyScale(float height, float radius) {
		PlayerObject.transform.localScale = new Vector3(
			radius / PLAYER_CHARACTER_CONTROLLER_RADIUS,
			height / PLAYER_CHARACTER_CONTROLLER_HEIGHT,
			radius / PLAYER_CHARACTER_CONTROLLER_RADIUS
		);
	}

	private void ApplyName(string name) {
		PlayerName = name;
		if (_remoteTag != null) _remoteTag.PlayerName = PlayerName;
	}

	private void ApplyTeam(string newTeam) {
		string normalized = newTeam.Trim().ToLower();
		if (team == normalized) return;
		team = normalized;
		RefreshRuleReference();
	}

	#endregion

	#region[组件初始化]

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
			if (child.TryGetComponent<RPContainerRef>(out var containerRef))
				containerRef.container = this;
			else
				child.gameObject.AddComponent<RPContainerRef>().container = this;
		}
	}

	// 初始化远程实体组件数据
	private void InitializeAllComponentData() {
		// 标签组件初始化命名
		_remoteTag.Initialize(PlayerId, PlayerName);
		// 实体标签赋予玩家Id
		if (RemoteEntities != null)
			foreach (var entity in RemoteEntities)
				entity.playerId = PlayerId;

		_remotePlayer?.playerId = PlayerId;
		_remoteLeftHand?.playerId = PlayerId;
		_remoteRightHand?.playerId = PlayerId;
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
			_remoteLeftHand.TeleportToPosition(playerData.LeftHand.Position);
			_remoteRightHand.TeleportToPosition(playerData.RightHand.Position);
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
		ICustomModelExtension extension = RPFactoryManager.GetExtension(prefabId);
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

	#endregion

	#region[队伍/规则函数]

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
		if (_colliders != null)
			foreach (var col in _colliders)
				if (!col.isTrigger)// 只处理非触发器的实体碰撞体
					col.enabled = actionRule.collision;// true 开启碰撞, false 关闭碰撞
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

