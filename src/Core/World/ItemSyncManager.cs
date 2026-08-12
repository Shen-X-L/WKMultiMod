using HarmonyLib;
using Steamworks.Data;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using UnityEngine;
using WKMPMod.Component;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.NetWork;
using WKMPMod.Util;
using static Inventory;
using static WKMPMod.Data.MPWriterPool;
using Object = UnityEngine.Object;

namespace WKMPMod.World;

/// <summary>
/// 多人游戏物品同步操作类型 (P2P 协议标签).
/// </summary>
public enum ItemSyncAction : byte {
	SnapshotReset = 0,    // 快照重置: 新玩家加入时清空其本地状态, 准备接收快照
	SnapshotFinalize = 1, // 快照完成: 发送方告知所有物品已发送完毕, 接收方对齐场景
	Create = 2,           // 创建物品: 广播在指定位置生成/注册一个掉落物
	PickupRequest = 3,    // 拾取申请: 拾取非自己持有物品时, 单播给该物品的所有者申请所有权
	Remove = 5,           // 移除物品: 广播全局销毁掉落物 (同时清除世界物体与背包数据)
	PickupReject = 6,     // 拾取拒绝: 所有者确认物品已被别人抢先取走, 通知申请者回滚背包
}

/// <summary>
/// P2P 架构下的分布式世界物品同步管理器.
///
/// ── 核心设计原则 ───────────────────────────────────────────────────────────────
///
///   废除单一主机权威, 转为 "谁丢弃谁拥有 (OwnerId), 谁生成谁管理, 抢夺向所有者申请" 的扁平 P2P 模型:
///     • 任意一方都可以通过 SyncAndBroadcast 使一个物品进入网络同步.
///     • 任意一方都可以通过 DespawnAndBroadcast 将一个网络物品全局清除.
///     • 物品拥有 OwnerId: 通常是最后一次 "丢弃" 该物品的玩家, 决定了谁能批准别人拾取它.
///
/// ── 所有权与拾取协议 ──────────────────────────────────────────────────────────
///
///   情况 A: 拾取自己拥有的物品 (OwnerId == UserSteamId)
///     NotifyLocalPickup → BroadcastRemove(holderId=我) + 本地 Forget
///     收到此广播的其他人执行 ForceCleanupItemPhysicalAndInventory
///
///   情况 B: 拾取他人拥有的物品 (乐观拾取)
///     NotifyLocalPickup → SendPickupRequestToOwner
///     ┌ 获批: 所有者执行 HandlePickupRequest → BroadcastRemove(holderId=申请者) + 本地 Forget
///     │       全网执行 ForceCleanupItemPhysicalAndInventory (申请者因 holderId==我 自动跳过)
///     └ 拒绝: 所有者已确认物品被抢走 → HandlePickupRequest 发 PickupReject
///               申请者收到 HandlePickupReject → ForceCleanupItemPhysicalAndInventory 回滚背包
///
///   注意: "乐观拾取" 指客户端在收到批准之前就把物品加入背包. 如果被拒绝, API 3 会强制回滚.
///
/// ── 新玩家加入的快照协议 ──────────────────────────────────────────────────────
///
///   主机 (Host) 保留的唯一特殊职责是向新玩家发送初始状态快照:
///     Step 1: 主机发 SnapshotReset → 客户端清空状态, 重新捕获场景候选
///     Step 2: 主机逐帧发送所有已追踪物品的 Create (限速防卡顿)
///     Step 3: 主机发 SnapshotFinalize → 客户端隐藏未被匹配的场景候选 (主机认为已被拾取)
///
/// ── ApplyingRemoteState 递归保护 ──────────────────────────────────────────────
///
///   当本类正在应用远程状态 (实例化物品/写入 Transform/激活 GameObject) 时置 true.
///   Harmony 补丁的 NotifyLocalPickup / NotifyLocalDrop 检查此标志并提前 return,
///   防止我们主动触发的游戏回调再次进入同步链路形成循环.
/// </summary>
public static class ItemSyncManager {

	#region[常量]

	private const float CandidateMatchDistanceSqr = 0.5f;   // 场景候选匹配距离平方阈值 (约 0.7m 半径): 场景物品不移动, 阈值较紧
	private const float LocalDropMatchDistanceSqr = 25f;    // 本地丢弃防御匹配距离平方阈值 (约 5m 半径): 物品可能在 RTT 内移动
	private const float LocalDropMaxAge = 3f;               // 本地丢弃记录最大存活时间 (秒), 超期自动清理防止陈旧记录污染
	private const float VelocityEpsilonSqr = 0.0025f;       // 速度零阈值平方 (约 0.05 m/s), 低于此值视为静止不发送速度
	private const float StableIdPositionPrecision = 20f;    // 稳定场景 ID 位置量化精度 (×精度后取整, 精度 20 ≈ 0.05m 分辨率)
	private const float StableIdRotationPrecision = 5f;     // 稳定场景 ID 旋转量化精度 (精度 5 ≈ 0.2° 分辨率)
	private const int SnapshotItemsPerFrame = 10;           // 快照协议每帧最多发送/注册物品数量, 防止大批量物品导致帧率下降

	#endregion

	#region[静态字段]

	// ── 反射绑定 ──────────────────────────────────────────────────────────────
	// Item.dropObject 是私有字段: 持有对应 Item_Object 的引用, 必须通过反射读写.
	// Item.InHand 是私有方法: 检查物品是否正在被玩家手持, 供 ForceCleanupItemPhysicalAndInventory 使用.
	private static readonly Func<Item, bool> _inHand =
		AccessTools.MethodDelegate<Func<Item, bool>>(AccessTools.Method(typeof(Item), "InHand"));

	// ── 追踪状态 ──────────────────────────────────────────────────────────────
	private static readonly Dictionary<string, NetworkedItem> _items = new();      // 全局物品追踪字典: NetworkId → NetworkedItem
	private static readonly List<Item_Object> _clientCandidates = new();           // 候选物品列表: 本地已存在但尚未匹配到任何 Create 的物品
	private static readonly List<Item_Object> _snapshotCandidates = new();         // 快照候选子集: SnapshotFinalize 后对未匹配者执行隐藏对齐
	private static readonly Dictionary<ulong, Coroutine> _snapshotRoutines = new(); // 快照发送协程字典: clientId → 协程引用, 防止重复发送

	// ── 控制状态 ──────────────────────────────────────────────────────────────
	private static ulong _nextLocalItemId = 1;     // 本地 P2P ID 自增计数器: 与 SteamId 组合确保全局唯一
	private static Coroutine _prepareRoutine;       // PrepareWorldRoutine 协程引用, ResetState 时停止
	private static Coroutine _hostDiscoveryRoutine; // HostDiscoveryRoutine 协程引用 (定期扫描场景新物品)
	private static bool _hostSceneItemsRegistered;  // 主机是否已完成场景物品初次注册 (快照发送的前置条件)

	#endregion

	#region[公共属性]

	/// <summary>
	/// 是否正在应用远程状态.
	/// true 期间: Harmony 补丁对 NotifyLocalPickup/NotifyLocalDrop 的调用会被跳过,
	/// 防止应用远程状态时触发的游戏回调再次进入同步链路.
	/// </summary>
	public static bool ApplyingRemoteState { get; private set; }

	#endregion

	#region[生命周期接口]

