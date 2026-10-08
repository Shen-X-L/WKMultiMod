using HarmonyLib;
using Steamworks.Data;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.Core;
using UnityEngine;
using WKMPMod.Components;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.NetWork;
using WKMPMod.RemotePlayers;
using WKMPMod.Team;
using WKMPMod.Util;
using static WKMPMod.Data.MPWriterPool;
using Object = UnityEngine.Object;

namespace WKMPMod.World;

/// <summary>
/// 多人游戏物品同步操作类型 (P2P 协议标签).
/// </summary>
public enum SceneItemSyncAction : byte {
	// 场景物品相关
	Create = 0,        // 创建物品: 场景物品创建(暂时不使用 死亡掉落物品会做成场景物品)
	Remove = 1,        // 移除物品: 场景物品消除
	RemoveChunk = 2,   // 移除物品: 主机发送的物品移除包
	RemoveChunkRequest = 3,    // 请求数据: 在重置场景/切换队伍时想主机申请移除物品网络包
	SceneToDropped = 4, // 所有权广播: 在使用抓钩/合成天下挪动物品时将物品申请所有者
}

public enum DroppedItemSyncAction : byte {
	Create = 0,// 创建物品: 广播在指定位置生成/注册一个掉落物
	PickupRequest = 1,// 拾取申请: 拾取非自己持有物品时, 单播给该物品的所有者申请所有权
	Remove = 2,// 移除物品: 广播全局销毁掉落物 (同时清除世界物体与背包数据)
	PickupReject = 3,// 拾取拒绝: 所有者确认物品已被别人抢先取走, 通知申请者回滚背包
	UpdateTransform = 4,// 更新位置: 所有者更新物品位置
	TransferRequest = 5,// 转移请求: 向所有者请求转移掉落物所有权
	TransferConfirm = 6,// 转移成立: 所有者广播掉落物新所有者
	TransferReject = 7,// 转移拒绝: 所有者向请求者发送转移拒绝
}

public enum ItemType : byte {
	NoneItem = 0,// 单机物品,如果在联机情况下读取,默认场景物品
	SceneItem = 1,// 场景物品,简单的静态物品,无归属权
	DroppedItem = 2,// 丢弃物品,复杂的动态物品,有归属权,有位置更新
}

/// <summary>
/// 场景物品管理器 P2P广播物品层级ID->hashID来确定唯一物品 主机仅记录场景物品消失记录
/// 场景内自带物品
/// 相关网络组件 <see cref="NetworkedItem"/>
/// </summary>
public class SceneItemModule : Singleton<SceneItemModule>, ISyncModule {
	// 快照协议每帧最多发送/注册物品数量, 防止大批量物品导致帧率下降
	private const int TombstonesPerChunk = 20;
	// 被其他玩家拿走过的场景id集合 可能会重复多发
	private HashSet<ulong> _sceneTombstones = new();
	// 注册到场景缓存
	private Dictionary<ulong, Item_Object> _sceneItems = new();
	// 主机端数据结构：TeamId 该队伍已销毁的物品 ID 集合
	private Dictionary<string, HashSet<ulong>> _teamTombstones = new();
	// 主机对每个玩家的发送协程
	private Dictionary<IDType, Coroutine> _sendCoroutines = new();

	#region[ISyncModule接口实现]

	public string ModuleName => "SceneItemSync";

	/// <summary>
	/// 是否开启了物品同步
	/// </summary>
	public bool IsEnabled { get; set; }

	public void OnResetMap() {
		ResetState();
	}

	// 没有联机情况 清空死亡生物记录和死亡生物记录发送协程
	public void OnLeave() {
		// 没有联机情况 清空所有状态
		if (!MPCore.IsReady) {
			_sceneTombstones.Clear();
			_sceneItems.Clear();

			foreach (var tobstone in _teamTombstones.Values) tobstone.Clear();
			_teamTombstones.Clear();

			foreach (var coroutine in _sendCoroutines.Values)
				if (coroutine != null) WorldSyncManager.Instance.StopCoroutine(coroutine);
			_sendCoroutines.Clear();

			return;
		}
		ResetState();
	}

	public void OnEnd() => OnLeave();

	public void OnSyncUpdate(float deltaTime) { }

	#endregion

	#region[API]

	/// <summary>
	/// 对所有记录进行重置, 在游戏地图重置,玩家队伍切换时调用 重新申请销毁物品表
	/// </summary>
	public void ResetState() {
		// 如果是场景切换 删除场景缓存物品记录
		_sceneItems.Clear();
		// 目前重启也会导致之前物品消失 
		// 注释后 重启后物品消失不同步
		_sceneTombstones.Clear();
	}

	/// <summary>
	/// 玩家转移队伍 刷新场景物品记录
	/// </summary>
	public void ChangeTeam() {
		_sceneTombstones.Clear();
		if (!MPSteamworks.IsHost) SendSceneRemoveChunkRequest();
		else if (_teamTombstones.TryGetValue(MPCore.CurrentTeam, out var newTombstones))
			_sceneTombstones = new HashSet<ulong>(newTombstones);
	}

	/// <summary>
	/// 场景物品被首次加载调用 检测该物品是否被其他同规则玩家拿走过
	/// </summary>
	public void OnSceneItemStarted(Item_Object itemObject) {
		if (itemObject == null || !MPCore.IsReady) return;
		// 黑名单或无法同步
		if (!IsSyncableWorldItem(itemObject) || IsBlacklisted(itemObject.gameObject)) return;
		// 由p2p创建的物品
		if (itemObject.TryGetComponent<NetworkedItem>(out var tempIdentity)
			&& tempIdentity.itemCreateType != ItemType.SceneItem) return;

		// 生成场景序列Hash
		ulong networkHashId = GetSceneNetworkId(itemObject);
		if (networkHashId == 0) return;

		// 该场景物品在客机加载前就已经被别人拾取/销毁了
		if (_sceneTombstones.Contains(networkHashId)) {
			// 销毁对象并删除记录
			MPMain.LogInfo($"[ItemSync] Suppressing deleted scene item on load: {networkHashId}");
			itemObject.gameObject.SetActive(false);
			Object.Destroy(itemObject);
			_sceneItems.Remove(networkHashId);
			return;
		}
		// 建立NetworkId
		var identity = GetOrCreateIdentity(itemObject.gameObject);
		identity.networkId = networkHashId;
		identity.ownerId = default;
		identity.itemCreateType = ItemType.SceneItem;

		// 缓存到 O(1) 检索字典
		_sceneItems[networkHashId] = itemObject;
	}

	/// <summary>
	/// 广播物品标签删除
	/// </summary>
	public void NotifyLocalRemove(NetworkedItem identity) {
		if (identity == null || !MPCore.IsReady || identity?.itemCreateType != ItemType.SceneItem) return;
		// 遗忘物品网络ID
		_sceneItems.Remove(identity.networkId);
		BroadcastSceneRemove(identity);
		// 重置网络ID
		identity.networkId = 0;
	}

	/// <summary>
	/// 广播场景物品进行个人所有权获取
	/// </summary>
	public void SceneToDropped(NetworkedItem identity) {
		if (identity == null || !MPCore.IsReady || identity?.itemCreateType != ItemType.SceneItem) return;
		// 遗忘物品网络ID
		_sceneItems.Remove(identity.networkId);
		DroppedItemModule.Instance.ConvertFromScene(identity, MPSteamworks.UserSteamId, true);
		BroadcastToDropped(identity);
	}

	#endregion

	#region[场景ID生成]

	/// <summary>
	/// 生成场景物品的稳定唯一 ID.
	/// 格式: sceneitem:{场景名}:{层级路径}
	/// <para>
	/// 主客双方加载同一关卡后对同一 Item_Object 生成相同 ID, 实现精确匹配无需模糊搜索.
	/// </para>
	/// </summary>
	private ulong GetSceneNetworkId(Item_Object itemObject) {
		if (itemObject == null || itemObject.gameObject == null) return 0;
		if (!itemObject.gameObject.scene.IsValid() || string.IsNullOrEmpty(itemObject.gameObject.scene.name))
			return 0;

		var identity = itemObject.GetComponent<NetworkedItem>();
		if (identity != null && identity.itemCreateType == ItemType.SceneItem)
			return identity.networkId;

		string path = MPUtil.BuildTransformPath(itemObject.transform);
		return MPUtil.Hash64("sceneItem:" + path);
	}

