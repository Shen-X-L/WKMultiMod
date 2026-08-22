using HarmonyLib;
using Steamworks.Data;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using WKMPMod.Component;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.NetWork;
using WKMPMod.RemotePlayer;
using WKMPMod.Util;
using static Inventory;
using static UnityEngine.UI.Image;
using static WKMPMod.Data.MPWriterPool;
using Object = UnityEngine.Object;

namespace WKMPMod.World;

/// <summary>
/// 多人游戏物品同步操作类型 (P2P 协议标签).
/// </summary>
public enum ItemSyncAction : byte {
	// 场景物品相关
	SceneCreate = 0,        // 创建物品: 场景物品创建(暂时不使用)
	SceneRemove = 1,        // 移除物品: 场景物品消除
	SceneRemoveChunk = 2,   // 移除物品: 主机发送的物品移除包
	SceneRemoveChunkRequest = 3,    // 请求数据: 在重置场景/切换队伍时想主机申请移除物品网络包

	// P2P物品相关
	DropCreate = 10,        // 创建物品: 广播在指定位置生成/注册一个掉落物
	PickupRequest = 11,     // 拾取申请: 拾取非自己持有物品时, 单播给该物品的所有者申请所有权
	PickupRemove = 12,      // 移除物品: 广播全局销毁掉落物 (同时清除世界物体与背包数据)
	PickupReject = 13,      // 拾取拒绝: 所有者确认物品已被别人抢先取走, 通知申请者回滚背包
}

public class SceneItemModule: Singleton<SceneItemModule>, ISyncModule{
	public const byte SCENE_ITEM = 1;
	// 快照协议每帧最多发送/注册物品数量, 防止大批量物品导致帧率下降
	private const int TombstonesPerChunk = 10;
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

	public void OnReset() {
		ResetState();
	}