	/// <summary>
	/// 世界初始化完成时调用. 重置所有状态, 启动世界准备协程.
	/// 由 Harmony 补丁 Patch_WorldLoader_Initialize_ItemSync 在 WorldLoader.Initialize Postfix 触发.
	/// </summary>
	public static void NotifyWorldInitialized() {
		ResetState();
		if (MPCore.Instance == null) return;
		_prepareRoutine = MPCore.Instance.StartCoroutine(PrepareWorldRoutine());
	}

	/// <summary>
	/// 重置所有状态: 停止协程, 销毁同步创建的物品, 清空所有追踪集合.
	/// 在断线, 地图切换或快照重置时调用.
	/// </summary>
	public static void ResetState() {
		// Step 1: 停止所有相关协程
		if (_prepareRoutine != null && MPCore.Instance != null) {
			MPCore.Instance.StopCoroutine(_prepareRoutine);
			_prepareRoutine = null;
		}
		if (_hostDiscoveryRoutine != null && MPCore.Instance != null) {
			MPCore.Instance.StopCoroutine(_hostDiscoveryRoutine);
			_hostDiscoveryRoutine = null;
		}
		if (MPCore.Instance != null) {
			foreach (var routine in _snapshotRoutines.Values) {
				if (routine != null) MPCore.Instance.StopCoroutine(routine);
			}
		}
		_snapshotRoutines.Clear();

		// Step 2: 销毁所有由同步系统实例化的物品 (场景原有物品不销毁)
		foreach (var identity in _items.Values) {
			if (identity == null || identity.gameObject == null) continue;
			if (!identity.WasInstantiatedBySync) continue;
			Object.Destroy(identity.gameObject);
		}

		// Step 3: 清空所有集合与标志
		_items.Clear();
		_clientCandidates.Clear();
		_snapshotCandidates.Clear();

		_hostSceneItemsRegistered = false;
		ApplyingRemoteState = false;

		MPMain.LogInfo("[P2P ItemSync] ResetState completed.");
	}

	/// <summary>
	/// 向指定新玩家发送完整物品快照 (仅主机调用, 在玩家连接时由 MPCore.HandlePlayerConnected 触发).
	/// 若已有快照协程正在运行, 停止旧协程再启动新的, 保证客户端收到最新状态.
	/// </summary>
	public static void SendSnapshotToClient(ulong clientId) {
		if (!MPCore.CanSync || !MPSteamworks.Instance.IsHost || MPCore.Instance == null) return;
		if (clientId == 0 || clientId == MPSteamworks.UserSteamId) return;

		if (_snapshotRoutines.TryGetValue(clientId, out var existingRoutine) && existingRoutine != null)
			MPCore.Instance.StopCoroutine(existingRoutine);

		_snapshotRoutines[clientId] = MPCore.Instance.StartCoroutine(SendSnapshotToClientRoutine(clientId));
	}

	/// <summary>
	/// 脚本/触发器直接在本地生成一个同步的世界掉落物.
	/// 实例化物品后调用 SyncAndBroadcast 使其进入 P2P 网络.
	/// </summary>
	public static void SpawnSyncedWorldDrop(string prefabKey, Vector3 position, Quaternion rotation, Vector3 velocity) {
		if (ApplyingRemoteState || !MPCore.CanSync || string.IsNullOrWhiteSpace(prefabKey)) return;

		var itemObject = InstantiateWorldItem(prefabKey, position, rotation);
		if (itemObject == null) return;

		SyncAndBroadcast(itemObject);
	}

	#endregion

	#region[P2P 核心 API]

	/// <summary>
	/// API 1: 为 Item_Object 赋予网络身份并广播创建.
	/// <para>
	/// 若该物体已有 NetworkedItem 组件且 NetworkId 非空 (说明已经在网络中), 直接复用并重新广播.
	/// 若没有 (本地新物品), 添加组件, 分配 "{UserSteamId}:p2p:{自增ID}", 设置 OwnerId = 我, 广播 Create.
	/// <br/>
	/// 适用场景: 玩家丢弃物品, 关卡触发器生成, 临时联网化黑名单道具等.
	/// </para>
	/// </summary>
	/// <returns>NetworkedItem 同步组件, 失败返回 null</returns>
	public static NetworkedItem SyncAndBroadcast(Item_Object itemObject) {
		if (itemObject == null || itemObject.gameObject == null) return null;

		var identity = itemObject.GetComponent<NetworkedItem>();

		// 如果没有同步组件 (或 ID 为空), 当场进行 P2P 注册
		if (identity == null || string.IsNullOrEmpty(identity.NetworkId) || identity.OwnerId == default) {
			identity = GetOrCreateIdentity(itemObject.gameObject);
			identity.NetworkId = $"{MPSteamworks.UserSteamId}:p2p:{_nextLocalItemId++}"; // SteamId 命名空间 + 本地自增 = 全局唯一
			identity.PrefabKey = GetPrefabKey(itemObject);
			identity.OwnerId = MPSteamworks.UserSteamId; // 我是此物品的首任所有者
			identity.IsRemote = false;
			identity.WasInstantiatedBySync = false;

			_items[identity.NetworkId] = identity;
			MPMain.LogTest($"[P2P ItemSync] SyncAndBroadcast - Created P2P Identity: {itemObject.name} → {identity.NetworkId}");
		} else {
			// [关键修复] 防御性修复: 如果它已经有网络 ID, 必须确保它存在于追踪字典中
			// 否则本地丢出一个携带旧 ID 的物品时, 别人申请拾取会报错
			if (!_items.ContainsKey(identity.NetworkId)) {
				_items[identity.NetworkId] = identity;
			}
		}

		// 广播 Create, 告知网络中所有对等端生成或注册此物体
		BroadcastCreate(identity, itemObject, GetVelocity(itemObject));
		return identity;
	}

	/// <summary>
	/// API 2: 若 Item_Object 拥有网络身份, 广播全局销毁并在本地遗忘.
	/// <para>
	/// 若没有 NetworkedItem, 说明物品从未进入同步, 直接 Destroy 即可.
	/// <br/>
	/// 适用场景: 垃圾桶吞噬, 剧情强制扣除, 作弊指令清理等.
	/// </para>
	/// </summary>
	public static void DespawnAndBroadcast(Item_Object itemObject) {
		if (itemObject == null) return;

		var identity = itemObject.GetComponent<NetworkedItem>();
		if (identity != null && !string.IsNullOrEmpty(identity.NetworkId)) {
			MPMain.LogTest($"[P2P ItemSync] DespawnAndBroadcast - Broadcasting global destruction: {identity.NetworkId}");
			BroadcastRemove(MPProtocol.BroadcastId, identity.NetworkId);
			Forget(identity.NetworkId);
		} else {
			Object.Destroy(itemObject.gameObject); // 无网络身份, 直接销毁
		}
	}