	#endregion

	#region[网络数据发送]

	/// <summary>
	/// 发送场景物品被拾取数据
	/// 接收函数: <see cref="HandleSceneRemove"/>
	/// </summary>
	private void BroadcastSceneRemove(NetworkedItem identity) {
		var writer = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.SceneItemSync);
		writer.Put((byte)SceneItemSyncAction.Remove);
		writer.Put(identity.networkId);

		// 仅广播给物品同步队伍的玩家 和 主机必须的一份
		var playerIds = RPManager.Instance.GetPlayersMatchingRule(RuleType.SyncSceneItem, true);
		foreach (var targetId in playerIds)
			if (targetId != MPSteamworks.Instance.HostSteamId)
				MPSteamworks.Instance.SendToPeer(targetId, writer, SendType.Reliable);

		// 通知主机进行删除记录
		if (!MPSteamworks.IsHost) MPSteamworks.Instance.SendToHost(writer, SendType.Reliable);
		else {
			if (!_teamTombstones.TryGetValue(MPCore.CurrentTeam, out var tombstones)) {
				tombstones = new HashSet<ulong>();
				_teamTombstones[MPCore.CurrentTeam] = tombstones;
			}
			tombstones.Add(identity.networkId);
		}
	}

	/// <summary>
	/// 客机向主机请求场景物品销毁表 (NeedRemoveChunk)
	/// 接收函数: <see cref="HandleSceneRemoveChunkRequest"/>
	/// </summary>
	private void SendSceneRemoveChunkRequest() {
		if (MPSteamworks.IsHost) return;

		MPMain.LogInfo("[MP ItemSync] Requesting Scene Tombstone Chunk from Host...");
		var writer = GetWriter(MPSteamworks.UserSteamId, MPSteamworks.Instance.HostSteamId, PacketType.SceneItemSync);
		writer.Put((byte)SceneItemSyncAction.RemoveChunkRequest);

		MPSteamworks.Instance.SendToHost(writer, SendType.Reliable);
	}

	/// <summary>
	/// 协程：分帧向客户端补发墓碑列表, 防止大批量数据导致帧率卡顿或网络拥塞
	/// 接收函数: <see cref="HandleSceneRemoveChunk"/>
	/// </summary>
	private IEnumerator SendTombstoneChunksCoroutine(IDType clientId, IReadOnlyCollection<ulong> tombstones) {
		int total = tombstones.Count;
		if (total == 0) yield break;

		using var enumerator = tombstones.GetEnumerator();
		int sentCount = 0;

		while (sentCount < total) {
			int countToSend = Mathf.Min(TombstonesPerChunk, total - sentCount);

			var writer = GetWriter(MPSteamworks.UserSteamId, clientId, PacketType.SceneItemSync);
			writer.Put((byte)SceneItemSyncAction.RemoveChunk);
			writer.Put(countToSend);

			for (int i = 0; i < countToSend; i++)
				if (enumerator.MoveNext()) writer.Put(enumerator.Current);

			MPSteamworks.Instance.SendToPeer(clientId, writer, SendType.Reliable);
			sentCount += countToSend;

			yield return null; // 等待下一帧继续发送
		}

		MPMain.LogInfo($"[MP ItemSync] Finished sending {total} tombstones to client {clientId}");
		_sendCoroutines.Remove(clientId);
	}

	/// <summary>
	/// 发送场景物品被接管权限
	/// 接收函数: <see cref="HandleSceneToDropped"/>
	/// </summary>
	private void BroadcastToDropped(NetworkedItem identity) {
		if (identity == null || identity.networkId == 0) return;

		var writer = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.SceneItemSync);
		writer.Put((byte)SceneItemSyncAction.SceneToDropped);
		writer.Put(identity.networkId);

		// 广播给其他开启场景物品同步的玩家
		var playerIds = RPManager.Instance.GetPlayersMatchingRule(RuleType.SyncSceneItem, true);
		foreach (var targetId in playerIds) {
			if (targetId != MPSteamworks.Instance.HostSteamId)
				MPSteamworks.Instance.SendToPeer(targetId, writer, SendType.Reliable);
		}

		if (!MPSteamworks.IsHost) {
			MPSteamworks.Instance.SendToHost(writer, SendType.Reliable);
		} else {
			if (!_teamTombstones.TryGetValue(MPCore.CurrentTeam, out var tombstones)) {
				tombstones = new HashSet<ulong>();
				_teamTombstones[MPCore.CurrentTeam] = tombstones;
			}
			tombstones.Add(identity.networkId);
		}
	}

	#endregion

	#region[网络数据接收]

	public void HandleSceneCreate(IDType senderId, DataReader reader) {
	}

	/// <summary>
	/// 接收场景物品消失数据, 物品存在则消除, 不存在则记录为待删除
	/// 发送函数: <see cref="BroadcastSceneRemove"/>
	/// </summary>
	public void HandleSceneRemove(IDType senderId, DataReader reader) {
		var networkId = reader.GetULong();
		// 是主机 记录该玩家所在队伍的销毁项
		if (MPSteamworks.IsHost) {
			var teamName = RPManager.Instance.GetPlayerTeam(senderId);
			if (!_teamTombstones.TryGetValue(teamName, out var tombstones)) {
				tombstones = new HashSet<ulong>();
				_teamTombstones[teamName] = tombstones;
			}
			tombstones.Add(networkId);
			// 不在相同队伍 不执行遗忘
			if (!RPManager.Instance.GetPlayerRuleValue(senderId, RuleType.SyncSceneItem)) return;
		}

		// 执行本地销毁与遗忘
		ProcessSceneItemRemoval(networkId);
	}

	/// <summary>
	/// 接收主机补发的场景物品销毁表 Chunk (SceneRemoveChunk)
	/// 发送函数: <see cref="SendTombstoneChunksCoroutine"/>
	/// </summary>
	public void HandleSceneRemoveChunk(DataReader reader) {
		int count = reader.GetInt();
		MPMain.LogInfo($"[MP ItemSync] Received Tombstone Chunk with {count} items.");

		for (int i = 0; i < count; i++) {
			var networkId = reader.GetULong();
			ProcessSceneItemRemoval(networkId);
		}
	}

	/// <summary>
	/// 主机收到客机申请销毁表请求 (NeedRemoveChunk)
	/// 发送函数: <see cref="SendSceneRemoveChunkRequest"/>
	/// </summary>
	public void HandleSceneRemoveChunkRequest(IDType senderId) {
		if (!MPCore.CanSync || !MPSteamworks.IsHost || WorldSyncManager.Instance == null) return;
		if (senderId == 0 || senderId == MPSteamworks.UserSteamId) return;

		var teamName = RPManager.Instance.GetPlayerTeam(senderId);
		if (teamName == string.Empty) teamName = MPKeys.DEFAULT_TEAM;

		// 获取所有对 teamA同步过的队伍列表
		var teams = TeamRuleManager.GetTeamsWithRuleTo(teamName, RuleType.SyncSceneItem, true);
		// 停止旧协程
		if (_sendCoroutines.TryGetValue(senderId, out var coroutine) && coroutine != null)
			WorldSyncManager.Instance.StopCoroutine(coroutine);

		HashSet<ulong> tombstones = new();
		foreach (var activeTeam in teams)
			if (_teamTombstones.TryGetValue(activeTeam, out var teamStones))
				tombstones.UnionWith(teamStones);

		if (tombstones.Count == 0) return;

		// 启动协程分帧发送
		_sendCoroutines[senderId] = WorldSyncManager.Instance.StartCoroutine(SendTombstoneChunksCoroutine(senderId, tombstones));
	}

	/// <summary>
	/// 接收场景物品所有权转移
	/// 发送函数: <see cref="BroadcastToDropped"/>
	/// </summary>
	private void HandleSceneToDropped(IDType senderId, DataReader reader) {
		var networkId = reader.GetULong();
		if (networkId == 0) return;
		// 是主机 记录该玩家所在队伍的销毁项
		if (MPSteamworks.IsHost) {
			var teamName = RPManager.Instance.GetPlayerTeam(senderId);
			if (!_teamTombstones.TryGetValue(teamName, out var tombstones)) {
				tombstones = new HashSet<ulong>();
				_teamTombstones[teamName] = tombstones;
			}
			tombstones.Add(networkId);
			// 不在相同队伍 不执行遗忘
			if (!RPManager.Instance.GetPlayerRuleValue(senderId, RuleType.SyncSceneItem)) return;
		}
		// 写入本地墓碑记录 (HashSet.Add 返回 false 说明早已记录过)
		_sceneTombstones.Add(networkId);
		// 若场景中已有该实体, 转移所有权
		if (_sceneItems.TryGetValue(networkId, out var itemObject)) {
			_sceneItems.Remove(networkId);
			if (itemObject.TryGetComponent<NetworkedItem>(out var identity))
				DroppedItemModule.Instance.ConvertFromScene(identity, senderId, false);
		}
	}

	/// <summary>
	/// 收到其他对等端发来的物品同步包时, 按 action 类型分发给对应处理函数.
	/// 由 MPPacketHandlers.HandleItemStateSync 调用.
	/// </summary>
	public void HandleItemState(IDType senderId, DataReader reader) {
		var action = (SceneItemSyncAction)reader.GetByte();
		try {
			switch (action) {
				case SceneItemSyncAction.Create:
					MPMain.LogDebug("[MP SceneItemSync] Create");
					HandleSceneCreate(senderId, reader);
					break;
				case SceneItemSyncAction.Remove:
					MPMain.LogDebug("[MP SceneItemSync] Remove");
					HandleSceneRemove(senderId, reader);
					break;
				case SceneItemSyncAction.RemoveChunk:
					MPMain.LogDebug("[MP SceneItemSync] RemoveChunk");
					HandleSceneRemoveChunk(reader);
					break;
				case SceneItemSyncAction.RemoveChunkRequest:
					MPMain.LogDebug("[MP SceneItemSync] RemoveChunkRequest");
					HandleSceneRemoveChunkRequest(senderId);
					break;
				case SceneItemSyncAction.SceneToDropped:
					MPMain.LogDebug("[MP SceneItemSync] SceneToDropped");
					HandleSceneToDropped(senderId, reader);
					break;
			}
		} catch (Exception e) {
			MPMain.LogError($"[MP ItemSync] HandleItemState failed for action {action}: {e.Message}");
		}
	}

	#endregion

	#region[工具函数]

	/// <summary>
	/// 处理场景物品销毁的统一核心逻辑 (本地/网络单条/网络 Chunk 均调用此函数)
	/// </summary>
	private void ProcessSceneItemRemoval(ulong networkId) {
		if (networkId == 0) return;

		// 写入本地墓碑记录 (HashSet.Add 返回 false 说明早已记录过)
		_sceneTombstones.Add(networkId);

		// 若场景中已有该实体, 进行销毁并移除缓存
		if (_sceneItems.TryGetValue(networkId, out var itemObject)) {
			MPMain.LogInfo($"[MP ItemSync] Suppressing deleted scene item: {networkId}");
			if (itemObject != null && itemObject.gameObject != null) {
				itemObject.gameObject.SetActive(false);
				Object.Destroy(itemObject.gameObject);
			}
			_sceneItems.Remove(networkId);
		}
	}

	/// <summary>获取或添加 NetworkedItem 组件.</summary>
	public static NetworkedItem GetOrCreateIdentity(GameObject gameObject) {
		return gameObject.GetComponent<NetworkedItem>() ?? gameObject.AddComponent<NetworkedItem>();
	}

	#endregion

	#region[黑名单判定]

	private static readonly HashSet<string> _blacklistedPrefabNames = new(StringComparer.OrdinalIgnoreCase) {
		"Item_Flashlight",		// 手电筒
		"Item_Flaregun",		// 信号枪
		"Item_Cryogun",			// 冷冻枪
		"Item_Handgun",			// 手枪
		"Item_Handgun_Debug",	// 手枪
		"Item_10mm_Ammo",		// 手枪子弹
	};

	private static readonly HashSet<string> _blacklistedItemTags = new(StringComparer.OrdinalIgnoreCase) {
		"artifact", // 神器
		"disk",     // 磁盘
		"trinket",	// 饰品
		"notsync",	// 不同步
	};

	private static readonly HashSet<string> _blacklistedObjectTagger = new(StringComparer.OrdinalIgnoreCase) {
		"ItemLocked"// 锁定物品
	};

	/// <summary>检查预制体名称是否在黑名单.</summary>
	public static bool IsBlacklisted(string prefabName) {
		if (string.IsNullOrEmpty(prefabName)) return false;
		return _blacklistedPrefabNames.Contains(MPUtil.CleanCloneName(prefabName));
	}

	/// <summary>检查 Item 数据是否在黑名单 (物品标签).</summary>
	public static bool IsBlacklisted(Item item) {
		if (item == null) return false;

		if (item.itemTags != null)
			foreach (var tag in item.itemTags)
				if (_blacklistedItemTags.Contains(tag)) return true;

		return false;
	}

	/// <summary>检查 ObjectTagger 是否在黑名单.</summary>
	public static bool IsBlacklisted(ObjectTagger tagger) {
		if (tagger?.tags == null) return false;

		foreach (var tag in tagger.tags)
			if (_blacklistedObjectTagger.Contains(tag)) return true;

		return false;
	}

	public static bool IsBlacklisted(GameObject go) {
		if (go == null) return false;

		// 检查对象名称
		if (IsBlacklisted(go.name))
			return true;

		// 检查数据组件 Item_Object
		if (go.TryGetComponent<Item_Object>(out var itemObj) && IsBlacklisted(itemObj.itemData))
			return true;

		// 检查标签组件 ObjectTagger
		if (go.TryGetComponent<ObjectTagger>(out var tagger) && IsBlacklisted(tagger))
			return true;

		return false;
	}

	/// <summary>
	/// 判断 Item_Object 是否为有效的可同步世界物品 (场景枚举与快照使用).
	/// <para>
	/// 排除条件: null/已销毁 · 不活跃 · 不在有效场景 · 无 itemData · inBag=true (背包物品)
	/// </para>
	/// </summary>
	private static bool IsSyncableWorldItem(Item_Object itemObject) {
		if (itemObject == null || itemObject.gameObject == null) return false;
		if (!itemObject.gameObject.activeInHierarchy) return false;
		if (string.IsNullOrEmpty(itemObject.gameObject.scene.name)) return false;
		if (itemObject.itemData == null) return false;
		if (itemObject.itemData.inBag) return false;
		return true;
	}

	#endregion
}