	// 没有联机情况 清空死亡生物记录和死亡生物记录发送协程
	public void OnLeave() {
		// 没有联机情况 清空所有状态
		if (!MPCore.IsInLobby && !MPCore.IsInitialized) {
			_sceneTombstones.Clear();
			_sceneItems.Clear();

			foreach (var tobstone in _teamTombstones.Values)
				tobstone.Clear();
			_teamTombstones.Clear();

			foreach (var coroutine in _sendCoroutines.Values)
				if (coroutine != null) MPCore.Instance.StopCoroutine(coroutine);
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
	public  void ResetState() {
		// 如果是场景切换 删除场景缓存物品记录
		_sceneItems.Clear();
		// 目前重启也会导致之前物品消失 
		// 注释后 重启后物品消失不同步
		//_sceneTombstones.Clear();
	}

	public void ChangeTeam() {
		_sceneTombstones.Clear();
		if (!MPSteamworks.IsHost) SendSceneRemoveChunkRequest();
		else if (_teamTombstones.TryGetValue(MPCore.CurrentTeam, out var newTombstones))
			_sceneTombstones = new HashSet<ulong>(newTombstones);
	}

	/// <summary>
	/// 场景物品被首次加载调用 检测该物品是否被其他同规则玩家拿走过
	/// </summary>
	public  void OnSceneItemStarted(Item_Object itemObject) {
		if (itemObject == null || !MPCore.IsReady) return;
		// 黑名单或无法同步
		if (!IsSyncableWorldItem(itemObject) || IsBlacklisted(itemObject.gameObject)) return;
		// 由p2p创建的物品
		if (itemObject.TryGetComponent<NetworkedItem>(out var tempIdentity)
			&& !(tempIdentity.sceneOrDropped == SCENE_ITEM)) return;

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
		// 缓存到 O(1) 检索字典
		_sceneItems[networkHashId] = itemObject;

		// 建立NetworkId
		var identity = GetOrCreateIdentity(itemObject.gameObject);
		identity.networkId = networkHashId;
		identity.ownerId = default;
		identity.isRemote = false;
		identity.sceneOrDropped = SCENE_ITEM;
	}

	/// <summary>
	/// 广播物品标签删除
	/// </summary>
	public  void NotifyLocalPickup(NetworkedItem identity) {
		BroadcastSceneRemove(identity);
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
	private  ulong GetSceneNetworkId(Item_Object itemObject) {
		if (itemObject == null || itemObject.gameObject == null) return 0;
		if (!itemObject.gameObject.scene.IsValid() || string.IsNullOrEmpty(itemObject.gameObject.scene.name))
			return 0;

		var identity = itemObject.GetComponent<NetworkedItem>();
		if (identity != null && identity.sceneOrDropped == SCENE_ITEM) {
			return identity.networkId;
		}

		string path = MPUtil.BuildTransformPath(itemObject.transform);
		return MPUtil.Hash64("sceneItem:" + path);
	}

	#endregion

	#region[网络数据发送]

	/// <summary>
	/// 发送场景物品被拾取数据
	/// 接收函数: <see cref="HandleSceneRemove"/>
	/// </summary>
	private  void BroadcastSceneRemove(NetworkedItem identity) {
		if ((identity?.sceneOrDropped != SCENE_ITEM)) return;
		var writer = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.SceneItemStateSync);
		writer.Put((byte)ItemSyncAction.SceneRemove);
		writer.Put(identity.networkId);

		// 仅广播给物品同步队伍的玩家 和 主机必须的一份
		var playerIds = RPManager.Instance.GetPlayersMatchingRule(RuleType.SyncItem, true);
		foreach (var targetId in playerIds)
			if (targetId != MPSteamworks.Instance.HostSteamId)
				MPSteamworks.Instance.SendToPeer(targetId, writer, SendType.Reliable);

		MPSteamworks.Instance.SendToHost(writer, SendType.Reliable);
	}

	/// <summary>
	/// 客机向主机请求场景物品销毁表 (NeedRemoveChunk)
	/// 接收函数: <see cref="HandleSceneRemoveChunkRequest"/>
	/// </summary>
	private  void SendSceneRemoveChunkRequest() {
		if (MPSteamworks.IsHost) return;

		MPMain.LogInfo("[MP ItemSync] Requesting Scene Tombstone Chunk from Host...");
		var writer = GetWriter(MPSteamworks.UserSteamId, MPSteamworks.Instance.HostSteamId, PacketType.SceneItemStateSync);
		writer.Put((byte)ItemSyncAction.SceneRemoveChunkRequest);

		MPSteamworks.Instance.SendToHost(writer, SendType.Reliable);
	}

	/// <summary>
	/// 协程：分帧向客户端补发墓碑列表, 防止大批量数据导致帧率卡顿或网络拥塞
	/// 接收函数: <see cref="HandleSceneRemoveChunk"/>
	/// </summary>
	private  IEnumerator SendTombstoneChunksCoroutine(IDType clientId, List<ulong> tombstoneList) {
		int total = tombstoneList.Count;
		int currentIndex = 0;

		while (currentIndex < total) {
			int countToSend = Mathf.Min(TombstonesPerChunk, total - currentIndex);

			var writer = GetWriter(MPSteamworks.UserSteamId, clientId, PacketType.SceneItemStateSync);
			writer.Put((byte)ItemSyncAction.SceneRemoveChunk);
			writer.Put(countToSend);

			for (int i = 0; i < countToSend; i++) {
				writer.Put(tombstoneList[currentIndex + i]);
			}

			MPSteamworks.Instance.SendToPeer(clientId, writer, SendType.Reliable);
			currentIndex += countToSend;

			yield return null; // 等待下一帧继续发送
		}

		MPMain.LogInfo($"[MP ItemSync] Finished sending {total} tombstones to client {clientId}");
		_sendCoroutines.Remove(clientId);
	}

	#endregion

	#region[网络数据接收]

	public  void HandleSceneCreate(IDType senderId, DataReader reader) {
	}

	/// <summary>
	/// 接收场景物品消失数据, 物品存在则消除, 不存在则记录为待删除
	/// 发送函数: <see cref="BroadcastSceneRemove"/>
	/// </summary>
	public  void HandleSceneRemove(IDType senderId, DataReader reader) {
		var networkId = reader.GetULong();
		// 是主机 记录该玩家所在队伍的销毁项
		if (MPSteamworks.IsHost) {
			var teamName = RPManager.Instance.GetPlayerTeam(senderId);
			if (!_teamTombstones.TryGetValue(teamName, out var tombstones)) {
				tombstones = new HashSet<ulong>();
				_teamTombstones[teamName] = tombstones;
			}
			tombstones.Add(networkId);
		}

		// 执行本地销毁与遗忘
		ProcessSceneItemRemoval(networkId);
	}

	/// <summary>
	/// 接收主机补发的场景物品销毁表 Chunk (SceneRemoveChunk)
	/// 发送函数: <see cref="SendTombstoneChunksCoroutine"/>
	/// </summary>
	public  void HandleSceneRemoveChunk(DataReader reader) {
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
	public  void HandleSceneRemoveChunkRequest(IDType senderId) {
		if (!MPCore.CanSync || !MPSteamworks.IsHost || MPCore.Instance == null) return;
		if (senderId == 0 || senderId == MPSteamworks.UserSteamId) return;

		var teamName = RPManager.Instance.GetPlayerTeam(senderId);
		if (!_teamTombstones.TryGetValue(teamName, out var tombstones) || tombstones.Count == 0) return;
		// 停止旧协程
		if (_sendCoroutines.TryGetValue(senderId, out var coroutine) && coroutine != null)
			MPCore.Instance.StopCoroutine(coroutine);
		// 启动协程分帧发送
		_sendCoroutines[senderId] = MPCore.Instance.StartCoroutine(SendTombstoneChunksCoroutine(senderId, tombstones.ToList()));

	}

	/// <summary>
	/// 收到其他对等端发来的物品同步包时, 按 action 类型分发给对应处理函数.
	/// 由 MPPacketHandlers.HandleItemStateSync 调用.
	/// </summary>
	public void HandleItemState(IDType senderId, DataReader reader) {
		var action = (ItemSyncAction)reader.GetByte();
		try {
			switch (action) {
				case ItemSyncAction.SceneCreate:
					MPMain.LogDebug("[MP ItemSync] SceneCreate");
					HandleSceneCreate(senderId, reader);
					break;
				case ItemSyncAction.SceneRemove:
					MPMain.LogDebug("[MP ItemSync] SceneRemove");
					HandleSceneRemove(senderId, reader);
					break;
				case ItemSyncAction.SceneRemoveChunk:
					MPMain.LogDebug("[MP ItemSync] SceneRemoveChunk");
					HandleSceneRemoveChunk(reader);
					break;
				case ItemSyncAction.SceneRemoveChunkRequest:
					MPMain.LogDebug("[MP ItemSync] NeedRemoveChunk");
					HandleSceneRemoveChunkRequest(senderId);
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

public class DroppedItemModule: Singleton<DroppedItemModule>, ISyncModule {
	public const byte DROPPED_ITEM = 2;
	private Dictionary<ulong, NetworkedItem> _p2pItems = new();
	private ulong _nextLocalItemId = 1;     // 本地 P2P ID 自增计数器: 与 SteamId 组合确保全局唯一

	public Func<Item, bool> _inHandMethod =
		AccessTools.MethodDelegate<Func<Item, bool>>(AccessTools.Method(typeof(Item), "InHand"));

	#region[ISyncModule接口实现]

	public string ModuleName => "DroppedItemSync";

	/// <summary>
	/// 是否开启了物品同步
	/// </summary>
	public bool IsEnabled { get; set; }

	public void OnReset() {
		_p2pItems.Clear();
		_nextLocalItemId = 1;
	}

	// 没有联机情况 清空死亡生物记录和死亡生物记录发送协程
	public void OnLeave() {
		_p2pItems.Clear();
		_nextLocalItemId = 1;
	}

	public void OnEnd() => OnLeave();

	public void OnSyncUpdate(float deltaTime) { }

	#endregion

	#region[API]

	/// <summary>
	/// 本地玩家丢弃物品时调用 (由 Harmony 补丁 Patch_Inventory_DropItemIntoWorld_ItemSync 在 Postfix 触发).
	/// <para>
	/// 检查可同步性后调用 SyncAndBroadcast.
	/// SyncAndBroadcast 内部会判断是否已有 NetworkId (防止重复广播).
	/// </para>
	/// </summary>
	public  void NotifyLocalDrop(Item item) {
		if (item == null || !MPCore.CanSync) return;

		var itemObject = item.GetDropObject();
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
	public  void NotifyLocalPickup(NetworkedItem identity) {
		if (identity.ownerId == MPSteamworks.UserSteamId) {
			// 我是所有者: 直接广播 Remove 并且仅隐藏物品而不移除
			BroadcastPickupRemove(MPSteamworks.UserSteamId, identity.networkId, false);
		} else {
			// 他人所有: 乐观拾取, 向所有者申请所有权
			SendPickupRequest(identity.networkId, identity.ownerId);
		}
	}

	/// <summary>
	/// 脚本/触发器直接在本地生成一个同步的世界掉落物.
	/// 实例化物品后调用 SyncAndBroadcast 使其进入 P2P 网络.
	/// </summary>
	public  void SpawnSyncedWorldDrop(string prefabKey, Vector3 position, Quaternion rotation, Vector3 velocity) {
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
	public  NetworkedItem SyncAndBroadcast(Item_Object itemObject) {
		if (itemObject == null || itemObject.gameObject == null) return null;

		var identity = GetOrCreateIdentity(itemObject.gameObject);

		// 如果没有同步组件 (或 ID 为空), 当场进行 P2P 注册
		if (identity.networkId == 0 || identity.ownerId == default) {
			identity.networkId = MPUtil.Hash64($"{MPSteamworks.UserSteamId}:{_nextLocalItemId++}"); // SteamId 命名空间 + 本地自增 = 全局唯一
			identity.prefabKey = GetPrefabKey(itemObject);
			identity.ownerId = MPSteamworks.UserSteamId; // 此物品的首任所有者
			identity.sceneOrDropped = DROPPED_ITEM;
			identity.isRemote = false;

			_p2pItems[identity.networkId] = identity;
			MPMain.LogTest($"[MP ItemSync] SyncAndBroadcast - Created P2P Identity: {itemObject.name} → {identity.networkId}");
		} else {
			// 如果它已经有网络 ID, 必须确保它存在于追踪字典中
			if (!_p2pItems.ContainsKey(identity.networkId)) {
				identity.ownerId = MPSteamworks.UserSteamId; // 此物品的所有者
				_p2pItems[identity.networkId] = identity;
			}
		}

		// 广播 Create, 告知网络中所有对等端生成或注册此物体
		BroadcastDropCreate(identity, itemObject, identity.CurrentVelocity);
		return identity;
	}

	/// <summary>
	/// 广播全局销毁Item_Object 并在本地遗忘. 销毁函数由游戏本体执行
	/// <para>
	/// 若没有 NetworkedItem, 说明物品从未进入同步, 直接 Destroy 即可.
	/// <br/>
	/// 适用场景: 垃圾桶吞噬, 剧情强制扣除, 作弊指令清理等.
	/// </para>
	/// </summary>
	public  void DespawnAndBroadcast(Item_Object itemObject) {
		if (itemObject == null) return;

		var identity = itemObject.GetComponent<NetworkedItem>();
		if (identity != null && identity.networkId != 0) {
			MPMain.LogTest($"[MP ItemSync] DespawnAndBroadcast - Broadcasting global destruction: {identity.networkId}");
			BroadcastPickupRemove(MPProtocol.BroadcastId, identity.networkId);
			_p2pItems.Remove(identity.networkId);
			identity.networkId = 0;
		} else {
			itemObject.gameObject.SetActive(false);
			Object.Destroy(itemObject.gameObject); // 无网络身份, 直接销毁
		}
	}

	#endregion

	#region[网络数据发送]

	/// <summary>
	/// 向全网广播创建物品.
	/// 接收函数: <see cref="HandleDropCreate"/>
	/// </summary>
	private  void BroadcastDropCreate(NetworkedItem identity, Item_Object itemObject, Vector3 velocity) {
		if (identity == null || itemObject == null || identity.networkId == 0) return;

		var writer = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.DroppedItemStateSync);
		writer.Put((byte)ItemSyncAction.DropCreate);
		writer.Put(identity.networkId);
		writer.Put(identity.prefabKey);
		writer.Put(itemObject.transform.position);
		writer.Put(itemObject.transform.rotation);
		writer.Put(velocity);

		// 仅广播给物品同步队伍的玩家
		var playerIds = RPManager.Instance.GetPlayersMatchingRule(RuleType.SyncItem, true);
		foreach (var targetId in playerIds)
			MPSteamworks.Instance.SendToPeer(targetId, writer, SendType.Reliable);
	}

	/// <summary>
	/// 向物品所有者单播拾取申请.
	/// 接收函数: <see cref="HandlePickupRequest"/>
	/// </summary>
	private  void SendPickupRequest(ulong networkId, ulong ownerId) {
		var writer = GetWriter(MPSteamworks.UserSteamId, ownerId, PacketType.DroppedItemStateSync);
		writer.Put((byte)ItemSyncAction.PickupRequest);
		writer.Put(networkId);
		MPSteamworks.Instance.SendToPeer(ownerId, writer, SendType.Reliable);
	}

	/// <summary>
	/// 向拾取申请者单播拒绝消息.
	/// 接收函数: <see cref="HandlePickupReject"/>
	/// </summary>
	private  void SendPickupReject(ulong targetId, ulong networkId) {
		var writer = GetWriter(MPSteamworks.UserSteamId, targetId, PacketType.DroppedItemStateSync);
		writer.Put((byte)ItemSyncAction.PickupReject);
		writer.Put(networkId);
		MPSteamworks.Instance.SendToPeer(targetId, writer, SendType.Reliable);
	}

	/// <summary>
	/// 向启用物品同步的玩家广播移除物品.
	/// holderId 标识最终持有物品的一方: 收到此广播时若 holderId == 自身则跳过 ForceCleanup
	/// (因为持有者本地已在发包前完成了清理).
	/// <see cref="HandlePickupRemove"/>
	/// </summary>
	private  void BroadcastPickupRemove(IDType holderId, ulong networkId, bool destroyObject = true) {
		var writer = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.DroppedItemStateSync);
		writer.Put((byte)ItemSyncAction.PickupRemove);
		writer.Put(networkId);
		writer.Put(holderId);
		writer.Put(destroyObject);

		// 仅广播给物品同步队伍的玩家
		var playerIds = RPManager.Instance.GetPlayersMatchingRule(RuleType.SyncItem, true);
		foreach (var targetId in playerIds)
			MPSteamworks.Instance.SendToPeer(targetId, writer, SendType.Reliable);
	}

	#endregion

	#region[网络数据处理]

	/// <summary>
	/// 收到创建消息: 按优先级匹配候选或实例化新物品, 应用初始状态并写入追踪.
	/// 发送函数: <see cref="BroadcastDropCreate"/>
	/// </summary>
	public  void HandleDropCreate(IDType senderId, DataReader reader) {
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
			return;
		}

		// 是玩家丢弃产生的 p2p 物品, 直接实例化并注册网络身份
		var (itemObject, identity) = InstantiateWorldItem(prefabKey, position, rotation, networkId: networkId, ownerId: senderId);

		// 物品已经销毁或无法实例化, 直接忽略
		if (itemObject == null || identity == null) return;

		_p2pItems[networkId] = identity;
		identity.ApplyRemoteState(position, rotation, velocity);
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
	public  void HandlePickupRequest(ulong requesterId, DataReader reader) {
		var networkId = reader.GetULong();
		if (networkId == 0) return;

		MPMain.LogInfo($"[MP ItemSync] PickupRequest from {requesterId} for {networkId}");

		// 物品已不在我这里 (已被别人先拿), 拒绝申请
		if (!_p2pItems.TryGetValue(networkId, out var identity)
			|| identity == null) {
			SendPickupReject(requesterId, networkId);
			return;
		}

		// 物品在背包 拒绝申请
		if (identity.IsInLocalInventory(_inHandMethod)) {
			MPMain.LogWarning($"[MP ItemSync] PickupRequest denied: Item {networkId} is already in local inventory.");
			SendPickupReject(requesterId, networkId);
			return;
		}

		// 批准: 广播 Remove (holderId=申请者) + 本地遗忘
		if (identity.ownerId == MPSteamworks.UserSteamId) {
			BroadcastPickupRemove(requesterId, networkId, true);
			Forget(networkId);
		} else {
			// 所有权异常 (不应发生): 拒绝申请
			SendPickupReject(requesterId, networkId);
		}
	}

	/// <summary>
	/// 收到全局移除消息 (Remove): 执行双向清理.
	/// 发送函数: <see cref="BroadcastPickupRemove"/>
	/// <br/>
	/// holderId == 我: 我就是发起 Remove 的那方 (批准了别人的 PickupRequest 或自己拾起了自己的物品).
	/// <br/>
	/// holderId != 我: 他人拾起了物品, 执行 ForceCleanupItemPhysicalAndInventory.
	/// </summary>
	public  void HandlePickupRemove(IDType senderId, DataReader reader) {
		var networkId = reader.GetULong();
		var holderId = reader.GetULong();
		var shouldRemove = reader.GetBool();

		// 与目标队伍间没有启用物品同步
		if (!RPManager.Instance.GetPlayerRuleValue(senderId, RuleType.SyncItem)) return;

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
		ForceCleanupItemPhysicalAndInventory(networkId, shouldRemove);
	}

	/// <summary>
	/// 收到拾取拒绝 (PickupReject): 乐观拾取失败, 强制回滚背包中已装入的该物品数据.
	/// 执行背包清理与世界实体清除.
	/// 发送函数: <see cref="SendPickupReject"/>
	/// </summary>
	public  void HandlePickupReject(DataReader reader) {
		var networkId = reader.GetULong();
		if (networkId == 0) return;

		MPMain.LogWarning($"[MP ItemSync] PickupReject received! Rolling back inventory for {networkId}");
		ForceCleanupItemPhysicalAndInventory(networkId, true);
	}

	/// <summary>
	/// 收到其他对等端发来的物品同步包时, 按 action 类型分发给对应处理函数.
	/// 由 MPPacketHandlers.HandleItemStateSync 调用.
	/// </summary>
	public void HandleItemState(IDType senderId, DataReader reader) {
		var action = (ItemSyncAction)reader.GetByte();
		try {
			switch (action) {
				case ItemSyncAction.DropCreate:
					MPMain.LogDebug("[MP ItemSync] DropCreate");
					HandleDropCreate(senderId, reader);
					break;
				case ItemSyncAction.PickupRequest:
					MPMain.LogDebug("[MP ItemSync] PickupRequest");
					HandlePickupRequest(senderId, reader);
					break;
				case ItemSyncAction.PickupRemove:
					MPMain.LogDebug("[MP ItemSync] PickupRemove");
					HandlePickupRemove(senderId, reader);
					break;
				case ItemSyncAction.PickupReject:
					MPMain.LogDebug("[MP ItemSync] PickupReject");
					HandlePickupReject(reader);
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
		string prefabKey, Vector3 position, Quaternion rotation, ulong networkId = 0, ulong ownerId = 0
	) {
		if (!MPUtil.TryGetItemPrefab(prefabKey, out Item_Object prefab)) return (null, null);

		// 记录 Prefab 原始状态并临时关闭 Prefab
		bool originalActive = prefab.gameObject.activeSelf;
		prefab.gameObject.SetActive(false);

		// 克隆对象
		var itemComponent = Object.Instantiate(prefab, position, rotation);

		// 还原预制体状态
		prefab.gameObject.SetActive(originalActive);

		if (itemComponent == null) return (null, null);

		// 完成网络数据的配置
		var identity = GetOrCreateIdentity(itemComponent.gameObject);
		if (networkId != 0) identity.SetupIdentity(networkId, prefabKey, ownerId, DROPPED_ITEM, true);

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
	public void ForceCleanupItemPhysicalAndInventory(ulong networkId, bool shouldRemove) {
		if (networkId == 0) return;

		// 物体存在判断
		if (!_p2pItems.TryGetValue(networkId, out var identity) || identity == null) return;

		// 委派给组件自身完成清理
		identity.ForceCleanup(shouldRemove);

		if (shouldRemove) _p2pItems.Remove(networkId);
	}

	/// <summary>
	/// 从追踪中遗忘物品: 移除候选记录, 隐藏/销毁 GameObject, 从 _items 移除.
	/// <para>
	/// WasInstantiatedBySync=true: Destroy (同步创建的临时物体)
	/// WasInstantiatedBySync=false: 仅 SetActive(false) (场景原有/玩家本地丢弃产生的物体)
	/// </summary>
	private void Forget(ulong networkId) {
		if (!_p2pItems.TryGetValue(networkId, out var identity) || identity == null) return;

		var itemObject = identity.GetComponent<Item_Object>();

		if (identity.gameObject != null) {
			// 正常的场景遗忘清理
			identity.gameObject.SetActive(false);
			Object.Destroy(identity.gameObject);
		}
		_p2pItems.Remove(networkId);
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