	/// <summary>
	/// API 3: 强制双向销毁 — 清除世界 Item_Object 实体 + 清除背包 Item 数据.
	/// <para>
	/// 触发场景:
	///   • HandleRemove: 收到全网广播销毁时
	///   • HandlePickupReject: 乐观拾取被所有者拒绝, 回滚背包数据
	/// <br/>
	/// 背包清理逻辑: 扫描 Inventory.bagItems, 找到 dropObject.NetworkId == networkId 的条目:
	///   - 从列表移除
	///   - 若手持该物品 (InHand), 调用 ClearItemFromHand 清除手持模型
	///   - 置 hasBeenDestroyed = true 触发物品自身的销毁回调
	///   - 调用 RescanInventory 刷新 UI 背包格子
	/// <br/>
	/// 世界清理逻辑: 从 _items 获取 Item_Object, 解除 Item.dropObject 引用绑定, SetActive(false) 并 Destroy.
	/// 无论是场景原有物品还是同步实例化的物品, 双向销毁时都执行 Destroy (强制对齐).
	/// </para>
	/// </summary>
	public static void ForceCleanupItemPhysicalAndInventory(string networkId) {
		if (string.IsNullOrEmpty(networkId)) return;

		MPMain.LogTest($"[P2P ItemSync] ForceCleanup - Double-Destruction: {networkId}");
		// 背包清理
		var inventory = ENT_Player.GetInventory();
		// 清理玩家当前正拿在手里的物品
		if (inventory?.itemHands != null) {
			foreach (var handSlot in inventory.itemHands) {
				if (handSlot == null || handSlot.currentItem == null) continue;

				var handItem = handSlot.currentItem;
				var dropObj = handItem.GetDropObject(false);
				if (dropObj == null) continue;

				var identity = dropObj.GetComponent<NetworkedItem>();
				if (identity == null || identity.NetworkId != networkId) continue;
				inventory.ClearItemFromHand(handItem);
				handItem.hasBeenDestroyed = true;
				MPMain.LogWarning($"[P2P ItemSync] API 3 - Removed stale/rejected item '{handItem.itemName}' directly from Player Hands!");
			}
		}
		// 清理背包内的物品
		if (inventory?.bagItems != null) {
			for (int i = inventory.bagItems.Count - 1; i >= 0; i--) {
				var bagItem = inventory.bagItems[i];
				if (bagItem == null) continue;

				var dropObj = bagItem.GetDropObject(false);
				if (dropObj == null) continue;

				var dropIdentity = dropObj.GetComponent<NetworkedItem>();
				if (dropIdentity == null || dropIdentity.NetworkId != networkId) continue;
				inventory.bagItems.RemoveAt(i);
				bagItem.hasBeenDestroyed = true;

				MPMain.LogTest($"[P2P ItemSync] ForceCleanup - Evicted '{bagItem.itemName}' from inventory.");
			}
		}
		// 清理额外的背包袋子 (Pouch) 内的物品
		if (inventory?.extraPouches != null) {
			foreach (Pouch pouch in inventory.extraPouches) {
				if (pouch == null) continue;
				for (int i = pouch.pouchItems.Count - 1; i >= 0; i--) {
					var pouchItem = pouch.pouchItems[i];
					if (pouchItem == null) continue;

					var dropObj = pouchItem.GetDropObject(false);
					if (dropObj == null) continue;

					var dropIdentity = dropObj.GetComponent<NetworkedItem>();
					if (dropIdentity == null || dropIdentity.NetworkId != networkId) continue;
					pouch.pouchItems.RemoveAt(i);
					pouchItem.hasBeenDestroyed = true;

					MPMain.LogTest($"[P2P ItemSync] ForceCleanup - Evicted '{pouchItem.itemName}' from inventory.");
				}
			}
		}
		// 清理额外的背包口袋 (Pocket) 内的物品
		if (inventory?.pockets != null) {
			foreach (Pocket pocket in inventory.pockets) {
				if (pocket == null||pocket.pouch == null) continue;
				for (int i = pocket.pouch.pouchItems.Count - 1; i >= 0; i--) {
					var pouchItem = pocket.pouch.pouchItems[i];
					if (pouchItem == null) continue;

					var dropObj = pouchItem.GetDropObject(false);
					if (dropObj == null) continue;

					var dropIdentity = dropObj.GetComponent<NetworkedItem>();
					if (dropIdentity == null || dropIdentity.NetworkId != networkId) continue;
					pocket.pouch.pouchItems.RemoveAt(i);
					pouchItem.hasBeenDestroyed = true;

					MPMain.LogTest($"[P2P ItemSync] ForceCleanup - Evicted '{pouchItem.itemName}' from inventory.");
				}
			}
		}

		inventory?.RescanInventory(); // 通知 UI 刷新背包格子

		// 世界物体清理
		if (!_items.TryGetValue(networkId, out var networkedItem) || networkedItem == null) {
			return;
		}

		var itemObject = networkedItem.GetComponent<Item_Object>();
		if (itemObject != null) {
			RemoveCandidate(itemObject);
			// 直接调用销毁
			if (itemObject.gameObject != null) {
				itemObject.gameObject.SetActive(false);
				Object.Destroy(itemObject.gameObject);
			}
		}

		_items.Remove(networkId);
	}

	/// <summary>
	/// 本地玩家拾取物品时调用 (由 Harmony 补丁 Patch_Item_Object_Pickup_ItemSync 在 Postfix 触发).
	/// <para>
	/// 若物品无 NetworkedItem (从未同步), 直接放行无需处理.
	/// <br/>
	/// OwnerId == 我: 我有完全所有权 → 直接广播 Remove + 本地 Forget.
	/// OwnerId == 他人: 采用乐观拾取 → 发送 PickupRequest 给所有者.
	///   - 批准: 所有者广播 Remove, 全网清理 (我因 holderId==我 自动跳过 ForceCleanup)
	///   - 拒绝: 收到 PickupReject → ForceCleanupItemPhysicalAndInventory 回滚背包
	/// </para>
	/// </summary>
	public static void NotifyLocalPickup(Item_Object itemObject) {
		if (ApplyingRemoteState || itemObject == null || !MPCore.CanSync) return;

		var identity = itemObject.GetComponent<NetworkedItem>();
		if (identity == null || string.IsNullOrEmpty(identity.NetworkId)) return; // 无网络身份, 纯本地物品

		MPMain.LogInfo($"[P2P ItemSync] LocalPickup: {itemObject.name}, ID={identity.NetworkId}, Owner={identity.OwnerId}");

		if (identity.OwnerId == MPSteamworks.UserSteamId) {
			// 我是所有者: 直接广播 Remove 并本地遗忘
			BroadcastRemove(MPSteamworks.UserSteamId, identity.NetworkId);
			Forget(identity.NetworkId);
		} else {
			// 他人所有: 乐观拾取, 向所有者申请所有权
			SendPickupRequestToOwner(identity.NetworkId, identity.OwnerId);
		}
	}

	/// <summary>
	/// 本地玩家丢弃物品时调用 (由 Harmony 补丁 Patch_Inventory_DropItemIntoWorld_ItemSync 在 Postfix 触发).
	/// <para>
	/// 通过反射从 Item 获取 dropObject (Item_Object), 检查可同步性后调用 SyncAndBroadcast.
	/// SyncAndBroadcast 内部会判断是否已有 NetworkId (防止重复广播).
	/// </para>
	/// </summary>
	public static void NotifyLocalDrop(Item item) {
		if (ApplyingRemoteState || item == null || !MPCore.CanSync) return;

		var itemObject = item.GetDropObject();
		if (!IsSyncableDropItem(itemObject)) return;
		if (itemObject.TryGetComponent<ObjectTagger>(out var tagger) && IsBlacklisted(tagger)) return;

		SyncAndBroadcast(itemObject);
	}

	#endregion

	#region[网络包路由]