/// <summary>
/// 生成物品管理器 P2P广播物品个人ID UserSteamId:LocalItemId++->hashID来确定唯一物品 
/// 生成者对物品有所有权 其他人需要进行申请
/// 玩家生成物品
/// 相关网络组件 <see cref="NetworkedItem"/>
/// </summary>
public class DroppedItemModule : Singleton<DroppedItemModule>, ISyncModule {
	#region[	字段反射工具]

	public Func<Item, bool> _inHandMethod =
		AccessTools.MethodDelegate<Func<Item, bool>>(AccessTools.Method(typeof(Item), "InHand"));

	#endregion

	#region[	数据储存]

	private Dictionary<ulong, NetworkedItem> _p2pItems = new();
	private Dictionary<ulong, NetworkedItem> _ownedItems = new();
	private ulong _nextLocalItemId = 1;     // 本地 P2P ID 自增计数器: 与 SteamId 组合确保全局唯一

	#endregion

	#region[	申请冷却]

	private readonly HashSet<ulong> _transferRequestWindow = new();// 窗口内申请记录 
	private const float TRANSFER_REQUEST_COOLDOWN = 0.5f;// 所有者转移申请冷却
	private float _transferRequestCooldownTimer;// 运行时冷却期

	#endregion

	#region[	分帧更新控制]

	// 周期性更新间隔 (秒 约10Hz)
	private const float PeriodicUpdateInterval = 0.10f;
	private int _maxScanPerFrame = 50;  // 单帧最多扫描/检查的物品数量 (扫描上限)
	private int _maxSyncPerFrame = 10;  // 单帧最多打包发送的物品数量 (发送上限)
	private float _timer = 0f;

