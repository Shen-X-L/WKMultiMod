using Steamworks;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using WKMPMod.Asset;
using WKMPMod.Component;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.NetWork;
using WKMPMod.Util;
using WKMultiPlayerMod.Shared.Component;
using WKMultiPlayerMod.Shared.Data;
using Object = UnityEngine.Object;

namespace WKMPMod.RemotePlayer;

// 单个玩家的容器类
public class RPContainer {
	#region[玩家数据常量]

	public const float PLAYER_CHARACTER_CONTROLLER_HEIGHT = 2.25f;// 玩家本体胶囊高度
	public const float PLAYER_CHARACTER_CONTROLLER_RADIUS = 0.45f;// 玩家本体胶囊半径

	#endregion

	#region[玩家数据]

	public ulong PlayerId { get; set; }
	public string PlayerName { get; set; }
	public GameObject PlayerObject { get; private set; }

	#endregion

	#region[本体组件引用]

	private Component.RemotePlayer _remotePlayer;   // 本体
	private RemoteHand _remoteLeftHand;     // 左手
	private RemoteHand _remoteRightHand;    // 右手
	private RemoteTag _remoteTag;   // 头顶标签
	public RemoteEntity[] RemoteEntities { get; private set; }  // 受击组件
	private ObjectTagger[] _objectTaggers;  // 全部标签 (游戏本体交互判定控制)
	private Collider[] _colliders;  // 全部碰撞
	private CustomModelBehaviour _modelBehaviour;// 运行时模型控制器组件

	#endregion

	// 死亡后0.5秒内不接受更新, 避免瞬移和动画冲突
	private bool _isDead = false;
	private TickTimer _deathTick = new TickTimer(0.5f);

	#region[TeamRule相关]

	public string team="";     // 队伍信息, 默认为 "default", 可以通过玩家数据更新
	public FlattenedRule actionRule;    // 相关规则引用

	#endregion

	#region[MemberData相关]

	// 玩家模型数据
	public ICustomModelExtension Extension { get; private set; }
	public string prefabId => _pending.prefabId;
	private Dictionary<string, string> _memberData = new();
	// 待应用数据 —— 在模型尚未加载时先缓存起来, 模型就绪后统一应用
	private struct PendingData {
		public string prefabId; // 模型数据
		public Color32? color;  // 主色调
		public string playerName;   // 自定义名字
		public string team;         // 所在队伍
		public string joinMessage;  // 加入信息
		public string leaveMessage; // 离开信息
	}
	private PendingData _pending;