	/// <summary>
	/// 收到其他对等端发来的物品同步包时, 按 action 类型分发给对应处理函数.
	/// 由 MPPacketHandlers.HandleItemStateSync 调用.
	/// </summary>
	public static void HandleItemState(IDType senderId, DataReader reader) {
		var action = (ItemSyncAction)reader.GetByte();
		try {
			switch (action) {
				case ItemSyncAction.SnapshotReset: MPMain.LogDebug("[MP ItemSync] HandleSnapshotReset"); HandleSnapshotReset(); break;
				case ItemSyncAction.SnapshotFinalize: MPMain.LogDebug("[MP ItemSync] HandleSnapshotFinalize"); HandleSnapshotFinalize(); break;
				case ItemSyncAction.Create: MPMain.LogDebug("[MP ItemSync] HandleCreate"); HandleCreate(senderId, reader); break;
				case ItemSyncAction.PickupRequest: MPMain.LogDebug("[MP ItemSync] HandlePickupRequest"); HandlePickupRequest(senderId, reader); break;
				case ItemSyncAction.Remove: MPMain.LogDebug("[MP ItemSync] HandleRemove"); HandleRemove(reader); break;
				case ItemSyncAction.PickupReject: MPMain.LogDebug("[MP ItemSync] HandlePickupReject"); HandlePickupReject(reader); break;
			}
		} catch (Exception e) {
			MPMain.LogError($"[P2P ItemSync] HandleItemState failed for action {action}: {e.Message}");
		}
	}

	#endregion

	#region[网络包处理]

	/// <summary>
	/// 收到快照重置: 清空本地状态并重新捕获场景候选 (仅非主机执行).
	/// 快照协议的第 1 步, 为后续接收 Create 消息做准备.
	/// </summary>
	private static void HandleSnapshotReset() {
		if (MPSteamworks.Instance.IsHost) return;
		ResetState();
		CaptureSceneCandidates(snapshotCandidate: true);
	}

	/// <summary>
	/// 收到快照完成: 隐藏所有未被快照中任何 Create 消息匹配的场景候选物品 (仅非主机执行).
	/// <para>
	/// 快照协议的第 3 步. 逻辑: 快照期间每收到一个 Create 就从 _snapshotCandidates 中移除匹配项.
	/// SnapshotFinalize 时仍留在列表中的物品 = 主机认为已被拾取 → 本地隐藏以对齐主机状态.
	/// 若候选已有 NetworkId 且在 _items 中 (已被精确匹配), 则不隐藏.
	/// </para>
	/// </summary>
	private static void HandleSnapshotFinalize() {
		if (MPSteamworks.Instance.IsHost) return;

		int hidden = 0;
		for (int i = _snapshotCandidates.Count - 1; i >= 0; i--) {
			var candidate = _snapshotCandidates[i];
			if (candidate == null || candidate.gameObject == null) continue;

			var identity = candidate.GetComponent<NetworkedItem>();
			if (identity != null && !string.IsNullOrEmpty(identity.NetworkId) && _items.ContainsKey(identity.NetworkId))
				continue; // 已被精确匹配, 不隐藏

			candidate.gameObject.SetActive(false);
			hidden++;
		}
		_snapshotCandidates.Clear();
	}

	/// <summary>
	/// 收到创建消息: 按优先级匹配候选或实例化新物品, 应用初始状态并写入追踪.
	/// <para>
	/// 匹配优先级:
	///   1. isDropSpawn=true → FindPendingLocalDrop (防御性匹配本地已有的同名丢弃物)
	///   2. StableSceneId 精确匹配 (场景物品有确定性 ID, 直接找到对应实例)
	///   3. _clientCandidates 按 prefabKey + 距离模糊匹配
	///   4. 无匹配 → InstantiateWorldItem (WasInstantiatedBySync=true)
	/// <br/>
	/// skipDropCallbacks: 若候选不是快照候选 (wasSnapshotCandidate=false) 且是已有候选且是丢弃生成,
	/// 说明本地已执行过 OnDrop(), 跳过重复调用.
	/// </para>
	/// </summary>
	private static void HandleCreate(ulong senderId, DataReader reader) {
		var networkId = reader.GetString();
		var prefabKey = reader.GetString();
		var position = reader.GetVector3();
		var rotation = reader.GetQuaternion();
		var velocity = reader.GetVector3();
		if (string.IsNullOrEmpty(networkId) || string.IsNullOrEmpty(prefabKey)) return;
		// 已追踪过此 ID: 直接刷新状态 (延迟/重复消息)
		if (_items.TryGetValue(networkId, out var existing) && existing != null) {
			existing.OwnerId = senderId;
			ApplyCreate(existing, position, rotation, velocity);
			return;
		}
		// 候选匹配
		bool wasSnapshotCandidate = false;

		Item_Object candidate = FindSceneItemByStableId(networkId);
		if (candidate != null) {
			wasSnapshotCandidate = _snapshotCandidates.Contains(candidate);
			RemoveCandidate(candidate);
		} else if (candidate == null){
			candidate = FindClientCandidate(prefabKey, position, out wasSnapshotCandidate);
		}

		var itemObject = candidate;
		bool instantiatedBySync = false;
		if (itemObject == null) {
			itemObject = InstantiateWorldItem(prefabKey, position, rotation);
			instantiatedBySync = itemObject != null;
		}
		if (itemObject == null) return;
		// 配置同步身份
		var identity = GetOrCreateIdentity(itemObject.gameObject);
		if (networkId.StartsWith("sceneitem:", StringComparison.Ordinal))
			identity.StableSceneId = networkId;
		identity.NetworkId = networkId;
		identity.PrefabKey = prefabKey;
		identity.OwnerId = senderId;
		identity.IsRemote = true;
		identity.WasInstantiatedBySync = instantiatedBySync;

		_items[networkId] = identity;

		ApplyCreate(identity, position, rotation, velocity);
	}

	/// <summary>
	/// 收到拾取申请 (PickupRequest): 判断我是否是该物品的所有者并决定批准或拒绝.
	/// <para>
	/// 批准条件: _items 中有此物品 且 OwnerId == 我.
	///   → BroadcastRemove(holderId=申请者): 全网清理 (申请者因 holderId==其自身 自动跳过 ForceCleanup)
	///   → 本地 Forget
	/// <br/>
	/// 拒绝条件: _items 中无此物品 (已被别人先拿) 或 OwnerId != 我 (所有权信息不一致).
	///   → SendPickupReject: 申请者收到后执行 ForceCleanup 回滚背包
	/// <br/>
	/// 注意: 包是单播给所有者的, 正常情况下 OwnerId==我 恒成立. OwnerId!=我 属于异常边界情况.
	/// </para>
	/// </summary>
	private static void HandlePickupRequest(ulong requesterId, DataReader reader) {
		var networkId = reader.GetString();
		if (string.IsNullOrEmpty(networkId)) return;

		MPMain.LogInfo($"[P2P ItemSync] PickupRequest from {requesterId} for {networkId}");

		if (!_items.TryGetValue(networkId, out var identity)) {
			// 物品已不在我这里 (已被别人先拿), 拒绝申请
			SendPickupReject(requesterId, networkId);
			return;
		}

		if (IsItemInInventory(identity)) {
			MPMain.LogWarning($"[P2P ItemSync] PickupRequest denied: Item {networkId} is already in local inventory.");
			SendPickupReject(requesterId, networkId);
			return;
		}

		if (identity.OwnerId == MPSteamworks.UserSteamId) {
			// 批准: 广播 Remove (holderId=申请者) + 本地遗忘
			BroadcastRemove(requesterId, networkId);
			Forget(networkId);
		} else {
			// 所有权异常 (不应发生): 拒绝申请
			SendPickupReject(requesterId, networkId);
		}
	}