	// 扫描控制字段
	private bool _isScanning = false;
	private int _scanIndex = 0;

	// 发送控制字段
	private bool _isSweeping = false;
	private int _sweepIndex = 0;

	// 缓存与队列 (复用集合避免 GC)
	private readonly List<NetworkedItem> _sweepQueue = new();
	private readonly List<NetworkedItem> _batchBuffer = new();
	private readonly List<ulong> _localKeysCache = new();
	private readonly List<ulong> _pendingRemoveKeys = new();

	private void ResetSweepState() {
		_timer = 0f;
		_isScanning = false;
		_scanIndex = 0;
		_isSweeping = false;
		_sweepIndex = 0;
		_sweepQueue.Clear();
		_batchBuffer.Clear();
		_localKeysCache.Clear();
		_pendingRemoveKeys.Clear();
	}

	#endregion

	#region[ISyncModule接口实现]

	public string ModuleName => "DroppedItemSync";

	/// <summary>
	/// 是否开启了物品同步
	/// </summary>
	public bool IsEnabled { get; set; } = true;

	public void OnResetMap() {
		_p2pItems.Clear();
		_ownedItems.Clear();
		_nextLocalItemId = 1;

		_transferRequestWindow.Clear();
		_transferRequestCooldownTimer = 0f;

		ResetSweepState();
	}

	// 没有联机情况 清空死亡生物记录和死亡生物记录发送协程
	public void OnLeave() {
		_p2pItems.Clear();
		_ownedItems.Clear();
		_nextLocalItemId = 1;

		_transferRequestWindow.Clear();
		_transferRequestCooldownTimer = 0f;

		ResetSweepState();
	}

	public void OnEnd() => OnLeave();

	#endregion

	#region[分帧同步]

	public void OnSyncUpdate(float deltaTime) {
		_transferRequestCooldownTimer += deltaTime;
		if (_transferRequestCooldownTimer >= TRANSFER_REQUEST_COOLDOWN) {
			_transferRequestCooldownTimer = 0f;
			_transferRequestWindow.Clear();
		}

		if (!MPCore.CanSync || !IsEnabled) {
			ResetSweepState();
			return;
		}

		// 1. 空闲阶段: 累加定时器, 等待下一个同步周期到来
		if (!_isScanning && !_isSweeping) {
			_timer += deltaTime;
			if (_timer >= PeriodicUpdateInterval) {
				_timer = Mathf.Max(0f, _timer - PeriodicUpdateInterval); // 保留余数保证计时精准
				StartNewScanCycle();                            // 触发新一轮扫描
			}
		}

		// 2. 分帧扫描阶段: 每帧最多检查 _maxScanPerFrame 个本地物品
		if (_isScanning) StepScan();
		
		// 3. 分帧发送阶段: 扫描完成后, 分帧将数据冲刷给客户端
		if (_isSweeping) FlushNextBatch();
	}

	/// <summary>
	/// 开启新一轮同步, 拍照缓存当前所有本地持有物品的 Key
	/// </summary>
	private void StartNewScanCycle() {
		_sweepQueue.Clear();
		_scanIndex = 0;
		_localKeysCache.Clear();

		_localKeysCache.AddRange(_ownedItems.Keys);

		// 如果当前持有物品存在, 启动分帧扫描
		if (_localKeysCache.Count > 0) _isScanning = true;
	}

	/// <summary>
	/// 开启新一轮同步, 扫描并收集所有需要更新位置/状态的本地持有物品
	/// </summary>
	private void StepScan() {
		int scannedThisFrame = 0;

		// 逐个检查, 检查数量达到上限 _maxScanPerFrame 或 遍历完列表 时停下
		while (_scanIndex < _localKeysCache.Count && scannedThisFrame < _maxScanPerFrame) {
			ulong networkId = _localKeysCache[_scanIndex];
			_scanIndex++;
			scannedThisFrame++; // 只要检查了一个物品, 计数器就 +1

			// 容错: 防止因其他逻辑提前从字典中删除了 key
			if (!_ownedItems.TryGetValue(networkId, out var identity) || identity == null) continue;

			// 二次校验物品有效性与失效判定
			if (identity.gameObject == null || !identity.gameObject.activeInHierarchy || !identity.IsValidSyncItem()) 
				_pendingRemoveKeys.Add(networkId);
			// 变化检测 (只要有变动就加入待发送队列)
			else if (identity.HasMeaningfulChange) _sweepQueue.Add(identity);
			
		}

		// 统一清理本轮检查到的失效本地物品 Key
		if (_pendingRemoveKeys.Count > 0) {
			for (int i = 0; i < _pendingRemoveKeys.Count; i++) {
				ulong key = _pendingRemoveKeys[i];
				if (key != 0) {
					_ownedItems.Remove(key);
					_p2pItems.Remove(key);
				}
			}
			_pendingRemoveKeys.Clear();
		}

		// 检查是否已扫描完全部物品
		if (_scanIndex >= _localKeysCache.Count) {
			_isScanning = false;
			_localKeysCache.Clear(); // 释放缓存引用

			// 扫描完成！如果有需要同步的物品, 进入发送冲刷阶段
			if (_sweepQueue.Count > 0) {
				_isSweeping = true;
				_sweepIndex = 0;
			}
		}
	}

	/// <summary>
	/// 连续分帧打包, 单帧最多打包并发送 _maxSyncPerFrame 个丢弃物品 Transform
	/// </summary>
	private void FlushNextBatch() {
		_batchBuffer.Clear();

		// 截取当前帧能容纳的上限数据
		while (_sweepIndex < _sweepQueue.Count && _batchBuffer.Count < _maxSyncPerFrame) {
			var identity = _sweepQueue[_sweepIndex];
			_sweepIndex++;

			// 跨帧二次有效性校验 (防止在前几帧冲刷期间物品被彻底 Destroy)
			if (identity != null && identity.gameObject != null && identity.gameObject.activeInHierarchy) 
				_batchBuffer.Add(identity);
		}

		// 发送 Transform 批量包
		if (_batchBuffer.Count > 0) BroadcastUpdateTransformBatch(_batchBuffer);
		
		// 如果队列已经全部发完, 关闭冲刷, 等待下一个 PeriodicUpdateInterval 触发
		if (_sweepIndex >= _sweepQueue.Count) {
			_isSweeping = false;
			_sweepQueue.Clear();
		}
	}

	#endregion

	#region[API]

	/// <summary>
	/// 本地玩家丢弃物品时调用 (由 Harmony 补丁 Patch_Inventory_DropItemIntoWorld_ItemSync 在 Postfix 触发).
	/// <para>
	/// 检查可同步性后调用 SyncAndBroadcast.
	/// SyncAndBroadcast 内部会判断是否已有 NetworkId (防止重复广播).
	/// </para>
	/// </summary>
	public void NotifyLocalDrop(Item item) {
		if (item == null || !MPCore.CanSync) return;

		var itemObject = item.GetDropObject();
		if (!IsSyncableDropItem(itemObject)) return;
		if (IsBlacklisted(itemObject.gameObject)) return;

		SyncAndBroadcast(itemObject);
	}
	public void NotifyLocalDrop(Item_Object itemObject) {
		if (!MPCore.CanSync) return;

		if (!IsSyncableDropItem(itemObject)) return;
		if (IsBlacklisted(itemObject.gameObject)) return;

		SyncAndBroadcast(itemObject);
	}

	/// <summary>
	/// p2p物品拾取API
	/// 所有者: 广播物品被拾取移除
	/// 非所有者: 乐观拾取->发送申请
	///		拒绝: 物品回滚消失
	///		允许: 物品正常保留并更改所有者
	/// </summary>
	public void NotifyLocalRemove(NetworkedItem identity) {
		if (identity == null || !MPCore.IsReady || identity?.itemCreateType != ItemType.DroppedItem) return;
		if (identity.ownerId == MPSteamworks.UserSteamId) {
			// 我是所有者: 直接广播 Remove 并且仅隐藏物品而不移除
			BroadcastRemove(identity.networkId, MPSteamworks.UserSteamId);
			Forget(identity.networkId, false);
		} else {
			// 他人所有: 乐观拾取, 向所有者申请所有权
			SendPickupRequest(identity.networkId, identity.ownerId);
		}
	}