	#endregion

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
			// 模型准备完成
			IsModelReady = true;
			// 将缓存的待应用数据一次性应用到模型
			FlushPendingData();
			// 刷新Team Rule状态
			RefreshRuleReference();
			// 暂时关闭对象
			_isDead = true;
			PlayerObject.SetActive(false);
			MPMain.LogInfo(Localization.Get("RPContainer.MappingSucceeded", PlayerId.ToString()));
			return true;
		} catch (Exception ex) {
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
		if (IsModelReady) { }

		PlayerObject = null;
		_remotePlayer = null;
		_remoteLeftHand = null;
		_remoteRightHand = null;
		_remoteTag = null;
		RemoteEntities = null;
		_objectTaggers = null;
		_colliders = null;
		_modelBehaviour = null;
		IsModelReady = false;
		_isDead = false;
	}

	/// <summary>
	/// 模型 + 容器引用全部清空(玩家离开时使用)
	/// </summary>
	public void Destroy() {
		Object.Destroy(PlayerObject);
		DestroyModel();
		// 清空 pending, 防止内存泄漏
		_pending = default;
	}

	#endregion

	#region[SteamMember数据写入接口]

	/// <summary>
	/// 读取并清除缓存的加入消息(RPManager 在合适时机调用)
	/// </summary>
	public string GetJoinMessage() => _pending.joinMessage;

	/// <summary>
	/// 读取离开消息(不清除, 玩家离线时 RPManager 读取后连同容器一起销毁)
	/// </summary>
	public string GetLeaveMessage() => _pending.leaveMessage;

	/// <summary>
	/// 从 MemberData 字典批量写入所有字段
	/// 返回是否收到了新的 prefabId (供 RPManager 决定是否重建模型) 
	/// </summary>
	public bool ApplyMemberData(Dictionary<string, string> data) {
		var changes = data
			.Where(kvp => !_memberData.TryGetValue(kvp.Key, out var oldValue) || oldValue != kvp.Value)
			.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

		_memberData = data;

		// 更新名称
		if (changes.TryGetValue(MPKeys.PLAYER_NAME, out var name)) {
			_pending.playerName = name;
			if (IsModelReady) ApplyName(name);
		}

		// 更新颜色
		if (changes.TryGetValue(MPKeys.PLAYER_COLOR, out var colorStr)
			&& MPUtil.TryParsePlayerColor(colorStr, out var color)) {

			_pending.color = color;
			if (IsModelReady) ApplyColor(color);
		}

		// 更新队伍
		if (changes.TryGetValue(MPKeys.TEAM, out var team)) {
			_pending.team = team;
			if (IsModelReady) ApplyTeam(team);
		}

		// 更新加入信息(目前无用)
		if (changes.TryGetValue(MPKeys.JOIN_MESSAGE, out var join))
			_pending.joinMessage = join;

		// 更新离开信息
		if (changes.TryGetValue(MPKeys.LEAVE_MESSAGE, out var leave))
			_pending.leaveMessage = leave;

		// prefabId 最后处理, 返回"是否需要重建模型"给 Manager 判断
		if (changes.TryGetValue(MPKeys.PREFAB_ID, out var pid)) {
			if (RPFactoryManager.Instance.TryGetExtension(pid, out var extension)) {
				_pending.prefabId = pid;
				Extension = extension;
				return true;
			}
		}
		return false;
	}

	#endregion

	#region[SteamMember数据应用]

	/// <summary>
	/// 模型就绪后, 将所有缓存数据一次性刷入
	/// </summary>
	private void FlushPendingData() {
		if (_pending.playerName != null) ApplyName(_pending.playerName);
		if (_pending.color.HasValue) ApplyColor(_pending.color.Value);
		if (_pending.team != null) ApplyTeam(_pending.team);
	}

	private void ApplyColor(Color32 color) {
		try {
			_modelBehaviour?.ApplyPlayerColor(color);
		} catch (Exception ex) {
			MPMain.LogError($"[Debug] 修改模型 {prefabId} 颜色时出错, 错误: {ex.Message}");
		}
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
		_modelBehaviour = instance.GetComponent<CustomModelBehaviour>();

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
		// 获取全部子对象
		Transform[] allChildren = instance.GetComponentsInChildren<Transform>(true);
		// 添加容器引用组件
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
		var transform = _modelBehaviour?.HandItemTransform ?? new Dictionary<string, ItemPoseData>();
		_remoteLeftHand?.Initialize();
		_remoteRightHand?.Initialize();
		_remoteLeftHand?.itemTransform = transform;
		_remoteRightHand?.itemTransform = transform;
	}

	#endregion

	#region[数据更新]

	/// <summary>
	/// 通过数据进行位置更新
	/// </summary>
	public void HandlePlayerData(ref PlayerData playerData) {
		// 死亡后0.5秒内不接受更新, 避免瞬移和动画冲突
		if (_isDead && !_deathTick.IsTickReached || PlayerObject == null || _remotePlayer == null) return;

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
	/// 通过额外字典进行如 背包物品显示等数据更新
	/// </summary>
	public void HandlePlayerDictData(Dictionary<string, string> playerData) {
		if (playerData.TryGetValue(MPKeys.PLAYER_SCALE, out var scaleStr)) {
			var args = scaleStr.Split(',');
			if (args.Length >= 2
				&& float.TryParse(args[0], out var height)
				&& float.TryParse(args[1], out var radius)
				&& IsModelReady)
				ApplyScale(height, radius);
		}

		try {
			_modelBehaviour?.HandlePlayerData(playerData);
		} catch (Exception ex) {
			MPMain.LogError($"[Debug] 使用玩家数据时出错, 错误: {ex.Message}");
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
		string effectName = Extension?.DeathEffectAssetName ?? MPAssetManager.DEATH_OBJECT_NAME;

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
	/// 处理玩家造成的伤害
	/// </summary>
	public void HandleDamage(Damageable.DamageInfo damageInfo) {
		// 决定采用什么受击特效资产名字
		string effectName = Extension?.DamageEffectAssetName ?? MPAssetManager.DAMAGE_OBJECT_NAME;
		GameObject effectPrefab = MPAssetManager.GetFXPrefab(effectName)
						   ?? CL_AssetManager.GetAssetGameObject(effectName);
		if (effectPrefab != null) {
			Vector3 spawnPos = damageInfo.position != Vector3.zero
				? damageInfo.position
				: PlayerObject.transform.position;
			GameObject.Instantiate(effectPrefab, spawnPos, Quaternion.identity);
		}
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
		if (_remoteTag != null) _remoteTag.gameObject.SetActive(actionRule.tagShow);

		// 更新玩家间交互权限
		ChangeGrabOrHang(MPCore.IsGrabOrHangState);

		// 更新PVP权限
		foreach (var entity in RemoteEntities) entity.pvpEnabled = actionRule.pvp;

		if (actionRule.pvp) {
			foreach (var tagger in _objectTaggers) {
				tagger.AddTag(MPKeys.DAMAGE_TAGGER);
				tagger.AddTag(MPKeys.CREATURE_TAGGER);
			}
		} else {
			foreach (var tagger in _objectTaggers) {
				tagger.RemoveTag(MPKeys.DAMAGE_TAGGER);
				tagger.RemoveTag(MPKeys.CREATURE_TAGGER);
			}
		}

		// 更新碰撞权限
		if (_colliders != null) foreach (var col in _colliders)
			// 只处理非触发器的实体碰撞体
			if (!col.isTrigger) col.enabled = actionRule.collision;// true 开启碰撞, false 关闭碰撞


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