	/// <summary>
	/// 收到拾取拒绝 (PickupReject): 乐观拾取失败, 强制回滚背包中已装入的该物品数据.
	/// 通过 API 3 (ForceCleanupItemPhysicalAndInventory) 执行背包清理与世界实体清除.
	/// </summary>
	private static void HandlePickupReject(DataReader reader) {
		var networkId = reader.GetString();
		if (string.IsNullOrEmpty(networkId)) return;

		MPMain.LogWarning($"[P2P ItemSync] PickupReject received! Rolling back inventory for {networkId}");
		ForceCleanupItemPhysicalAndInventory(networkId);
	}

	/// <summary>
	/// 收到全局移除消息 (Remove): 执行 API 3 双向清理.
	/// <para>
	/// holderId == 我: 我就是发起 Remove 的那方 (批准了别人的 PickupRequest 或自己拾起了自己的物品).
	/// holderId != 我: 他人拾起了物品, 执行 ForceCleanupItemPhysicalAndInventory.
	/// </para>
	/// </summary>
	private static void HandleRemove(DataReader reader) {
		var networkId = reader.GetString();
		var holderId = reader.GetULong();

		if (string.IsNullOrEmpty(networkId)) return;
		if (holderId == MPSteamworks.UserSteamId) {
			// 物品已经在背包里了,跳过双向清理,但必须调用 Forget 洗白它的网络身份
			// 否则下次扔出来时,依然附带旧的 OwnerId,导致别人的拾取申请被拒
			Forget(networkId);
			return;
		}

		ForceCleanupItemPhysicalAndInventory(networkId);
	}

	#endregion

	#region[网络包发送]

	/// <summary>向物品所有者单播拾取申请.</summary>
	private static void SendPickupRequestToOwner(string networkId, ulong ownerId) {
		var writer = GetWriter(MPSteamworks.UserSteamId, ownerId, PacketType.ItemStateSync);
		writer.Put((byte)ItemSyncAction.PickupRequest);
		writer.Put(networkId);
		MPSteamworks.Instance.SendToPeer(ownerId, writer, SendType.Reliable);
	}

	/// <summary>向拾取申请者单播拒绝消息.</summary>
	private static void SendPickupReject(ulong targetId, string networkId) {
		var writer = GetWriter(MPSteamworks.UserSteamId, targetId, PacketType.ItemStateSync);
		writer.Put((byte)ItemSyncAction.PickupReject);
		writer.Put(networkId);
		MPSteamworks.Instance.SendToPeer(targetId, writer, SendType.Reliable);
	}