	/// <summary>
	/// 脚本/触发器直接在本地生成一个同步的世界掉落物.
	/// 实例化物品后调用 SyncAndBroadcast 使其进入 P2P 网络.
	/// </summary>
	public void SpawnSyncedWorldDrop(string prefabKey, Vector3 position, Quaternion rotation, Vector3 velocity) {
		if (!MPCore.CanSync || string.IsNullOrWhiteSpace(prefabKey)) return;

		var (itemObject, identity) = InstantiateWorldItem(prefabKey, position, rotation);
		if (itemObject == null) return;

		SyncAndBroadcast(itemObject);
	}

	/// <summary>
	/// 为 Item_Object 赋予网络身份并广播创建.
	/// <para>
	/// 若该物体已有 NetworkedItem 组件且 NetworkId 非空 (说明已经在网络中), 直接复用并重新广播.
	/// 若没有 (本地新物品), 添加组件, 分配 "{UserSteamId}:{自增ID}", 设置 OwnerId = 我, 广播 Create.
	/// <br/>
	/// 适用场景: 玩家丢弃物品, 关卡触发器生成, 临时联网化黑名单道具等.
	/// </para>
	/// </summary>
	/// <returns>NetworkedItem 同步组件, 失败返回 null</returns>
	public NetworkedItem SyncAndBroadcast(Item_Object itemObject) {
		if (itemObject == null || itemObject.gameObject == null) return null;

		var identity = GetOrCreateIdentity(itemObject.gameObject);

		// 如果没有同步组件 (或 ID 为空), 当场进行 P2P 注册
		if (identity.networkId == 0 || identity.ownerId == default) {
			identity.networkId = MPUtil.Hash64($"{MPSteamworks.UserSteamId}:{_nextLocalItemId++}"); // SteamId 命名空间 + 本地自增 = 全局唯一
			identity.prefabKey = GetPrefabKey(itemObject);
			identity.ownerId = MPSteamworks.UserSteamId; // 此物品的首任所有者
			identity.itemCreateType = ItemType.DroppedItem;
			_p2pItems[identity.networkId] = identity;
			_ownedItems[identity.networkId] = identity;
		} else {
			// 如果它已经有网络 ID, 必须确保它存在于追踪字典中
			if (!_p2pItems.ContainsKey(identity.networkId)) {
				identity.ownerId = MPSteamworks.UserSteamId; // 此物品的所有者
				identity.itemCreateType = ItemType.DroppedItem;
				_p2pItems[identity.networkId] = identity;
				_ownedItems[identity.networkId] = identity;
			}
		}

		// 广播 Create, 告知网络中所有对等端生成或注册此物体
		BroadcastDropCreate(identity, itemObject, identity.CurrentVelocity);
		return identity;
	}

	/// <summary>
	/// 进行所有权转移申请
	/// </summary>
	public void NotifyLocalTransfer(NetworkedItem identity) {
		// 是玩家物品 清除异常的标签并结束
		if (identity.ownerId == MPSteamworks.UserSteamId) {
			identity.ItemObject.itemData.itemTags?.Remove(MPKeys.OTHER_PLAYER_ITEM);
			return;
		}
		// 目标玩家不存在或已经离开
		if (identity.ownerId == default || !MPSteamworks.Instance.Members.Any(f => f.Id == identity.ownerId)) {
			BroadcastTransferConfirm(identity.networkId, identity.ownerId, MPSteamworks.UserSteamId);
			identity.ownerId = MPSteamworks.UserSteamId;
			_ownedItems[identity.networkId] = identity;
			identity.ItemObject.itemData.itemTags?.Remove(MPKeys.OTHER_PLAYER_ITEM);
			return;
		}
		// 向目标所有者发送所有权转移请求
		SendTransferRequest(identity.networkId, identity.ownerId);
	}

	/// <summary>
	/// 从场景物品转移物品为丢弃物品
	/// </summary>
	/// <param name="isOwner">是否是物品所有者</param>
	public void ConvertFromScene(NetworkedItem identity, IDType ownerId, bool isOwner) {
		if (identity == null) return;

		identity.ownerId = ownerId;
		identity.itemCreateType = ItemType.DroppedItem;

		_p2pItems[identity.networkId] = identity;
		if (isOwner) _ownedItems[identity.networkId] = identity;
	}

	#endregion

	#region[网络数据发送]

	/// <summary>
	/// 向全网广播创建物品.
	/// 接收函数: <see cref="HandleDropCreate"/>
	/// </summary>
	private void BroadcastDropCreate(NetworkedItem identity, Item_Object itemObject, Vector3 velocity) {
		if (identity == null || itemObject == null || identity.networkId == 0) return;

		var writer = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.DroppedItemSync);
		writer.Put((byte)DroppedItemSyncAction.Create);
		writer.Put(identity.networkId);
		writer.Put(identity.prefabKey);
		writer.Put(itemObject.transform.position);
		writer.Put(itemObject.transform.rotation);
		writer.Put(velocity);

		// 仅广播给物品同步队伍的玩家
		var playerIds = RPManager.Instance.GetPlayersMatchingRule(RuleType.SyncDropItem, true);
		foreach (var targetId in playerIds)
			MPSteamworks.Instance.SendToPeer(targetId, writer, SendType.Reliable);
	}

	/// <summary>
	/// 向物品所有者单播拾取申请.
	/// 接收函数: <see cref="HandlePickupRequest"/>
	/// </summary>
	private void SendPickupRequest(ulong networkId, ulong ownerId) {
		var writer = GetWriter(MPSteamworks.UserSteamId, ownerId, PacketType.DroppedItemSync);
		writer.Put((byte)DroppedItemSyncAction.PickupRequest);
		writer.Put(networkId);
		MPSteamworks.Instance.SendToPeer(ownerId, writer, SendType.Reliable);
	}

	/// <summary>
	/// 向拾取申请者单播拒绝消息.
	/// 接收函数: <see cref="HandlePickupReject"/>
	/// </summary>
	private void SendPickupReject(ulong networkId, ulong targetId) {
		var writer = GetWriter(MPSteamworks.UserSteamId, targetId, PacketType.DroppedItemSync);
		writer.Put((byte)DroppedItemSyncAction.PickupReject);
		writer.Put(networkId);
		MPSteamworks.Instance.SendToPeer(targetId, writer, SendType.Reliable);
	}

	/// <summary>
	/// 向启用物品同步的玩家广播移除物品.
	/// holderId 标识最终持有物品的一方: 收到此广播时若 holderId == 自身则跳过 ForceCleanup
	/// (因为持有者本地已在发包前完成了清理).
	/// <see cref="HandleRemove"/>
	/// </summary>
	private void BroadcastRemove(ulong networkId, IDType holderId) {
		var writer = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.DroppedItemSync);
		writer.Put((byte)DroppedItemSyncAction.Remove);
		writer.Put(networkId);
		writer.Put(holderId);

		// 仅广播给物品同步队伍的玩家
		var playerIds = RPManager.Instance.GetPlayersMatchingRule(RuleType.SyncDropItem, true);
		foreach (var targetId in playerIds)
			MPSteamworks.Instance.SendToPeer(targetId, writer, SendType.Reliable);
	}

	/// <summary>
	/// 向物品所有者单播所有权转移请求
	/// 接收函数: <see cref="HandleTransferRequest"/>
	/// </summary>
	private void SendTransferRequest(ulong networkId, ulong ownerId) {
		if (!_transferRequestWindow.Add(networkId)) return;
		var writer = GetWriter(MPSteamworks.UserSteamId, ownerId, PacketType.DroppedItemSync);
		writer.Put((byte)DroppedItemSyncAction.TransferRequest);
		writer.Put(networkId);
		MPSteamworks.Instance.SendToPeer(ownerId, writer, SendType.Reliable);
	}

	/// <summary>
	/// 广播所有权转移成功
	/// 接收函数: <see cref="HandleTransferConfirm"/>
	/// </summary>
	private void BroadcastTransferConfirm(ulong networkId, IDType oldOwnerId, IDType newOwnerId) {
		var writer = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.DroppedItemSync);
		writer.Put((byte)DroppedItemSyncAction.TransferConfirm);
		writer.Put(networkId);
		writer.Put(oldOwnerId);
		writer.Put(newOwnerId);

		var playerIds = RPManager.Instance.GetPlayersMatchingRule(RuleType.SyncDropItem, true);
		foreach (var targetId in playerIds)
			MPSteamworks.Instance.SendToPeer(targetId, writer, SendType.Reliable);
	}

	/// <summary>
	/// 向所有权转移申请者单播拒绝
	/// 接收函数: <see cref="HandleTransferReject"/>
	/// </summary>
	private void SendTransferReject(ulong networkId, IDType targetId) {
		var writer = GetWriter(MPSteamworks.UserSteamId, targetId, PacketType.DroppedItemSync);
		writer.Put((byte)DroppedItemSyncAction.TransferReject);
		writer.Put(networkId);
		MPSteamworks.Instance.SendToPeer(targetId, writer, SendType.Reliable);
	}

	/// <summary>
	/// 批量广播更新 Transform 及速度
	/// 接收函数: <see cref="HandleUpdateTransform"/>
	/// </summary>
	private void BroadcastUpdateTransformBatch(List<NetworkedItem> batch) {
		if (batch == null || batch.Count == 0) return;

		var writer = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.DroppedItemSync);
		writer.Put((byte)DroppedItemSyncAction.UpdateTransform);
		writer.Put((byte)batch.Count);

		for (int i = 0; i < batch.Count; i++) {
			var identity = batch[i];
			identity.RememberSyncState();

			writer.Put(identity.networkId);
			writer.Put(identity.transform.position);
			writer.Put(identity.transform.rotation);
			writer.Put(identity.CurrentVelocity);
		}

		var playerIds = RPManager.Instance.GetPlayersMatchingRule(RuleType.SyncDropItem, true);
		foreach (var targetId in playerIds) {
			MPSteamworks.Instance.SendToPeer(targetId, writer, SendType.Unreliable | SendType.NoNagle);
		}
	}

	#endregion

	#region[网络数据处理]

	/// <summary>
	/// 收到创建消息: 按优先级匹配候选或实例化新物品, 应用初始状态并写入追踪.
	/// 发送函数: <see cref="BroadcastDropCreate"/>
	/// </summary>
	private void HandleDropCreate(IDType senderId, DataReader reader) {
		var networkId = reader.GetULong();
		var prefabKey = reader.GetString();
		var position = reader.GetVector3();
		var rotation = reader.GetQuaternion();
		var velocity = reader.GetVector3();

		if (networkId == 0 || string.IsNullOrEmpty(prefabKey)) return;

		// 如果已经追踪过此 ID, 直接刷新状态
		if (_p2pItems.TryGetValue(networkId, out var existing) && existing != null) {
			existing.ownerId = senderId;
			existing.ApplyRemoteState(position, rotation, velocity);
			existing.SetRemoteControlled(senderId != MPSteamworks.UserSteamId);
			return;
		}

		// 是玩家丢弃产生的 p2p 物品, 直接实例化并注册网络身份
		var (itemObject, identity) = InstantiateWorldItem(prefabKey, position, rotation, networkId: networkId, ownerId: senderId);

		// 物品已经销毁或无法实例化, 直接忽略
		if (itemObject == null || identity == null) return;

		_p2pItems[networkId] = identity;
		identity.ApplyRemoteState(position, rotation, velocity);
		identity.SetRemoteControlled(senderId != MPSteamworks.UserSteamId);
	}

	/// <summary>
	/// 收到拾取申请 (PickupRequest): 判断我是否是该物品的所有者并决定批准或拒绝.
	/// 发送函数: <see cref="SendPickupRequest"/>
	/// <br/>
	/// 批准条件: _items 中有此物品 且 OwnerId == 我.
	///		BroadcastRemove(holderId=申请者): 全网清理 (申请者因 holderId==其自身 自动跳过 ForceCleanup)
	///		本地 Forget
	/// <br/>
	/// 拒绝条件: _items 中无此物品 (已被别人先拿) 或 OwnerId != 我 (所有权信息不一致).
	///		SendPickupReject: 申请者收到后执行 ForceCleanup 回滚背包
	/// <br/>
	/// 包是单播给所有者的, 正常情况下 OwnerId==我 恒成立. OwnerId!=我 属于异常边界情况.
	/// </summary>
	private void HandlePickupRequest(IDType requesterId, DataReader reader) {
		var networkId = reader.GetULong();
		if (networkId == 0) return;

		MPMain.LogInfo($"[MP ItemSync] PickupRequest from {requesterId} for {networkId}");

		// 物品已不在我这里 (已被别人先拿), 拒绝申请
		if (!_p2pItems.TryGetValue(networkId, out var identity) || identity == null) {
			SendPickupReject(networkId, requesterId);
			return;
		}

		// 物品在背包 拒绝申请
		if (identity.IsInLocalInventory(_inHandMethod)) {
			MPMain.LogWarning($"[MP ItemSync] PickupRequest denied: Item {networkId} is already in local inventory.");
			SendPickupReject(networkId, requesterId);
			return;
		}

		// 所有权异常 (不应发生): 拒绝申请
		if (identity.ownerId != MPSteamworks.UserSteamId || !_ownedItems.ContainsKey(networkId)) {
			SendPickupReject(networkId, requesterId);
			return;
		}

		// 批准: 广播 Remove (holderId=申请者) + 本地遗忘
		BroadcastRemove(networkId, requesterId);
		Forget(networkId, true);
	}

	/// <summary>
	/// 收到全局移除消息 (Remove): 执行双向清理.
	/// 发送函数: <see cref="BroadcastRemove"/>
	/// <br/>
	/// holderId == 我: 我就是发起 Remove 的那方 (批准了别人的 PickupRequest 或自己拾起了自己的物品).
	/// <br/>
	/// holderId != 我: 他人拾起了物品, 执行 ForceCleanupItemPhysicalAndInventory.
	/// </summary>
	private void HandleRemove(IDType senderId, DataReader reader) {
		var networkId = reader.GetULong();
		var holderId = reader.GetULong();

		// 与目标队伍间没有启用物品同步
		if (!RPManager.Instance.GetPlayerRuleValue(senderId, RuleType.SyncDropItem)) return;

		if (networkId == 0) return;

		if (holderId == MPSteamworks.UserSteamId) {
			// 物品已经在背包里了,跳过双向清理,但剥夺网络记录
			// 否则下次扔出来时,依然附带旧的 OwnerId,导致别人的拾取申请被拒
			if (!_p2pItems.TryGetValue(networkId, out var identity) || identity == null) return;
			var itemObject = identity.GetComponent<Item_Object>();
			identity.networkId = 0;
			_p2pItems.Remove(networkId);
			return;
		}

		// 进行物品回滚
		ForceCleanupItemPhysicalAndInventory(networkId);
	}

	/// <summary>
	/// 收到拾取拒绝 (PickupReject): 乐观拾取失败, 强制回滚背包中已装入的该物品数据.
	/// 执行背包清理与世界实体清除.
	/// 发送函数: <see cref="SendPickupReject"/>
	/// </summary>
	private void HandlePickupReject(DataReader reader) {
		var networkId = reader.GetULong();
		if (networkId == 0) return;

		MPMain.LogWarning($"[MP ItemSync] PickupReject received! Rolling back inventory for {networkId}");
		ForceCleanupItemPhysicalAndInventory(networkId);
	}

	/// <summary>
	/// 收到所有者转移申请 (PickupRequest): 判断我是否是该物品的所有者并决定批准或拒绝.
	/// 发送函数: <see cref="SendTransferRequest"/>
	/// </summary>
	private void HandleTransferRequest(IDType requesterId, DataReader reader) {
		var networkId = reader.GetULong();
		if (networkId == 0) return;

		// 物品已不在我这里 (已被别人先拿), 拒绝申请
		if (!_p2pItems.TryGetValue(networkId, out var identity) || identity == null) {
			SendTransferReject(networkId, requesterId);
			return;
		}

		// 物品在背包 拒绝申请
		if (identity.IsInLocalInventory(_inHandMethod)) {
			MPMain.LogWarning($"[MP ItemSync] PickupRequest denied: Item {networkId} is already in local inventory.");
			SendTransferReject(networkId, requesterId);
			return;
		}

		// 所有权异常 (不应发生): 拒绝申请
		if (identity.ownerId != MPSteamworks.UserSteamId || !_ownedItems.ContainsKey(networkId)) {
			SendTransferReject(networkId, requesterId);
			return;
		}

		// 权限转移 并 添加所有者标记
		identity.ownerId = requesterId;
		_ownedItems.Remove(identity.networkId);
		identity.SetRemoteControlled(true);
		var itemData = identity.ItemObject.itemData;
		itemData.itemTags ??= new List<string>();
		if (!itemData.itemTags.Contains(MPKeys.OTHER_PLAYER_ITEM))
			itemData.itemTags.Add(MPKeys.OTHER_PLAYER_ITEM);

		// 广播新所有者
		BroadcastTransferConfirm(networkId, MPSteamworks.UserSteamId, requesterId);
	}

	/// <summary>
	/// 收到所有权转移信息 对该I得到物品进行所有权转移
	/// 发送函数: <see cref="BroadcastTransferConfirm"/>
	/// </summary>
	private void HandleTransferConfirm(DataReader reader) {
		var networkId = reader.GetULong();
		var oldOwnerId = reader.GetULong();
		var newOwnerId = reader.GetULong();

		// 与目标队伍间没有启用物品同步
		if (oldOwnerId != default && !RPManager.Instance.GetPlayerRuleValue(oldOwnerId, RuleType.SyncDropItem)) return;
		// 物品不存在
		if (networkId == 0 || !_p2pItems.TryGetValue(networkId, out var identity) || identity == null) return;

		// 转移所有权
		identity.ownerId = newOwnerId;
		// 缓存数据
		var itemData = identity.ItemObject.itemData;

		// 额外标签处理
		// 保底校验 虽然应该在HandleTransferRequest时进行过处理 存在其他玩家向本玩家未申请直接转移的可能
		if (oldOwnerId == MPSteamworks.UserSteamId) {
			_ownedItems.Remove(identity.networkId);
			identity.SetRemoteControlled(true);
			itemData.itemTags ??= new List<string>();
			if (!itemData.itemTags.Contains(MPKeys.OTHER_PLAYER_ITEM))
				itemData.itemTags.Add(MPKeys.OTHER_PLAYER_ITEM);
		} else if (newOwnerId == MPSteamworks.UserSteamId) {
			// 所有权转移到本玩家
			_ownedItems[identity.networkId] = identity;
			identity.SetRemoteControlled(false);
			itemData.itemTags?.Remove(MPKeys.OTHER_PLAYER_ITEM);
		}
	}

	/// <summary>
	/// 收到所有权转移拒绝 使用悲观转移申请 失败后无操作
	/// 接收函数: <see cref="SendTransferReject"/>
	/// </summary>
	private void HandleTransferReject(DataReader reader) {
		var networkId = reader.GetULong();
	}

	/// <summary>
	/// 处理远程广播的批量 UpdateTransform 数据
	/// 发送函数: <see cref="BroadcastUpdateTransformBatch"/>
	/// </summary>
	private void HandleUpdateTransform(DataReader reader) {
		byte count = reader.GetByte();
		for (int i = 0; i < count; i++) {
			ulong networkId = reader.GetULong();
			Vector3 pos = reader.GetVector3();
			Quaternion rot = reader.GetQuaternion();
			Vector3 vel = reader.GetVector3();

			if (_p2pItems.TryGetValue(networkId, out var identity) && identity != null)
				identity.ApplyRemoteState(pos, rot, vel);
		}
	}

	/// <summary>
	/// 收到其他对等端发来的物品同步包时, 按 action 类型分发给对应处理函数.
	/// 由 MPPacketHandlers.HandleItemStateSync 调用.
	/// </summary>
	public void HandleItemState(IDType senderId, DataReader reader) {
		var action = (DroppedItemSyncAction)reader.GetByte();
		try {
			switch (action) {
				case DroppedItemSyncAction.Create:
					MPMain.LogDebug("[MP DropItemSync] Create");
					HandleDropCreate(senderId, reader);
					break;
				case DroppedItemSyncAction.PickupRequest:
					MPMain.LogDebug("[MP DropItemSync] PickupRequest");
					HandlePickupRequest(senderId, reader);
					break;
				case DroppedItemSyncAction.Remove:
					MPMain.LogDebug("[MP DropItemSync] PickupRemove");
					HandleRemove(senderId, reader);
					break;
				case DroppedItemSyncAction.PickupReject:
					MPMain.LogDebug("[MP DropItemSync] PickupReject");
					HandlePickupReject(reader);
					break;
				case DroppedItemSyncAction.TransferRequest:
					MPMain.LogDebug("[MP DropItemSync] TransferRequest");
					HandleTransferRequest(senderId, reader);
					break;
				case DroppedItemSyncAction.TransferConfirm:
					MPMain.LogDebug("[MP DropItemSync] TransferConfirm");
					HandleTransferConfirm(reader);
					break;
				case DroppedItemSyncAction.TransferReject:
					MPMain.LogDebug("[MP DropItemSync] TransferReject");
					HandleTransferReject(reader);
					break;
				case DroppedItemSyncAction.UpdateTransform:
					MPMain.LogDebug("[MP DropItemSync] UpdateTransform");
					HandleUpdateTransform(reader);
					break;
			}
		} catch (Exception e) {
			MPMain.LogError($"[MP ItemSync] HandleItemState failed for action {action}: {e.Message}");
		}
	}

	#endregion

	#region[工具函数]

	/// <summary>
	/// 实例化世界物品并返回其 Item_Object 组件.
	/// <para>
	/// ApplyingRemoteState=true: 阻止 Item_Object.Start() 触发的游戏回调进入同步链路.
	/// 手动调用 InitializeItemData: 立即建立 Item.dropObject 引用, 不等待 Start().
	/// levelRoot 父级: 与游戏本体 Item.Drop() 保持一致.
	/// </para>
	/// </summary>
	private static (Item_Object, NetworkedItem) InstantiateWorldItem(
		string prefabKey, Vector3 position, Quaternion rotation, ulong networkId = 0, IDType ownerId = 0
	) {
		if (!MPUtil.TryGetItemPrefab(prefabKey, out Item_Object prefab)) return (null, null);

		// 记录 Prefab 原始状态并临时关闭 Prefab
		bool originalActive = prefab.gameObject.activeSelf;
		prefab.gameObject.SetActive(false);

		// 克隆对象
		var itemComponent = Object.Instantiate(prefab, position, rotation);

		// 还原预制体状态
		prefab.gameObject.SetActive(originalActive);

		// 创建失败
		if (itemComponent == null) return (null, null);

		// 完成网络数据的配置
		var identity = GetOrCreateIdentity(itemComponent.gameObject);
		if (networkId != 0) identity.SetupIdentity(networkId, prefabKey, ownerId, ItemType.DroppedItem, true);
		if (ownerId != MPSteamworks.UserSteamId && ownerId != default) itemComponent.itemData.itemTags.Add(MPKeys.OTHER_PLAYER_ITEM);

		// 获取所在关卡
		var closeLevelRoot = WorldLoader.GetClosestLevelToPosition(position);
		var nowLevelRoot = WorldLoader.GetCurrentLevelFromBounds();
		// 不同关卡时零重力
		if (closeLevelRoot != nowLevelRoot) itemComponent.GetComponent<Rigidbody>()?.useGravity = false;
		if (nowLevelRoot != null) itemComponent.transform.SetParent(closeLevelRoot.GetLevel().GetParentRoot());
		// 绑定数据
		if (itemComponent.itemData != null)
			itemComponent.itemData.InitializeItemData(itemComponent);

		// 正常激活
		itemComponent.gameObject.SetActive(true);

		return (itemComponent, identity);
	}

	/// <summary>
	/// 获取物品的预制体键: 优先 itemData.prefabName, 否则取去 Clone 后缀的 GameObject 名.
	/// </summary>
	private static string GetPrefabKey(Item_Object itemObject) {
		if (itemObject.itemData != null && !string.IsNullOrEmpty(itemObject.itemData.prefabName))
			return itemObject.itemData.prefabName;
		return MPUtil.CleanCloneName(itemObject.gameObject.name);
	}

	/// <summary>获取或添加 NetworkedItem 组件.</summary>
	public static NetworkedItem GetOrCreateIdentity(GameObject gameObject) {
		return gameObject.GetComponent<NetworkedItem>() ?? gameObject.AddComponent<NetworkedItem>();
	}

	#endregion

	#region[网络标签控制]

	/// <summary>
	/// 清除世界 Item_Object 实体 + 清除背包 Item 数据.
	/// <para>
	/// 触发场景:
	/// HandleRemove: 收到全网广播销毁时
	/// HandlePickupReject: 乐观拾取被所有者拒绝, 回滚背包数据
	/// </para>
	/// </summary>
	public void ForceCleanupItemPhysicalAndInventory(ulong networkId) {
		if (networkId == 0) return;

		// 物体存在判断
		if (!_p2pItems.TryGetValue(networkId, out var identity) || identity == null) return;

		// 委派给组件自身完成清理
		identity.ForceCleanup();

		// 清除记录
		_p2pItems.Remove(networkId);
	}

	/// <summary>
	/// 从追踪中遗忘物品: 移除候选记录, 隐藏/销毁 GameObject, 从 _items 移除.
	/// <para>
	/// WasInstantiatedBySync=true: Destroy (同步创建的临时物体)
	/// WasInstantiatedBySync=false: 仅 SetActive(false) (场景原有/玩家本地丢弃产生的物体)
	/// </summary>
	private void Forget(ulong networkId, bool needDestroy) {
		if (!_p2pItems.TryGetValue(networkId, out var identity) || identity == null) return;

		var itemObject = identity.GetComponent<Item_Object>();

		if (needDestroy && identity.gameObject != null) {
			// 正常的场景遗忘清理
			identity.gameObject.SetActive(false);
			Object.Destroy(identity.gameObject);
		}
		_p2pItems.Remove(networkId);
		_ownedItems.Remove(networkId);
	}

	#endregion

	#region[黑名单判定]

	private static readonly HashSet<string> _blacklistedPrefabNames = new(StringComparer.OrdinalIgnoreCase) {

	};

	private static readonly HashSet<string> _blacklistedItemTags = new(StringComparer.OrdinalIgnoreCase) {
		"notsync",	// 不同步
	};

	private static readonly HashSet<string> _blacklistedObjectTagger = new(StringComparer.OrdinalIgnoreCase) {
		"ItemLocked"// 锁定物品
	};

	/// <summary>检查预制体名称是否在黑名单.</summary>
	public static bool IsBlacklisted(string prefabName) {
		if (string.IsNullOrEmpty(prefabName)) return false;
		return _blacklistedPrefabNames.Contains(MPUtil.CleanCloneName(prefabName));
	}

	/// <summary>检查 Item 数据是否在黑名单 (物品标签).</summary>
	public static bool IsBlacklisted(Item item) {
		if (item == null) return false;

		if (item.itemTags != null)
			foreach (var tag in item.itemTags)
				if (_blacklistedItemTags.Contains(tag)) return true;

		return false;
	}

	/// <summary>检查 ObjectTagger 是否在黑名单.</summary>
	public static bool IsBlacklisted(ObjectTagger tagger) {
		if (tagger?.tags == null) return false;

		foreach (var tag in tagger.tags)
			if (_blacklistedObjectTagger.Contains(tag)) return true;

		return false;
	}

	public static bool IsBlacklisted(GameObject go) {
		if (go == null) return false;

		// 检查对象名称
		if (IsBlacklisted(go.name))
			return true;

		// 检查数据组件 Item_Object
		if (go.TryGetComponent<Item_Object>(out var itemObj) && IsBlacklisted(itemObj.itemData))
			return true;

		// 检查标签组件 ObjectTagger
		if (go.TryGetComponent<ObjectTagger>(out var tagger) && IsBlacklisted(tagger))
			return true;

		return false;
	}

	/// <summary>
	/// 判断丢弃产生的 Item_Object 是否可同步 (NotifyLocalDrop 使用).
	/// <para>
	/// 判定逻辑与 IsSyncableWorldItem 相同, 保留独立入口便于未来分离两类物品的过滤规则.
	/// 注意: 黑名单物品在 IsSyncableWorldItem 中被排除 (场景枚举), 但丢弃路径不经过此函数,
	/// 即黑名单物品可以被丢弃并同步 (这是设计意图: 场景生成不同步, 但丢弃同步).
	/// </para>
	/// </summary>
	private static bool IsSyncableDropItem(Item_Object itemObject) {
		if (itemObject == null || itemObject.gameObject == null) return false;
		if (!itemObject.gameObject.activeInHierarchy) return false;
		if (itemObject.itemData == null) return false;
		if (itemObject.itemData.inBag) return false;
		return true;
	}

	#endregion
}