	/// <summary>向全网广播创建物品.</summary>
	private static void BroadcastCreate(NetworkedItem identity, Item_Object itemObject, Vector3 velocity) {
		if (identity == null || itemObject == null || string.IsNullOrEmpty(identity.NetworkId)) return;

		var writer = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.ItemStateSync);
		writer.Put((byte)ItemSyncAction.Create);
		writer.Put(identity.NetworkId);
		writer.Put(identity.PrefabKey);
		writer.Put(itemObject.transform.position);
		writer.Put(itemObject.transform.rotation);
		writer.Put(velocity);
		MPSteamworks.Instance.Broadcast(writer, SendType.Reliable);
	}

	/// <summary>
	/// 向全网广播移除物品.
	/// holderId 标识最终持有物品的一方: 收到此广播时若 holderId == 自身则跳过 ForceCleanup
	/// (因为持有者本地已在发包前完成了清理).
	/// </summary>
	private static void BroadcastRemove(IDType holderId, string networkId) {
		var writer = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.ItemStateSync);
		writer.Put((byte)ItemSyncAction.Remove);
		writer.Put(networkId);
		writer.Put(holderId);
		MPSteamworks.Instance.Broadcast(writer, SendType.Reliable);
	}

	/// <summary>向指定客户端单播快照重置消息.</summary>
	private static void SendSnapshotReset(ulong clientId) {
		var writer = GetWriter(MPSteamworks.UserSteamId, clientId, PacketType.ItemStateSync);
		writer.Put((byte)ItemSyncAction.SnapshotReset);
		MPSteamworks.Instance.SendToPeer(clientId, writer);
	}

	/// <summary>向指定客户端单播快照完成消息.</summary>
	private static void SendSnapshotFinalize(ulong clientId) {
		var writer = GetWriter(MPSteamworks.UserSteamId, clientId, PacketType.ItemStateSync);
		writer.Put((byte)ItemSyncAction.SnapshotFinalize);
		MPSteamworks.Instance.SendToPeer(clientId, writer);
	}

	/// <summary>向指定客户端单播单个物品的创建消息 (快照协议使用).</summary>
	private static void SendCreate(ulong clientId, NetworkedItem identity, Item_Object itemObject, Vector3 velocity) {
		if (identity == null || itemObject == null || string.IsNullOrEmpty(identity.NetworkId)) return;

		var writer = GetWriter(MPSteamworks.UserSteamId, clientId, PacketType.ItemStateSync);
		writer.Put((byte)ItemSyncAction.Create);
		writer.Put(identity.NetworkId);
		writer.Put(identity.PrefabKey);
		writer.Put(itemObject.transform.position);
		writer.Put(itemObject.transform.rotation);
		writer.Put(velocity);
		MPSteamworks.Instance.SendToPeer(clientId, writer);
	}

	#endregion

	#region[世界生命周期协程]

	/// <summary>
	/// 世界准备协程: 等待世界加载完成后初始化物品同步.
	/// <para>
	/// 两次 yield null: 让 WorldLoader 的其他脚本在同帧 Update 中完成初始化,
	/// 确保所有 Item_Object 都处于就绪状态再开始扫描.
	/// <br/>
	/// 主机路径: 分帧注册所有场景物品 → 启动 HostDiscoveryRoutine 定期扫描新物品
	/// 客户端路径: 分帧捕获所有场景物品为候选, 待快照 Create 到来时匹配
	/// </para>
	/// </summary>
	private static IEnumerator PrepareWorldRoutine() {
		yield return new WaitUntil(() => WorldLoader.isLoaded && WorldLoader.initialized);
		yield return null;
		yield return null;

		if (MPSteamworks.Instance.IsHost) {
			yield return RegisterHostSceneItemsRoutine();
			if (MPCore.Instance != null && _hostDiscoveryRoutine == null)
				_hostDiscoveryRoutine = MPCore.Instance.StartCoroutine(HostDiscoveryRoutine());
		} else if (MPCore.IsInLobby) {
			CaptureSceneCandidates(snapshotCandidate: true);
		}
	}

	/// <summary>
	/// 快照发送协程: 向新玩家发送当前所有已追踪世界物品 (限速防卡顿).
	/// <para>
	/// 协议顺序: SnapshotReset → 逐帧 Create × N → SnapshotFinalize
	/// 若主机场景物品注册未完成 (RegisterHostSceneItemsRoutine), 先等待完成再发.
	/// </para>
	/// </summary>
	private static IEnumerator SendSnapshotToClientRoutine(ulong clientId) {
		yield return new WaitUntil(() => WorldLoader.isLoaded && WorldLoader.initialized);
		if (!_hostSceneItemsRegistered) yield return RegisterHostSceneItemsRoutine();

		SendSnapshotReset(clientId);
		var snapshot = new List<NetworkedItem>(EnumerateKnownHostWorldItems());
		int sentThisFrame = 0;

		foreach (var identity in snapshot) {
			if (identity == null || identity.gameObject == null) continue;
			var itemObject = identity.GetComponent<Item_Object>();
			if (!IsSyncableWorldItem(itemObject)) continue;

			SendCreate(clientId, identity, itemObject, GetVelocity(itemObject));
			if (++sentThisFrame >= SnapshotItemsPerFrame) { sentThisFrame = 0; yield return null; }
		}

		SendSnapshotFinalize(clientId);
		_snapshotRoutines.Remove(clientId);
	}

	/// <summary>
	/// 主机注册场景物品协程: 分帧扫描并注册当前场景中所有可同步物品.
	/// 注册后将 _hostSceneItemsRegistered 置 true, 解锁快照发送.
	/// </summary>
	private static IEnumerator RegisterHostSceneItemsRoutine() {
		if (!MPSteamworks.Instance.IsHost) yield break;

		int registeredThisFrame = 0, totalRegistered = 0;
		foreach (var itemObject in EnumerateSceneItems()) {
			var prefabKey = GetPrefabKey(itemObject);
			if (string.IsNullOrEmpty(prefabKey)) continue;

			RegisterHostItem(itemObject, prefabKey, preferStableSceneIdentity: true);
			totalRegistered++;

			if (++registeredThisFrame >= SnapshotItemsPerFrame) { registeredThisFrame = 0; yield return null; }
		}
		_hostSceneItemsRegistered = true;
	}

	/// <summary>
	/// 主机发现协程: 每 0.5 秒扫描场景, 发现并广播新出现的未追踪物品 (例如游戏脚本动态生成的).
	/// </summary>
	private static IEnumerator HostDiscoveryRoutine() {
		var wait = new WaitForSecondsRealtime(0.5f);
		while (MPCore.Instance != null) {
			if (MPCore.CanSync && MPSteamworks.Instance.IsHost && _hostSceneItemsRegistered)
				DiscoverAndBroadcastNewHostWorldItems();
			yield return wait;
		}
		_hostDiscoveryRoutine = null;
	}

	/// <summary>发现并广播场景中新出现的未注册物品.</summary>
	private static void DiscoverAndBroadcastNewHostWorldItems() {
		foreach (var itemObject in EnumerateSceneItems()) {
			if (!TryPrepareNewHostWorldItem(itemObject, out var prefabKey)) continue;
			var identity = RegisterHostItem(itemObject, prefabKey, preferStableSceneIdentity: true);
			BroadcastCreate(identity, itemObject, GetVelocity(itemObject));
		}
	}

	#endregion

	#region[物品注册与遗忘]

	/// <summary>
	/// 注册/刷新主机场景物品: 获取或创建 NetworkedItem 组件, 赋予稳定场景 ID 或新分配 ID, 写入追踪字典.
	/// <para>
	/// 场景物品的 OwnerId 归属主机, 用于快照发送和后续拾取申请时的权威判断.
	/// "scene:{HostSteamId}:item:{计数器}" 格式区分于玩家丢弃产生的 "p2p:{SteamId}:p2p:{计数器}".
	/// </para>
	/// </summary>
	private static NetworkedItem RegisterHostItem(Item_Object itemObject, string prefabKey, bool preferStableSceneIdentity = false) {
		var identity = GetOrCreateIdentity(itemObject.gameObject);

		if (preferStableSceneIdentity) TryAssignStableSceneIdentity(itemObject, identity);

		if (string.IsNullOrEmpty(identity.NetworkId))
			identity.NetworkId = $"scene:{MPSteamworks.Instance.HostSteamId}:item:{_nextLocalItemId++}";

		identity.PrefabKey = prefabKey;
		identity.OwnerId = MPSteamworks.Instance.HostSteamId; // 场景物品归主机所有
		identity.IsRemote = false;
		identity.WasInstantiatedBySync = false;

		_items[identity.NetworkId] = identity;
		return identity;
	}

	/// <summary>
	/// 检查场景物品是否为新出现且需要注册的 (可同步, 有 prefabKey, 无有效 NetworkId 或未追踪).
	/// </summary>
	private static bool TryPrepareNewHostWorldItem(Item_Object itemObject, out string prefabKey) {
		prefabKey = string.Empty;
		if (!IsSyncableWorldItem(itemObject)) return false;
		prefabKey = GetPrefabKey(itemObject);
		if (string.IsNullOrEmpty(prefabKey)) return false;
		var identity = itemObject.GetComponent<NetworkedItem>();
		if (identity == null || string.IsNullOrEmpty(identity.NetworkId)) return true;
		return !_items.ContainsKey(identity.NetworkId);
	}

	/// <summary>枚举所有已追踪且仍可同步的物品 (用于快照枚举).</summary>
	private static IEnumerable<NetworkedItem> EnumerateKnownHostWorldItems() {
		foreach (var identity in _items.Values) {
			if (identity == null || identity.gameObject == null) continue;
			var itemObject = identity.GetComponent<Item_Object>();
			if (!IsSyncableWorldItem(itemObject)) continue;
			yield return identity;
		}
	}

	/// <summary>
	/// 将网络接收到的初始状态应用到物品对象.
	/// <para>
	/// 执行顺序:
	///   1. 写入 Transform 位置/旋转
	///   2. 通过反射设置 Item.dropObject = itemObject (确保游戏本体 OnPickup 能找到并隐藏该物体)
	///   3. 可选调用 OnDrop: 触发掉落冷却 (dropWait) 使物品进入"刚掉落"的物理状态
	///   4. 设置 Rigidbody 速度, 激活 GameObject
	/// <br/>
	/// try-finally 确保 ApplyingRemoteState 无论如何都能复位.
	/// </para>
	/// </summary>
	private static void ApplyCreate(
		NetworkedItem identity, Vector3 position, Quaternion rotation, Vector3 velocity) {
		if (identity == null || identity.gameObject == null) return;

		ApplyingRemoteState = true;
		try {
			identity.transform.SetPositionAndRotation(position, rotation);

			var rb = GetRigidbody(identity.gameObject);
			if (rb != null) { rb.isKinematic = false; rb.velocity = velocity; }

			identity.gameObject.SetActive(true);
		} finally {
			ApplyingRemoteState = false;
		}
	}

	/// <summary>
	/// 从追踪中遗忘物品: 移除候选记录, 隐藏/销毁 GameObject, 从 _items 移除.
	/// <para>
	/// WasInstantiatedBySync=true: Destroy (同步创建的临时物体)
	/// WasInstantiatedBySync=false: 仅 SetActive(false) (场景原有/玩家本地丢弃产生的物体)
	/// <br/>
	/// 若 _items 中找不到 networkId, 尝试通过稳定场景 ID 降级处理 (TryForgetUnknownSceneItem).
	/// </para>
	/// </summary>
	private static void Forget(string networkId) {
		if (!_items.TryGetValue(networkId, out var identity) || identity == null) {
			TryForgetUnknownSceneItem(networkId);
			return;
		}

		var itemObject = identity.GetComponent<Item_Object>();
		if (itemObject != null) RemoveCandidate(itemObject);

		if (identity.gameObject != null) {
			// 判断物品是否已经合法进入了本地玩家的背包或手中
			bool inInventory = itemObject != null && itemObject.itemData != null &&
							   (itemObject.itemData.inBag || _inHand(itemObject.itemData));

			if (inInventory) {
				// 如果在包里: 绝对不能摧毁物理实体和 SetActive(false)
				// 我们只剥夺它的网络身份, 让它彻底变回单机物品
				identity.NetworkId = string.Empty;
				identity.StableSceneId = string.Empty;
				identity.WasInstantiatedBySync = false;
			} else {
				// 正常的场景遗忘清理
				identity.gameObject.SetActive(false);
				if (identity.WasInstantiatedBySync)
					Object.Destroy(identity.gameObject);
				else {
					// 清除旧的 NetworkId防止带旧 ID 错乱
					identity.NetworkId = string.Empty;
					identity.StableSceneId = string.Empty;
				}
			}
		}
		_items.Remove(networkId);
	}

	/// <summary>
	/// 回退: 通过稳定场景 ID 处理未追踪的物品 (Forget 在 _items 中找不到记录时的降级路径).
	/// </summary>
	private static bool TryForgetUnknownSceneItem(string networkId) {
		if (string.IsNullOrEmpty(networkId) || !networkId.StartsWith("sceneitem:", StringComparison.Ordinal)) return false;

		var itemObject = FindSceneItemByStableId(networkId);
		if (itemObject == null || itemObject.gameObject == null) return false;

		var identity = itemObject.GetComponent<NetworkedItem>();
		if (identity != null && string.IsNullOrEmpty(identity.NetworkId)) {
			identity.NetworkId = string.Empty;
			identity.StableSceneId = string.Empty;
		}
		RemoveCandidate(itemObject);
		itemObject.gameObject.SetActive(false);
		Object.Destroy(itemObject.gameObject); 
		_items.Remove(networkId);
		return true;
	}

	/// <summary>
	/// 实例化世界物品并返回其 Item_Object 组件.
	/// <para>
	/// ApplyingRemoteState=true: 阻止 Item_Object.Start() 触发的游戏回调进入同步链路.
	/// 手动调用 InitializeItemData: 立即建立 Item.dropObject 引用, 不等待 Start().
	/// levelRoot 父级: 与游戏本体 Item.Drop() 保持一致.
	/// </para>
	/// </summary>
	private static Item_Object InstantiateWorldItem(string prefabKey, Vector3 position, Quaternion rotation) {
		if (!MPUtil.TryGetItemPrefab(prefabKey, out Item_Object prefab)) return null;

		ApplyingRemoteState = true;
		try {
			var itemComponent = Object.Instantiate(prefab, position, rotation);
			var levelRoot = WorldLoader.GetCurrentLevelParentRoot();
			if (levelRoot != null) itemComponent.transform.SetParent(levelRoot);

			if (itemComponent?.itemData != null)
				itemComponent.itemData.InitializeItemData(itemComponent);

			return itemComponent;
		} finally {
			ApplyingRemoteState = false;
		}
	}

	#endregion

	#region[候选物品管理]

	/// <summary>
	/// 将场景中所有可同步物品捕获为候选.
	/// snapshotCandidate=true: 同时加入 _snapshotCandidates (供 SnapshotFinalize 清理判断).
	/// </summary>
	private static void CaptureSceneCandidates(bool snapshotCandidate) {
		foreach (var itemObject in EnumerateSceneItems())
			RememberCandidate(itemObject, snapshotCandidate);
	}

	/// <summary>枚举场景中所有可同步且未在黑名单的物品.</summary>
	private static IEnumerable<Item_Object> EnumerateSceneItems() {
		var levelRoot = WorldLoader.instance?.transform;
		if (levelRoot == null) yield break;

		// 仅在当前关卡根节点下进行子树扫描，开销极小
		var items = levelRoot.GetComponentsInChildren<Item_Object>(includeInactive: false);

		foreach (var itemObject in items)
			if (IsSyncableWorldItem(itemObject) && !IsBlacklisted(itemObject.gameObject))
				yield return itemObject;
	}

	/// <summary>
	/// 记住候选物品: 优先尝试分配稳定场景 ID 直接写入 _items,
	/// 若无法分配则加入 _clientCandidates (模糊匹配备用).
	/// </summary>
	private static void RememberCandidate(Item_Object itemObject, bool snapshotCandidate) {
		if (!IsSyncableWorldItem(itemObject)) return;
		// 优先尝试分配稳定场景 ID, 若成功则直接写入 _items, 否则加入候选列表
		var identity = GetOrCreateIdentity(itemObject.gameObject);
		// NetworkId 第一次初始化 写入 _items, 否则加入候选列表
		if (snapshotCandidate && TryAssignStableSceneIdentity(itemObject, identity)) {
			identity.PrefabKey = GetPrefabKey(itemObject);
			_items[identity.NetworkId] = identity;
			MPMain.LogTest("RememberCandidate NetworkId 第一次初始化");
			return;
		}
		// NetworkId 已初始化 写入 _items
		if (!string.IsNullOrEmpty(identity.NetworkId)) {
			_items[identity.NetworkId] = identity;
			MPMain.LogTest("RememberCandidate NetworkId 已初始化");
			return;
		}

		MPMain.LogTest("RememberCandidate 添加到候选物品列表");
		if (!_clientCandidates.Contains(itemObject)) _clientCandidates.Add(itemObject);
		if (snapshotCandidate && !_snapshotCandidates.Contains(itemObject)) _snapshotCandidates.Add(itemObject);
	}

	/// <summary>从所有候选列表移除物品 (匹配成功或失效时调用).</summary>
	private static void RemoveCandidate(Item_Object itemObject) {
		_clientCandidates.Remove(itemObject);
		_snapshotCandidates.Remove(itemObject);
	}

	/// <summary>按 prefabKey + 距离在 _clientCandidates 中查找最近匹配物品.</summary>
	private static Item_Object FindClientCandidate(string prefabKey, Vector3 position, out bool wasSnapshotCandidate) {
		RemoveDestroyedCandidates();
		Item_Object best = null;
		float bestDistance = float.MaxValue;
		wasSnapshotCandidate = false;

		foreach (var candidate in _clientCandidates) {
			if (!IsSyncableWorldItem(candidate)) continue;
			if (!PrefabKeysMatch(prefabKey, GetPrefabKey(candidate))) continue;

			float distance = (candidate.transform.position - position).sqrMagnitude;
			if (distance > CandidateMatchDistanceSqr || distance >= bestDistance) continue;

			best = candidate;
			bestDistance = distance;
		}
		if (best != null) {
			wasSnapshotCandidate = _snapshotCandidates.Contains(best);
			RemoveCandidate(best);
		}
		return best;
	}

	/// <summary>清理候选列表中已失效 (销毁/进背包) 的物品.</summary>
	private static void RemoveDestroyedCandidates() {
		for (int i = _clientCandidates.Count - 1; i >= 0; i--)
			if (!IsSyncableWorldItem(_clientCandidates[i])) _clientCandidates.RemoveAt(i);
		for (int i = _snapshotCandidates.Count - 1; i >= 0; i--)
			if (!IsSyncableWorldItem(_snapshotCandidates[i])) _snapshotCandidates.RemoveAt(i);
	}

	#endregion

	#region[稳定场景 ID]

	/// <summary>
	/// 尝试为场景物品分配稳定场景 ID.
	/// 仅当 identity.NetworkId 为空时执行, 避免覆盖已有 ID.
	/// </summary>
	private static bool TryAssignStableSceneIdentity(Item_Object itemObject, NetworkedItem identity) {
		if (itemObject == null || identity == null) return false;
		if (!string.IsNullOrEmpty(identity.NetworkId)) return false; // 已有 ID, 不覆盖

		string stableId = GetStableSceneItemId(itemObject);
		if (string.IsNullOrEmpty(stableId)) return false;

		identity.StableSceneId = stableId;
		identity.NetworkId = stableId;
		return true;
	}

	/// <summary>
	/// 生成场景物品的稳定唯一 ID.
	/// 格式: <c>sceneitem:{场景名}:{层级路径}|{变换锚点}</c>
	/// <para>
	/// 主客双方加载同一关卡后对同一 Item_Object 生成相同 ID, 实现精确匹配无需模糊搜索.
	/// 层级路径包含 sibling index, 变换锚点对路径做双重校验, 防止场景改动导致误匹配.
	/// 若已有 StableSceneId 则直接返回, 避免重复计算.
	/// </para>
	/// </summary>
	private static string GetStableSceneItemId(Item_Object itemObject) {
		if (itemObject == null || itemObject.gameObject == null) return string.Empty; 
		if (!itemObject.gameObject.scene.IsValid() || string.IsNullOrEmpty(itemObject.gameObject.scene.name)) 
			return string.Empty;

		var identity = itemObject.GetComponent<NetworkedItem>();
		if (identity != null && !string.IsNullOrEmpty(identity.StableSceneId)) {
			MPMain.LogTest("GetStableSceneItemId NetworkedItem.StableSceneId != null");
			return identity.StableSceneId;
		}

		string path = MPUtil.BuildTransformPath(itemObject.transform);
		if (string.IsNullOrEmpty(path)) return string.Empty;
		
		string anchor = BuildStableTransformAnchor(itemObject.transform);
		MPMain.LogTest("GetStableSceneItemId " + path + anchor);
		return $"sceneitem:{itemObject.gameObject.scene.name}:{path}|{anchor}";
	}

	/// <summary>通过稳定场景 ID 在所有场景物品中精确查找对应实例.</summary>
	private static Item_Object FindSceneItemByStableId(string networkId) {
		if (string.IsNullOrEmpty(networkId) || !networkId.StartsWith("sceneitem:", StringComparison.Ordinal)) return null;
		foreach (var itemObject in EnumerateSceneItems())
			if (string.Equals(GetStableSceneItemId(itemObject), networkId, StringComparison.Ordinal)) return itemObject;
		return null;
	}

	#endregion

	#region[可同步性判定]

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
		if (string.IsNullOrEmpty(itemObject.gameObject.scene.name)) return false;
		if (itemObject.itemData == null) return false;
		if (itemObject.itemData.inBag) return false;
		return true;
	}

	#endregion

	#region[工具函数]
	#region[组件工具]

	/// <summary>获取 GameObject 或其子物体上的 Rigidbody 组件.</summary>
	private static Rigidbody GetRigidbody(GameObject gameObject) {
		if (gameObject == null) return null;
		return gameObject.GetComponent<Rigidbody>() ?? gameObject.GetComponentInChildren<Rigidbody>();
	}

	/// <summary>获取或添加 NetworkedItem 组件.</summary>
	private static NetworkedItem GetOrCreateIdentity(GameObject gameObject) {
		return gameObject.GetComponent<NetworkedItem>() ?? gameObject.AddComponent<NetworkedItem>();
	}

	#endregion
	#region[物理工具]

	/// <summary>获取物品的 Rigidbody 速度, 静止 (低于 VelocityEpsilonSqr) 时返回零向量.</summary>
	private static Vector3 GetVelocity(Item_Object itemObject) {
		var rb = GetRigidbody(itemObject.gameObject);
		if (rb == null) return Vector3.zero;
		return rb.velocity.sqrMagnitude > VelocityEpsilonSqr ? rb.velocity : Vector3.zero;
	}

	#endregion
	#region[坐标工具]

	/// <summary>浮点数量化为整数 (乘以精度后四舍五入), 用于稳定 ID 生成.</summary>
	private static int Quantize(float value, float precision) => Mathf.RoundToInt(value * precision);

	/// <summary>将角度归一化到 [0, 360), 用于稳定 ID 生成.</summary>
	private static float NormalizeAngle(float value) {
		value %= 360f;
		if (value < 0f) value += 360f;
		return value;
	}

	/// <summary>构建 Transform 的稳定锚点字符串 (量化后的本地位置和旋转).</summary>
	private static string BuildStableTransformAnchor(Transform transform) {
		if (transform == null) return string.Empty;
		Vector3 p = transform.localPosition;
		Vector3 r = transform.localEulerAngles;
		return $"lp:{Quantize(p.x, StableIdPositionPrecision)},{Quantize(p.y, StableIdPositionPrecision)},{Quantize(p.z, StableIdPositionPrecision)}" +
			   $"|lr:{Quantize(NormalizeAngle(r.x), StableIdRotationPrecision)},{Quantize(NormalizeAngle(r.y), StableIdRotationPrecision)},{Quantize(NormalizeAngle(r.z), StableIdRotationPrecision)}";
	}

	#endregion
	#region[字符串工具]

	/// <summary>获取物品的预制体键: 优先 itemData.prefabName, 否则取去 Clone 后缀的 GameObject 名.</summary>
	private static string GetPrefabKey(Item_Object itemObject) {
		if (itemObject == null) return string.Empty;
		if (itemObject.itemData != null && !string.IsNullOrEmpty(itemObject.itemData.prefabName))
			return itemObject.itemData.prefabName;
		return MPUtil.CleanCloneName(itemObject.gameObject.name);
	}

	/// <summary>比较两个预制体键是否匹配 (忽略大小写, 忽略 Clone 后缀).</summary>
	private static bool PrefabKeysMatch(string a, string b) {
		if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
		return string.Equals(MPUtil.CleanCloneName(a), MPUtil.CleanCloneName(b), StringComparison.OrdinalIgnoreCase);
	}

	#endregion

	#region[安全判断工具]

	/// <summary>
	/// 检查物品是否已经在本地玩家的背包或手中
	/// </summary>
	private static bool IsItemInInventory(NetworkedItem identity) {
		if (identity == null || identity.gameObject == null) return false;

		var itemObject = identity.GetComponent<Item_Object>();
		if (itemObject == null || itemObject.itemData == null) return false;

		// 检查 itemData 是否标记为在包内, 或通过委托检查是否在手中
		bool inBag = itemObject.itemData.inBag;
		bool inHand = _inHand(itemObject.itemData);

		return inBag || inHand;
	}

	#endregion

	#endregion

	#region[黑名单判定]

	private static readonly HashSet<string> _blacklistedPrefabNames = new(StringComparer.OrdinalIgnoreCase) {
		"Item_Flashlight",		// 手电筒: 不同步世界生成
		"Item_Flaregun",		// 信号枪: 不同步世界生成
		"Item_Cryogun",			// 冷冻枪: 不同步世界生成
		"Item_Handgun",			// 手枪: 不同步世界生成
		"Item_Handgun_Debug",	// 手枪: 不同步世界生成
		"Item_10mm_Ammo",		// 手枪子弹: 不同步世界生成
	};

	private static readonly HashSet<string> _blacklistedItemTags = new(StringComparer.OrdinalIgnoreCase) {
		"artifact", // 神器: 关卡特殊物品, 不同步世界生成, 但丢弃/拾取同步
		"disk",     // 磁盘: 关卡特殊物品, 不同步世界生成, 但丢弃/拾取同步
		"trinket",	// 饰品: 关卡特殊物品, 不同步世界生成, 但丢弃/拾取同步
		"notsync",	// 不同步: 特殊物品, 不同步世界生成, 但丢弃/拾取同步
	};

	private static readonly HashSet<string> _blacklistedObjectTagger = new(StringComparer.OrdinalIgnoreCase) {
		"ItemLocked"// 锁定物品: 特殊锁定物品, 不同步世界生成/丢弃/拾取
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

	#endregion
}