public static class ItemSyncBridge {
	/// <summary>
	/// 统一的本地拾取/移除处理入口
	/// </summary>
	public static void OnLocalPickup(Item_Object itemObject) {
		if (itemObject == null || !MPCore.IsReady || !itemObject.TryGetComponent<NetworkedItem>(out var identity)) return;

		MPMain.LogInfo($"[MP ItemSync] LocalPickup: {itemObject.name}, ID={identity.networkId}, Owner={identity.ownerId}");

		if (identity.itemCreateType == ItemType.SceneItem) {
			// 场景物品被拾取
			SceneItemModule.Instance.NotifyLocalRemove(identity);
		} else if (identity.itemCreateType == ItemType.DroppedItem) {
			// P2P 掉落物被拾取
			DroppedItemModule.Instance.NotifyLocalRemove(identity);
		} else {
			MPMain.LogError($"[MP ItemSync] 未知物品创建方式");
		}
	}

	/// <summary>
	/// 统一的本地丢弃/生成处理入口
	/// </summary>
	public static void OnLocalDrop(Item item) {
		if (item == null || !MPCore.CanSync) return;

		// 任何物品丢弃到世界中, 一律作为 DroppedItem 进行广播
		DroppedItemModule.Instance.NotifyLocalDrop(item);
	}

	/// <summary>
	/// 是否可以执行移动物品操作
	/// </summary>
	public static bool OnLocalMove(Item_Object itemObject) {
		if (itemObject == null || !MPCore.IsReady || !itemObject.TryGetComponent<NetworkedItem>(out var identity)) return true;

		if (identity.itemCreateType == ItemType.SceneItem) {
			// 场景物品被移动 广播所有权转移
			SceneItemModule.Instance.SceneToDropped(identity);
			return true;
		} else if (identity.itemCreateType == ItemType.DroppedItem) {
			// 其他人物品 申请所有权
			if (itemObject.itemData.HasTag(MPKeys.OTHER_PLAYER_ITEM) || identity.ownerId != MPSteamworks.UserSteamId) {
				DroppedItemModule.Instance.NotifyLocalTransfer(identity);
				return false;
			} else return true;
		} else {
			MPMain.LogError($"[MP ItemSync] 未知物品创建方式");
		}

		return false;
	}

	public static bool OnLocalRemove(Item_Object itemObject) {
		if (itemObject == null || !MPCore.IsReady || !itemObject.TryGetComponent<NetworkedItem>(out var identity)) return true;

		MPMain.LogInfo($"[MP ItemSync] LocalRemove: {itemObject.name}, ID={identity.networkId}, Owner={identity.ownerId}");

		if (identity.itemCreateType == ItemType.SceneItem) {
			// 场景物品被移动 广播所有权转移
			SceneItemModule.Instance.NotifyLocalRemove(identity);
			return true;
		} else if (identity.itemCreateType == ItemType.DroppedItem) {
			// 其他人物品 申请所有权
			if (itemObject.itemData.HasTag(MPKeys.OTHER_PLAYER_ITEM) || identity.ownerId != MPSteamworks.UserSteamId) {
				DroppedItemModule.Instance.NotifyLocalTransfer(identity);
				return false;
			} else {
				// 本机物品 执行销毁并广播
				DroppedItemModule.Instance.NotifyLocalRemove(identity);
				return true;
			}
		} else {
			MPMain.LogError($"[MP ItemSync] 未知物品创建方式 HasTag:{itemObject.itemData.HasTag(MPKeys.OTHER_PLAYER_ITEM)} ownerId:{identity.ownerId}");
		}

		return false;
	}

}