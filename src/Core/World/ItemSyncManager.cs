using HarmonyLib;
using Steamworks.Data;
using Steamworks.Ugc;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using WKMPMod.Component;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.NetWork;
using WKMPMod.Util;
using WKMPMod.World;
using static WKMPMod.Data.MPWriterPool;
using Object = UnityEngine.Object;

namespace WKMPMod.World;

/// <summary>
/// 多人游戏物品同步操作类型枚举 (网络协议标签)
/// </summary>
public enum ItemSyncAction : byte {
	SnapshotReset = 0,      // 快照重置: 新玩家加入时，清空其本地所有非持久化物品，准备接受房主数据
	SnapshotFinalize = 1,   // 快照完成: 房主发送完所有世界物品数据，通知客户端解除世界加载锁定
	Create = 2,             // 创建物品: 通知其他客户端在指定位置生成一个掉落物
	PickupRequest = 3,      // 拾取请求: 客户端捡起物品时向网络广播 (或向房主申请验证)
	DropRequest = 4,        // 丢弃请求: 客户端从背包扔出物品时网络广播
	Remove = 5,             // 移除物品: 全局销毁指定的掉落物 (如被垃圾桶吞掉或剧情销毁)
}

/// <summary>
/// 管理多人游戏中物品 (Item_Object) 的网络同步.
///
/// ── 架构概述 ──────────────────────────────────────────────────────────────────
///
/// 采用 Host-Authoritative 权威主机模型:
///		主机 (Host) 是唯一的物品状态权威, 负责分配 NetworkId、广播创建和移除.
///		客户端 (Client) 只能"请求"操作 (PickupRequest / DropRequest), 由主机决策后广播.
///
/// ── 物品来源分类 ──────────────────────────────────────────────────────────────
///
///   1. 场景物品 (Scene Item):
///      预置在关卡中的 Item_Object. 主客双方在世界加载后都会在相同位置拥有该物品.
///      通过 StableSceneId (层级路径 + 量化变换) 跨实例匹配, 避免重复实例化.
///
///   2. 主机丢弃 (Host Drop):
///      主机本地玩家将物品丢入世界. 主机立即注册并广播 Create, 客户端实例化或复用候选.
///
///   3. 客户端丢弃 (Client Drop):
///      客户端在本地已经有丢弃物体 (Item.Drop 已执行, Item_Object 已激活).
///      客户端发送 DropRequest → 主机在自己这边实例化并广播 Create → 客户端将
///      收到的 Create 匹配到本地已有的那个物体 (PendingLocalDrop), 避免出现两个重影.
///
/// ── 新玩家加入的快照协议 ──────────────────────────────────────────────────────
///
///   Step 1: 主机发送 SnapshotReset  → 客户端清空状态, 重新捕获场景候选
///   Step 2: 主机逐帧发送所有已知世界物品的 Create 消息 (分帧限速避免卡顿)
///   Step 3: 主机发送 SnapshotFinalize → 客户端隐藏所有未被匹配的场景候选
///           (说明主机认为这些物品已被拾取或不存在, 客户端应与主机对齐)
///
/// ── ApplyingRemoteState 标志 ──────────────────────────────────────────────────
///
///   当本类正在执行远程状态写入 (创建物品、设置位置/速度等) 时, 该标志为 true.
///   Harmony 补丁 (Patch_Item_Object_Pickup_ItemSync 等) 会检查此标志, 以避免
///   "我们在应用远程状态时触发的游戏回调" 再次进入同步逻辑造成循环.
///
/// ── 与游戏本体的接口 ─────────────────────────────────────────────────────────
///
///  Item.dropObject (private):
///		Item 数据类持有对场景中 Item_Object 的引用. 该字段为私有, 必须通过反射读写.
///		Item.InitializeItemData(selfObject) 在 Item_Object.Start() 时自动设置.
///		通过反射的 AssignDropObject 确保同步创建的物品也正确建立这条反向引用.
///
///  Item.inBag:
///		物品在玩家背包中时为 true. IsSyncableWorldItem 以此排除背包物品,
///		只同步处于世界中的掉落物.
///
///  Item_Object.OnDrop():
///		物品进入世界时触发, 会设置短暂丢弃冷却 (dropWait), 防止立即再次拾取.
///		ApplyCreate 在应用远程状态时根据 skipDropCallbacks 决定是否调用,
///		避免本地已触发过的 OnDrop 被重复调用.
/// </summary>
public static class ItemSyncManager {
	#region[常量]
	/// <summary>
	/// 场景候选物品的最大匹配距离平方 (约 0.7m 半径).
	/// 用于 FindClientCandidate: 收到 Host 的 Create 时, 寻找半径内且同 prefab 的本地物品.
	/// 场景物品不会移动, 因此阈值可以较紧, 避免不同物品间误匹配.
	/// </summary>
	private const float CandidateMatchDistanceSqr = 0.5f;

	/// <summary>
	/// 本地丢弃物品的最大匹配距离平方 (约 5m 半径).
	/// 用于 FindPendingLocalDrop: 客户端在本地丢弃物品后, 等待 Host 广播 Create 再匹配回来.
	/// 因为存在网络往返延迟 (RTT 期间物品可能因重力/初速度移动较远), 阈值须比场景候选宽松.
	/// </summary>
	private const float LocalDropMatchDistanceSqr = 25f;

	/// <summary>
	/// PendingLocalDrop 记录的最大存活时间 (秒).
	/// 若 Host 在 3 秒内未广播对应的 Create (例如网络丢包), 客户端侧自动丢弃该记录,
	/// 防止陈旧记录污染后续丢弃动作的匹配.
	/// </summary>
	private const float LocalDropMaxAge = 3f;

	/// <summary>
	/// 丢弃后拾取抑制窗口 (秒).
	/// 物品进入世界的瞬间, Harmony 的 Pickup 补丁也可能被触发 (例如物品正好落在玩家脚下).
	/// 在此窗口内抑制本地拾取通知, 等待 Host/Client 的丢弃-创建流程完成后再恢复.
	/// </summary>
	private const float LocalDropPickupSuppressWindow = 0.2f;

	/// <summary>
	/// 速度零阈值平方 (约 0.05 m/s).
	/// 低于此速度视为静止, GetVelocity 返回 Vector3.zero,
	/// 避免向网络发送微小无意义的速度值.
	/// </summary>
	private const float VelocityEpsilonSqr = 0.0025f;

	/// <summary>
	/// 等待世界加载完成的超时时间 (秒).
	/// WaitForWorldReady 协程在此时间内若世界仍未就绪则直接放行, 防止协程永久挂起.
	/// </summary>
	private const float WorldReadyTimeout = 12f;

	/// <summary>
	/// 稳定场景 ID 的位置量化精度.
	/// 本地位置乘以此值后取整, 用于生成 StableSceneId 中的位置锚点.
	/// 精度 20 对应约 0.05m 分辨率, 足以区分相邻物品但对浮点漂移不敏感.
	/// </summary>
	private const float StableIdPositionPrecision = 20f;

	/// <summary>
	/// 稳定场景 ID 的旋转量化精度.
	/// 欧拉角乘以此值后取整, 精度 5 对应约 0.2° 分辨率.
	/// </summary>
	private const float StableIdRotationPrecision = 5f;

	/// <summary>
	/// 每帧快照发送/注册的物品数量上限.
	/// 场景物品数量可能很大 (数十至数百), 若在一帧内全量处理会导致卡顿.
	/// 此值将工作分散到多帧完成.
	/// </summary>
	private const int SnapshotItemsPerFrame = 10;

	#endregion
	#region[静态字段/反射]

	private static readonly AccessTools.FieldRef<Item, Item_Object> _dropObjectField =
		AccessTools.FieldRefAccess<Item, Item_Object>("dropObject");

	#endregion
	#region[静态字段/状态]
	/// <summary>
	/// 所有已追踪的网络物品注册表. Key = NetworkId, Value = NetworkedItem 组件.
	/// <para>
	/// 主机: 包含场景内所有已注册的世界物品 (场景物品 + 运行时丢弃物).
	/// 客户端: 包含已通过快照或 Create 消息确认存在的物品, 以及通过 StableSceneId 自行匹配的场景物品.
	/// 物品被拾取 (Forget) 后从此字典移除.
	/// </para>
	/// </summary>
	private static readonly Dictionary<string, NetworkedItem> _items = new();

	/// <summary>
	/// 客户端的候选物品列表: 本地已存在、尚未与任何 Create 消息匹配的 Item_Object.
	/// <para>
	/// 场景加载后, 客户端本地已有所有场景物品实例. 当主机发来 Create 消息时,
	/// 先在此列表中按 prefabKey + 距离查找匹配对象, 复用而非重新实例化, 避免重影.
	/// 成功匹配后从列表移除. 分为两类:
	///   - _clientCandidates: 所有候选 (包含场景物品和本地丢弃物)
	///   - _snapshotCandidates: 仅包含场景候选, 用于快照完成后的清理判断
	/// </para>
	/// </summary>
	private static readonly List<Item_Object> _clientCandidates = new();

	/// <summary>
	/// 待处理的本地丢弃记录.
	/// <para>
	/// 客户端玩家丢弃物品时: 游戏本体已在本地激活 Item_Object (Item.Drop 已执行),
	/// 同时客户端发送 DropRequest 给主机. 主机收到后在自己这边实例化物品并广播 Create.
	/// 客户端收到该 Create 时需要将其匹配到"本地已有的那个物体", 而不是再实例化一个新的.
	/// 此列表暂存等待匹配的本地丢弃物, 并在 LocalDropMaxAge 后自动过期清理.
	/// </para>
	/// </summary>
	private static readonly List<PendingLocalDrop> _pendingLocalDrops = new();


	/// <summary>
	/// 快照候选物品: 仅在快照协议期间使用的场景物品候选子集.
	/// <para>
	/// SnapshotReset 后重新捕获, 在快照期间随 Create 匹配而减少.
	/// SnapshotFinalize 时, 此列表中仍剩余的物品意味着主机不知道它们的存在
	/// (很可能已被其他玩家拾取), 客户端应将其隐藏以对齐主机状态.
	/// </para>
	/// </summary>
	private static readonly List<Item_Object> _snapshotCandidates = new();

	/// <summary>
	/// 每个客户端对应的快照发送协程. Key = clientId.
	/// <para>
	/// 同一客户端在快照协程运行期间若再次连接, 先停止旧协程再启动新的,
	/// 确保客户端始终收到最新完整快照.
	/// </para>
	/// </summary>
	private static readonly Dictionary<ulong, Coroutine> _snapshotRoutines = new();

	/// <summary>
	/// 拾取抑制记录. Key = Item_Object 的 InstanceID, Value = 抑制截止时间 (Time.time).
	/// <para>
	/// 物品刚被丢入世界时, Harmony 的 Pickup 补丁可能被立即触发 (例如物品落在玩家脚边).
	/// 在短暂窗口 (LocalDropPickupSuppressWindow) 内, ShouldSuppressLocalPickup 返回 true,
	/// 阻止此类误触发进入同步流程. 窗口到期后自动解除.
	/// </para>
	/// </summary>
	private static readonly Dictionary<int, float> _suppressedPickupObjects = new();

	#endregion
	#region[静态字段/控制]
	/// <summary>
	/// 主机下一个要分配的物品 ID 序号 (单调递增).
	/// NetworkId 格式: "{HostSteamId}:item:{_nextHostItemId}"
	/// 场景物品优先使用 StableSceneId, 此计数器仅用于运行时丢弃物.
	/// </summary>
	private static ulong _nextHostItemId = 1;

	/// <summary>
	/// PrepareWorldRoutine 协程引用, 用于 ResetState 时停止.
	/// </summary>
	private static Coroutine _prepareRoutine;

	/// <summary>
	/// HostDiscoveryRoutine 协程引用.
	/// 该协程每 0.5 秒扫描一次场景, 发现并注册新出现的未追踪物品 (例如游戏脚本动态生成的).
	/// </summary>
	private static Coroutine _hostDiscoveryRoutine;

	/// <summary>
	/// 主机是否已完成对所有场景物品的初次注册.
	/// 在 RegisterHostSceneItemsRoutine 完成后置 true.
	/// 用于 SendSnapshotToClientRoutine 判断是否需要先等待注册完成再发送快照.
	/// </summary>
	private static bool _hostSceneItemsRegistered;

	#endregion
	#region[静态属性]

	/// <summary>
	/// 是否正在应用远程状态 (防止同步递归).
	/// <para>
	/// 当此类执行以下操作时为 true:
	///   InstantiateWorldItem: 实例化远程物品
	///   ApplyCreate: 设置物品位置/速度/激活状态
	/// <br/>
	/// Harmony 补丁 (NotifyLocalPickup / NotifyLocalDrop) 在 ApplyingRemoteState=true 时
	/// 直接返回, 防止我们自己触发的游戏回调再次进入同步逻辑 (无限递归).
	/// </para>
	/// </summary>
	public static bool ApplyingRemoteState { get; private set; }

	#endregion
	#region[公共接口]

	/// <summary>
	/// 世界初始化完成通知. 重置状态并启动准备协程.
	/// </summary>
	public static void NotifyWorldInitialized() {
		ResetState();

		if (MPCore.Instance == null) {
			MPMain.LogWarning("[MP ItemSync] Prepare skipped: MPCore.Instance is null.");
			return;
		}

		_prepareRoutine = MPCore.Instance.StartCoroutine(PrepareWorldRoutine());
	}

	/// <summary>
	/// 完全重置物品同步状态, 清理所有追踪数据和协程.
	/// </summary>
	public static void ResetState() {
		// 停止准备协程
		if (_prepareRoutine != null && MPCore.Instance != null) {
			MPCore.Instance.StopCoroutine(_prepareRoutine);
			_prepareRoutine = null;
		}

		// 停止主机发现协程
		if (_hostDiscoveryRoutine != null && MPCore.Instance != null) {
			MPCore.Instance.StopCoroutine(_hostDiscoveryRoutine);
			_hostDiscoveryRoutine = null;
		}

		// 停止所有快照协程
		if (MPCore.Instance != null) {
			foreach (var routine in _snapshotRoutines.Values) {
				if (routine != null) {
					MPCore.Instance.StopCoroutine(routine);
				}
			}
		}

		_snapshotRoutines.Clear();

		// 销毁由同步创建的物品
		foreach (var identity in _items.Values) {
			if (identity == null || identity.gameObject == null) continue;
			if (!identity.WasInstantiatedBySync) continue;

			Object.Destroy(identity.gameObject);
		}

		_items.Clear();
		_clientCandidates.Clear();
		_pendingLocalDrops.Clear();
		_snapshotCandidates.Clear();
		_suppressedPickupObjects.Clear();

		_hostSceneItemsRegistered = false;
		ApplyingRemoteState = false;

		MPMain.LogInfo("[MP ItemSync] ResetState completed.");
	}

	/// <summary>
	/// 向指定客户端发送完整物品快照.
	/// </summary>
	public static void SendSnapshotToClient(ulong clientId) {
		if (!MPCore.CanSync) return;
		if (!MPSteamworks.Instance.IsHost) return;
		if (MPCore.Instance == null) return;
		if (clientId == 0 || clientId == MPSteamworks.UserSteamId) return;

		// 替换已有的快照协程
		if (_snapshotRoutines.TryGetValue(clientId, out var existingRoutine) && existingRoutine != null) {
			MPCore.Instance.StopCoroutine(existingRoutine);
		}

		_snapshotRoutines[clientId] = MPCore.Instance.StartCoroutine(SendSnapshotToClientRoutine(clientId));
	}

	/// <summary>
	/// 本地拾取物品通知 (由 Harmony 补丁 Patch_Item_Object_Pickup_ItemSync 调用).
	/// <para>
	/// 触发时机: Item_Object.Pickup() 被调用后 (Postfix), 即玩家成功将物品拾入手中.
	/// 注意: Item.OnPickup() 会隐藏 dropObject (SetActive false), 但 NetworkedItem 仍存在.
	/// <br/>
	/// 主机: 直接广播 Remove 并从追踪中遗忘 (无需等待确认, 主机即是权威).
	/// 客户端: 发送 PickupRequest 给主机, 由主机决策后广播 Remove 给所有人.
	/// <br/>
	/// 跳过条件:
	///   ApplyingRemoteState=true: 我们自己应用远程状态时触发的回调, 忽略
	///   IsPendingLocalDrop: 物品刚被本地丢弃还未经过同步确认, 忽略
	///   ShouldSuppressLocalPickup: 丢弃后短暂抑制窗口内, 忽略
	///   identity 或 NetworkId 为空: 物品尚未注册到同步系统, 忽略
	/// </para>
	/// </summary>
	public static void NotifyLocalPickup(Item_Object itemObject) {
		if (ApplyingRemoteState || itemObject == null || !MPCore.CanSync) return;

		// 忽略待处理的本地丢弃
		if (IsPendingLocalDrop(itemObject)) {
			MPMain.LogInfo($"[MP ItemSync] Suppressed pickup for pending local drop. Item={itemObject.name}");
			return;
		}

		// 忽略刚丢弃的抑制拾取
		if (ShouldSuppressLocalPickup(itemObject)) return;

		var identity = itemObject.GetComponent<NetworkedItem>();

		if (identity == null || string.IsNullOrEmpty(identity.NetworkId)) {
			MPMain.LogWarning(
				$"[MP ItemSync] Pickup ignored: missing NetworkedItem or NetworkId. " +
				$"Item={itemObject.name}, PrefabKey={GetPrefabKey(itemObject)}, " +
				$"HasIdentity={identity != null}"
			);
			return;
		}

		MPMain.LogInfo(
			$"[MP ItemSync] Local pickup. " +
			$"Host={MPSteamworks.Instance.IsHost}, " +
			$"Item={itemObject.name}, " +
			$"NetworkId={identity.NetworkId}, " +
			$"PrefabKey={identity.PrefabKey}"
		);

		// 主机直接广播移除并遗忘
		if (MPSteamworks.Instance.IsHost) {
			BroadcastRemove(identity.NetworkId);
			Forget(identity.NetworkId);
			return;
		}

		// 客户端发送拾取请求
		SendPickupRequest(identity.NetworkId);
	}

	/// <summary>
	/// 本地丢弃物品通知 (由 Harmony 补丁 Patch_Inventory_DropItemIntoWorld_ItemSync 调用).
	/// <para>
	/// 触发时机: Inventory.DropItemIntoWorld() 执行后 (Postfix).
	/// 此时游戏本体已完成 Item.Drop() 调用: Item_Object 已在场景中激活并定位.
	/// <br/>
	/// 主机流程:
	///   1. RegisterHostItem: 为该 Item_Object 分配 NetworkId 并写入 _items
	///   2. RememberSuppressedPickup: 防止此帧 Pickup 补丁误触发
	///   3. BroadcastCreate: 告知所有客户端该物品已存在
	/// <br/>
	/// 客户端流程:
	///   1. RememberCandidate: 加入候选列表, 以便后续与 Host 的 Create 消息匹配
	///   2. RememberPendingLocalDrop: 记录为待匹配丢弃
	///   3. RememberSuppressedPickup: 防止立即误触发
	///   4. SendDropRequest: 向主机请求权威创建
	/// <br/>
	/// ResolveDropObject 通过反射从 Item 中取出 dropObject (Item_Object 引用), 因为
	/// Inventory.DropItemIntoWorld 的参数只有 Item 数据类, 没有直接暴露 Item_Object.
	/// </para>
	/// </summary>
	public static void NotifyLocalDrop(Item item) {
		if (ApplyingRemoteState || item == null || !MPCore.CanSync) return;

		var itemObject = _dropObjectField(item);
		if (!IsSyncableDropItem(itemObject)) return;

		var prefabKey = GetPrefabKey(itemObject);
		if (string.IsNullOrEmpty(prefabKey)) return;

		if (MPSteamworks.Instance.IsHost) {
			var identity = RegisterHostItem(itemObject, prefabKey);
			var velocity = GetVelocity(itemObject);
			RememberSuppressedPickup(itemObject);

			MPMain.LogInfo(
				$"[MP ItemSync] Host local drop. " +
				$"Item={itemObject.name}, NetworkId={identity.NetworkId}, PrefabKey={prefabKey}"
			);

			BroadcastCreate(identity, itemObject, velocity, isDropSpawn: true);
			return;
		}

		// 客户端记录候选和待处理丢弃
		RememberCandidate(itemObject, snapshotCandidate: false);
		RememberPendingLocalDrop(itemObject, prefabKey);
		RememberSuppressedPickup(itemObject);

		MPMain.LogInfo(
			$"[MP ItemSync] Client local drop request. " +
			$"Item={itemObject.name}, PrefabKey={prefabKey}"
		);

		SendDropRequest(prefabKey, itemObject.transform.position, itemObject.transform.rotation, GetVelocity(itemObject));
	}

	/// <summary>
	/// 生成同步的世界丢弃物品. 主机直接生成, 客户端发送请求.
	/// </summary>
	public static void SpawnSyncedWorldDrop(string prefabKey, Vector3 position, Quaternion rotation, Vector3 velocity) {
		if (ApplyingRemoteState || !MPCore.CanSync) return;
		if (string.IsNullOrWhiteSpace(prefabKey)) return;

		if (!MPSteamworks.Instance.IsHost) {
			SendDropRequest(prefabKey, position, rotation, velocity);
			return;
		}

		var itemObject = InstantiateWorldItem(prefabKey, position, rotation);
		if (itemObject == null) {
			MPMain.LogWarning($"[MP ItemSync] Could not instantiate synced drop '{prefabKey}'.");
			return;
		}

		var identity = RegisterHostItem(itemObject, prefabKey);
		RememberSuppressedPickup(itemObject);
		ApplyCreate(identity, position, rotation, velocity, isDropSpawn: true, skipDropCallbacks: false);
		BroadcastCreate(identity, itemObject, velocity, isDropSpawn: true);
	}

	/// <summary>
	/// 处理接收到的物品同步消息.
	/// </summary>
	public static void HandleItemState(IDType senderId, DataReader reader) {
		var action = (ItemSyncAction)reader.GetByte();

		try {
			switch (action) {
				case ItemSyncAction.SnapshotReset:
					HandleSnapshotReset();
					break;
				case ItemSyncAction.SnapshotFinalize:
					HandleSnapshotFinalize();
					break;
				case ItemSyncAction.Create:
					HandleCreate(senderId, reader);
					break;
				case ItemSyncAction.PickupRequest:
					HandlePickupRequest(reader);
					break;
				case ItemSyncAction.DropRequest:
					HandleDropRequest(senderId, reader);
					break;
				case ItemSyncAction.Remove:
					HandleRemove(reader);
					break;
			}
		} catch (System.Exception e) {
			MPMain.LogError($"[MP ItemSync] Failed to apply {action}: {e.Message}");
		}
	}

	#endregion
	#region[协程]

	/// <summary>
	/// 世界准备协程: 等待世界就绪后执行角色初始化任务.
	/// <para>
	/// 两次 yield return null 的作用: 让 WorldLoader 相关脚本在同一帧的后续 Update 中
	/// 完成它们的初始化逻辑, 再执行我们的注册/捕获, 避免在场景物品完全就绪前访问它们.
	/// <br/>
	/// 主机路径:
	///   RegisterHostSceneItemsRoutine → 分帧注册场景物品
	///   HostDiscoveryRoutine → 启动定时扫描协程
	/// <br/>
	/// 客户端路径:
	///   CaptureSceneCandidates → 记录所有场景物品为候选, 待快照到来时匹配
	/// </para>
	/// </summary>
	private static IEnumerator PrepareWorldRoutine() {
		MPMain.LogInfo(
			$"[MP ItemSync] PrepareWorldRoutine started. " +
			$"IsHost={MPSteamworks.Instance.IsHost}, IsInLobby={MPCore.IsInLobby}, CanSync={MPCore.CanSync}, " +
			$"WorldInitialized={WorldLoader.initialized}, WorldLoaded={WorldLoader.isLoaded}"
		);

		yield return WaitForWorldReady();

		yield return null;
		yield return null;

		if (MPSteamworks.Instance.IsHost) {
			// 主机注册场景物品
			yield return RegisterHostSceneItemsRoutine();
			if (MPCore.Instance != null && _hostDiscoveryRoutine == null) {
				_hostDiscoveryRoutine = MPCore.Instance.StartCoroutine(HostDiscoveryRoutine());
			}
		} else if (MPCore.IsInLobby) {
			// 客户端捕获场景候选
			CaptureSceneCandidates(snapshotCandidate: true);
			MPMain.LogInfo(
				$"[MP ItemSync] Captured client scene candidates. " +
				$"Candidates={_clientCandidates.Count}, SnapshotCandidates={_snapshotCandidates.Count}"
			);
		} else {
			MPMain.LogInfo("[MP ItemSync] Prepare skipped candidate capture because client is not in lobby.");
		}

		_prepareRoutine = null;

		MPMain.LogInfo(
			$"[MP ItemSync] PrepareWorldRoutine finished. " +
			$"Items={_items.Count}, HostSceneItemsRegistered={_hostSceneItemsRegistered}"
		);
	}

	/// <summary>
	/// 向指定客户端发送完整物品快照的协程.
	/// <para>
	/// 协议顺序:
	///   1. SnapshotReset: 通知客户端清空状态并重新捕获场景候选
	///   2. 逐物品发送 Create (每帧限速 SnapshotItemsPerFrame 个, 防止卡顿)
	///   3. SnapshotFinalize: 通知客户端快照发送完毕, 客户端据此隐藏未匹配的场景物品
	/// <br/>
	/// 若客户端快照协程在发送过程中被重新触发, 旧协程先被 StopCoroutine 停止,
	/// 以确保客户端收到的是最新状态而不是半完成的旧快照.
	/// </para>
	/// </summary>
	private static IEnumerator SendSnapshotToClientRoutine(ulong clientId) {
		yield return WaitForWorldReady();

		if (!_hostSceneItemsRegistered) {
			yield return RegisterHostSceneItemsRoutine();
		}

		// 发送快照重置
		SendSnapshotReset(clientId);

		var snapshot = new List<NetworkedItem>(EnumerateKnownHostWorldItems());

		int sentThisFrame = 0;

		// 逐物品发送创建消息
		foreach (var identity in snapshot) {
			if (identity == null || identity.gameObject == null) continue;

			var itemObject = identity.GetComponent<Item_Object>();
			if (!IsSyncableWorldItem(itemObject)) continue;

			SendCreate(clientId, identity, itemObject, GetVelocity(itemObject), isDropSpawn: false);

			sentThisFrame++;
			// 每帧发送数量限制
			if (sentThisFrame >= SnapshotItemsPerFrame) {
				sentThisFrame = 0;
				yield return null;
			}
		}

		// 发送快照完成
		SendSnapshotFinalize(clientId);
		_snapshotRoutines.Remove(clientId);

		MPMain.LogInfo($"[MP ItemSync] Sent item snapshot to {clientId}. Items={snapshot.Count}");
	}

	/// <summary>
	/// 等待世界加载完成, 带有超时保护.
	/// </summary>
	private static IEnumerator WaitForWorldReady() {
		float elapsed = 0f;

		while ((!WorldLoader.initialized || !WorldLoader.isLoaded) && elapsed < WorldReadyTimeout) {
			elapsed += Time.unscaledDeltaTime;
			yield return null;
		}

		MPMain.LogInfo(
			$"[MP ItemSync] World ready wait finished. " +
			$"Elapsed={elapsed:F2}, Initialized={WorldLoader.initialized}, Loaded={WorldLoader.isLoaded}"
		);
	}

	/// <summary>
	/// 主机注册所有场景物品协程 (分帧执行).
	/// </summary>
	private static IEnumerator RegisterHostSceneItemsRoutine() {
		if (!MPSteamworks.Instance.IsHost) yield break;

		int registeredThisFrame = 0;
		int totalRegistered = 0;

		foreach (var itemObject in EnumerateSceneItems()) {
			var prefabKey = GetPrefabKey(itemObject);
			if (string.IsNullOrEmpty(prefabKey)) continue;

			RegisterHostItem(itemObject, prefabKey, preferStableSceneIdentity: true);

			totalRegistered++;
			registeredThisFrame++;

			if (registeredThisFrame >= SnapshotItemsPerFrame) {
				registeredThisFrame = 0;
				yield return null;
			}
		}

		_hostSceneItemsRegistered = true;

		MPMain.LogInfo($"[MP ItemSync] Registered host scene items. Items={totalRegistered}");
	}

	/// <summary>
	/// 主机定期发现新物品协程 (每0.5秒).
	/// </summary>
	private static IEnumerator HostDiscoveryRoutine() {
		var wait = new WaitForSecondsRealtime(0.5f);

		while (MPCore.Instance != null) {
			if (MPCore.CanSync && MPSteamworks.Instance.IsHost && _hostSceneItemsRegistered) {
				DiscoverAndBroadcastNewHostWorldItems();
			}

			yield return wait;
		}

		_hostDiscoveryRoutine = null;
	}

	#endregion
	#region[主机物品管理]

	/// <summary>
	/// 发现并广播场景中新出现的未注册物品.
	/// </summary>
	private static void DiscoverAndBroadcastNewHostWorldItems() {
		foreach (var itemObject in EnumerateSceneItems()) {
			if (!TryPrepareNewHostWorldItem(itemObject, out var prefabKey)) continue;

			var identity = RegisterHostItem(itemObject, prefabKey, preferStableSceneIdentity: true);
			RememberSuppressedPickup(itemObject);

			MPMain.LogInfo(
				$"[MP ItemSync] Registered dynamic host world item. " +
				$"Item={itemObject.name}, NetworkId={identity.NetworkId}, PrefabKey={prefabKey}"
			);

			BroadcastCreate(identity, itemObject, GetVelocity(itemObject), isDropSpawn: false);
		}
	}

	/// <summary>
	/// 检查物品是否为新出现且需要注册的主机物品.
	/// </summary>
	private static bool TryPrepareNewHostWorldItem(Item_Object itemObject, out string prefabKey) {
		prefabKey = string.Empty;
		if (!IsSyncableWorldItem(itemObject)) return false;

		prefabKey = GetPrefabKey(itemObject);
		if (string.IsNullOrEmpty(prefabKey)) return false;

		var identity = itemObject.GetComponent<NetworkedItem>();
		if (identity == null) return true;

		if (string.IsNullOrEmpty(identity.NetworkId)) return true;

		return !_items.ContainsKey(identity.NetworkId);
	}

	/// <summary>
	/// 枚举所有已知的主机世界物品.
	/// </summary>
	private static IEnumerable<NetworkedItem> EnumerateKnownHostWorldItems() {
		foreach (var identity in _items.Values) {
			if (identity == null || identity.gameObject == null) continue;

			var itemObject = identity.GetComponent<Item_Object>();
			if (!IsSyncableWorldItem(itemObject)) continue;

			yield return identity;
		}
	}

	/// <summary>
	/// 将物品注册为主机物品, 分配 NetworkId 并加入追踪.
	/// </summary>
	private static NetworkedItem RegisterHostItem(
		Item_Object itemObject,
		string prefabKey,
		bool preferStableSceneIdentity = false
	) {
		var identity = GetOrCreateIdentity(itemObject.gameObject);

		// 优先分配稳定的场景身份
		if (preferStableSceneIdentity) {
			TryAssignStableSceneIdentity(itemObject, identity);
		}

		// 若无 NetworkId 则生成新的
		if (string.IsNullOrEmpty(identity.NetworkId)) {
			identity.NetworkId = $"{MPSteamworks.UserSteamId}:item:{_nextHostItemId++}";
		}

		identity.PrefabKey = prefabKey;
		identity.OwnerId = MPSteamworks.UserSteamId;
		identity.IsRemote = false;
		identity.WasInstantiatedBySync = false;

		// 检测重复 NetworkId
		if (_items.TryGetValue(identity.NetworkId, out var existing) &&
			existing != null &&
			existing != identity) {
			MPMain.LogWarning(
				$"[MP ItemSync] Duplicate NetworkId detected. " +
				$"NetworkId={identity.NetworkId}, " +
				$"Existing={existing.name}, New={itemObject.name}"
			);
		}

		_items[identity.NetworkId] = identity;
		return identity;
	}

	#endregion
	#region[消息处理]

	/// <summary>
	/// 客户端收到快照重置: 清空状态并重新捕获场景候选.
	/// </summary>
	private static void HandleSnapshotReset() {
		if (MPSteamworks.Instance.IsHost) return;

		MPMain.LogInfo("[MP ItemSync] Received snapshot reset.");

		ResetState();
		CaptureSceneCandidates(snapshotCandidate: true);
	}

	/// <summary>
	/// 客户端收到快照完成消息: 隐藏所有未被匹配到任何 Create 消息的场景候选物品.
	/// <para>
	/// 逻辑: 快照期间, 每收到一个 Create 消息就从 _snapshotCandidates 中移除匹配项.
	/// SnapshotFinalize 时仍留在列表中的物品 = 主机不知道它们存在 = 它们已被拾取.
	/// 客户端将其隐藏以与主机状态对齐, 防止客户端看到主机上已消失的物品.
	/// </para>
	/// </summary>
	private static void HandleSnapshotFinalize() {
		if (MPSteamworks.Instance.IsHost) return;

		int hidden = 0;

		for (int i = _snapshotCandidates.Count - 1; i >= 0; i--) {
			var candidate = _snapshotCandidates[i];
			if (candidate == null || candidate.gameObject == null) continue;

			var identity = candidate.GetComponent<NetworkedItem>();
			// 已注册的跳过
			if (identity != null &&
				!string.IsNullOrEmpty(identity.NetworkId) &&
				_items.ContainsKey(identity.NetworkId)) {
				continue;
			}

			candidate.gameObject.SetActive(false);
			hidden++;
		}

		_snapshotCandidates.Clear();

		MPMain.LogInfo($"[MP ItemSync] Snapshot finalized. Hidden unmatched scene items={hidden}.");
	}


	/// <summary>
	/// 客户端收到创建消息: 按优先级匹配候选或实例化新物品, 并应用初始状态.
	/// <para>
	/// 匹配优先级 (由高到低):
	///   1. isDropSpawn=true 时: 从 _pendingLocalDrops 中查找
	///      (客户端本地已有丢弃物, 无需再实例化, 只需绑定 NetworkId)
	///   2. StableSceneId 精确匹配
	///      (场景物品有确定性 ID, 优先按 ID 直接找到对应实例)
	///   3. _clientCandidates 中按 prefabKey + 距离模糊匹配
	///      (场景物品无 StableSceneId 时的降级策略)
	///   4. 无匹配 → 实例化新物品 (WasInstantiatedBySync=true)
	/// <br/>
	/// skipDropCallbacks 判断逻辑:
	///   如果匹配到的候选不是快照候选 (wasSnapshotCandidate=false), 且候选存在
	///   (说明是本地丢弃物), 且这是一个丢弃产生的物品 (isDropSpawn=true),
	///   则跳过 OnDrop 回调 ── 因为本地丢弃时游戏已经调用过 OnDrop 了,
	///   不能重复调用, 否则会重置 dropWait 并触发重复事件.
	/// </para>
	/// </summary>
	private static void HandleCreate(ulong senderId, DataReader reader) {
		if (MPSteamworks.Instance.IsHost) return;

		var networkId = reader.GetString();
		var prefabKey = reader.GetString();
		var position = reader.GetVector3();
		var rotation = reader.GetQuaternion();
		var velocity = reader.GetVector3();
		var isDropSpawn = reader.GetBool();

		if (string.IsNullOrEmpty(networkId) || string.IsNullOrEmpty(prefabKey)) return;

		// 已存在的物品直接更新状态
		if (_items.TryGetValue(networkId, out var existing) && existing != null) {
			ApplyCreate(existing, position, rotation, velocity, isDropSpawn, skipDropCallbacks: true);
			return;
		}

		// 尝试匹配候选物品: 优先本地丢弃, 其次场景稳定ID, 最后客户端候选
		var candidate = isDropSpawn ? FindPendingLocalDrop(prefabKey, position) : null;
		bool wasSnapshotCandidate = false;

		if (candidate == null) {
			candidate = FindSceneItemByStableId(networkId);
			if (candidate != null) {
				wasSnapshotCandidate = _snapshotCandidates.Contains(candidate);
				RemoveCandidate(candidate);
			}
		}

		if (candidate == null) {
			candidate = FindClientCandidate(prefabKey, position, out wasSnapshotCandidate);
		}

		var itemObject = candidate;
		bool instantiatedBySync = false;

		// 无候选则实例化新物品
		if (itemObject == null) {
			itemObject = InstantiateWorldItem(prefabKey, position, rotation);
			instantiatedBySync = itemObject != null;
		}

		if (itemObject == null) {
			MPMain.LogWarning($"[MP ItemSync] Could not create item '{prefabKey}' for {networkId}.");
			return;
		}

		var identity = GetOrCreateIdentity(itemObject.gameObject);

		// 场景物品保留稳定ID
		if (networkId.StartsWith("sceneitem:", System.StringComparison.Ordinal)) {
			identity.StableSceneId = networkId;
		}

		identity.NetworkId = networkId;
		identity.PrefabKey = prefabKey;
		identity.OwnerId = senderId;
		identity.IsRemote = true;
		identity.WasInstantiatedBySync = instantiatedBySync;

		if (_items.TryGetValue(networkId, out var existingAfterCandidate) &&
			existingAfterCandidate != null &&
			existingAfterCandidate != identity) {
			MPMain.LogWarning(
				$"[MP ItemSync] Duplicate client NetworkId detected on create. " +
				$"NetworkId={networkId}, Existing={existingAfterCandidate.name}, New={itemObject.name}"
			);
		}

		_items[networkId] = identity;

		// 跳过本地已处理过的丢弃回调
		bool skipDropCallbacks = !wasSnapshotCandidate && candidate != null && isDropSpawn;
		ApplyCreate(identity, position, rotation, velocity, isDropSpawn, skipDropCallbacks);
	}

	/// <summary>
	/// 主机处理拾取请求: 广播移除并遗忘物品.
	/// </summary>
	private static void HandlePickupRequest(DataReader reader) {
		if (!MPSteamworks.Instance.IsHost) return;

		var networkId = reader.GetString();

		MPMain.LogInfo($"[MP ItemSync] Host received pickup request. NetworkId={networkId}");

		if (string.IsNullOrEmpty(networkId)) return;

		if (!_items.ContainsKey(networkId)) {
			MPMain.LogWarning($"[MP ItemSync] Pickup request ignored. Unknown NetworkId={networkId}");
			return;
		}

		BroadcastRemove(networkId);
		Forget(networkId);
	}

	/// <summary>
	/// 主机处理丢弃请求: 实例化物品, 注册并广播创建.
	/// </summary>
	private static void HandleDropRequest(ulong senderId, DataReader reader) {
		if (!MPSteamworks.Instance.IsHost) return;

		var prefabKey = reader.GetString();
		var position = reader.GetVector3();
		var rotation = reader.GetQuaternion();
		var velocity = reader.GetVector3();

		if (string.IsNullOrEmpty(prefabKey)) return;

		var itemObject = InstantiateWorldItem(prefabKey, position, rotation);
		if (itemObject == null) {
			MPMain.LogWarning($"[MP ItemSync] Could not instantiate dropped item '{prefabKey}'.");
			return;
		}

		var identity = RegisterHostItem(itemObject, prefabKey);
		identity.OwnerId = senderId;

		MPMain.LogInfo(
			$"[MP ItemSync] Host accepted drop request. " +
			$"Sender={senderId}, Item={itemObject.name}, NetworkId={identity.NetworkId}, PrefabKey={prefabKey}"
		);

		ApplyCreate(identity, position, rotation, velocity, isDropSpawn: true, skipDropCallbacks: false);
		BroadcastCreate(identity, itemObject, velocity, isDropSpawn: true);
	}

	/// <summary>
	/// 处理移除消息: 遗忘物品.
	/// </summary>
	private static void HandleRemove(DataReader reader) {
		var networkId = reader.GetString();

		MPMain.LogInfo($"[MP ItemSync] Received remove. NetworkId={networkId}");

		if (string.IsNullOrEmpty(networkId)) return;

		Forget(networkId);
	}

	#endregion
	#region[状态应用与遗忘]

	/// <summary>
	/// 将网络接收到的初始状态写入物品对象.
	/// <para>
	/// 执行顺序:
	///   1. 设置 Transform 位置/旋转
	///   2. AssignDropObject: 通过反射将 Item_Object 引用写入 Item.dropObject,
	///      确保游戏本体在后续 OnPickup 时能正确找到并隐藏该物体.
	///      (若不设置, Item.OnPickup 中的 dropObject.gameObject.SetActive(false) 会对 null 调用)
	///   3. 可选调用 OnDrop: 设置掉落冷却 (dropWait), 让物品进入"刚掉落"的物理状态
	///   4. 设置 Rigidbody 速度
	///   5. 激活 GameObject
	/// <br/>
	/// isKinematic=false: 确保刚体参与物理模拟 (某些实例化流程可能保持 kinematic).
	/// <br/>
	/// try-finally 保证 ApplyingRemoteState 无论是否抛出异常都能正确重置,
	/// 防止后续所有同步操作被永久屏蔽.
	/// </para>
	/// </summary>
	private static void ApplyCreate(
		NetworkedItem identity,
		Vector3 position,
		Quaternion rotation,
		Vector3 velocity,
		bool isDropSpawn,
		bool skipDropCallbacks
	) {
		if (identity == null || identity.gameObject == null) return;

		ApplyingRemoteState = true;

		try {
			identity.transform.position = position;
			identity.transform.rotation = rotation;

			var itemObject = identity.GetComponent<Item_Object>();
			if (itemObject != null && itemObject.itemData != null) {
				_dropObjectField(itemObject.itemData) = itemObject;
			}

			// 触发丢弃回调 (本地已处理的可跳过)
			if (isDropSpawn && itemObject != null && !skipDropCallbacks) {
				itemObject.OnDrop();
			}

			// 设置刚体速度
			var rb = GetRigidbody(identity.gameObject);
			if (rb != null) {
				rb.isKinematic = false;
				rb.velocity = velocity;
			}

			identity.gameObject.SetActive(true);
		} finally {
			ApplyingRemoteState = false;
		}
	}

	/// <summary>
	/// 遗忘 (Forget) 一个物品: 从追踪中移除并隐藏/销毁其 GameObject.
	/// <para>
	/// 触发时机: 物品被拾取 (HandlePickupRequest 或 HandleRemove).
	/// <br/>
	/// WasInstantiatedBySync=true 的物品 (由同步系统实例化) 直接 Destroy,
	/// 场景原有物品 (WasInstantiatedBySync=false) 仅 SetActive(false) 隐藏,
	/// 不销毁 ── 以便断线重连或地图重载时可以复用.
	/// <br/>
	/// 若 _items 中找不到该 NetworkId, 尝试通过 StableSceneId 降级处理,
	/// 防止客户端收到 Remove 时因之前未通过正常 Create 注册而导致物品残留显示.
	/// </para>
	/// </summary>
	private static void Forget(string networkId) {
		if (!_items.TryGetValue(networkId, out var identity) || identity == null) {
			// 回退: 尝试通过稳定ID遗忘未知场景物品
			if (TryForgetUnknownSceneItem(networkId)) {
				return;
			}

			MPMain.LogWarning($"[MP ItemSync] Forget ignored. Unknown NetworkId={networkId}");
			return;
		}

		var itemObject = identity.GetComponent<Item_Object>();
		if (itemObject != null) {
			RemoveCandidate(itemObject);
		}

		MPMain.LogInfo(
			$"[MP ItemSync] Forget item. " +
			$"NetworkId={networkId}, " +
			$"Item={(identity.gameObject != null ? identity.gameObject.name : "null")}, " +
			$"WasInstantiatedBySync={identity.WasInstantiatedBySync}"
		);

		if (identity.gameObject != null) {
			identity.gameObject.SetActive(false);

			// 同步创建的物品直接销毁
			if (identity.WasInstantiatedBySync) {
				Object.Destroy(identity.gameObject);
			}
		}

		_items.Remove(networkId);
	}

	/// <summary>
	/// 回退: 通过稳定场景ID遗忘未追踪的物品.
	/// </summary>
	private static bool TryForgetUnknownSceneItem(string networkId) {
		if (string.IsNullOrEmpty(networkId)) return false;
		if (!networkId.StartsWith("sceneitem:", System.StringComparison.Ordinal)) return false;

		var itemObject = FindSceneItemByStableId(networkId);
		if (itemObject == null || itemObject.gameObject == null) return false;

		var identity = itemObject.GetComponent<NetworkedItem>();
		if (identity != null && string.IsNullOrEmpty(identity.NetworkId)) {
			identity.NetworkId = networkId;
			identity.StableSceneId = networkId;
		}

		RemoveCandidate(itemObject);
		itemObject.gameObject.SetActive(false);

		_items.Remove(networkId);

		MPMain.LogWarning(
			$"[MP ItemSync] Forgot unknown scene item by stable ID fallback. " +
			$"NetworkId={networkId}, Item={itemObject.name}"
		);

		return true;
	}

	#endregion
	#region[网络消息发送]

	private static void SendSnapshotReset(ulong clientId) {
		var writer = GetWriter(MPSteamworks.UserSteamId, clientId, PacketType.ItemStateSync);
		writer.Put((byte)ItemSyncAction.SnapshotReset);

		MPSteamworks.Instance.SendToPeer(clientId, writer);
	}

	private static void SendSnapshotFinalize(ulong clientId) {
		var writer = GetWriter(MPSteamworks.UserSteamId, clientId, PacketType.ItemStateSync);
		writer.Put((byte)ItemSyncAction.SnapshotFinalize);

		MPSteamworks.Instance.SendToPeer(clientId, writer);
	}

	private static void SendCreate(
		ulong clientId,
		NetworkedItem identity,
		Item_Object itemObject,
		Vector3 velocity,
		bool isDropSpawn
	) {
		if (identity == null || itemObject == null || string.IsNullOrEmpty(identity.NetworkId)) return;

		var writer = GetWriter(MPSteamworks.UserSteamId, clientId, PacketType.ItemStateSync);
		writer.Put((byte)ItemSyncAction.Create);
		writer.Put(identity.NetworkId);
		writer.Put(identity.PrefabKey);
		writer.Put(itemObject.transform.position);
		writer.Put(itemObject.transform.rotation);
		writer.Put(velocity);
		writer.Put(isDropSpawn);

		MPSteamworks.Instance.SendToPeer(clientId, writer);
	}

	private static void BroadcastCreate(NetworkedItem identity, Item_Object itemObject, Vector3 velocity, bool isDropSpawn) {
		if (identity == null || itemObject == null || string.IsNullOrEmpty(identity.NetworkId)) return;

		var writer = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.ItemStateSync);
		writer.Put((byte)ItemSyncAction.Create);
		writer.Put(identity.NetworkId);
		writer.Put(identity.PrefabKey);
		writer.Put(itemObject.transform.position);
		writer.Put(itemObject.transform.rotation);
		writer.Put(velocity);
		writer.Put(isDropSpawn);

		MPSteamworks.Instance.Broadcast(writer, SendType.Reliable);
	}

	private static void BroadcastRemove(string networkId) {
		var writer = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.ItemStateSync);
		writer.Put((byte)ItemSyncAction.Remove);
		writer.Put(networkId);

		MPMain.LogInfo($"[MP ItemSync] Broadcast remove. NetworkId={networkId}");

		MPSteamworks.Instance.Broadcast(writer, SendType.Reliable);
	}

	private static void SendPickupRequest(string networkId) {
		var writer = GetWriter(MPSteamworks.UserSteamId, MPSteamworks.Instance.HostSteamId, PacketType.ItemStateSync);
		writer.Put((byte)ItemSyncAction.PickupRequest);
		writer.Put(networkId);

		MPMain.LogInfo($"[MP ItemSync] Sent pickup request. NetworkId={networkId}");

		MPSteamworks.Instance.SendToHost(writer, SendType.Reliable);
	}

	private static void SendDropRequest(string prefabKey, Vector3 position, Quaternion rotation, Vector3 velocity) {
		var writer = GetWriter(MPSteamworks.UserSteamId, MPSteamworks.Instance.HostSteamId, PacketType.ItemStateSync);
		writer.Put((byte)ItemSyncAction.DropRequest);
		writer.Put(prefabKey);
		writer.Put(position);
		writer.Put(rotation);
		writer.Put(velocity);

		MPSteamworks.Instance.SendToHost(writer, SendType.Reliable);
	}

	#endregion
	#region[候选物品管理]

	/// <summary>
	/// 捕获所有场景物品为候选.
	/// </summary>
	private static void CaptureSceneCandidates(bool snapshotCandidate) {
		foreach (var itemObject in EnumerateSceneItems()) {
			RememberCandidate(itemObject, snapshotCandidate);
		}
	}

	/// <summary>
	/// 枚举场景中所有可同步的物品.
	/// </summary>
	private static IEnumerable<Item_Object> EnumerateSceneItems() {
		foreach (var itemObject in Object.FindObjectsOfType<Item_Object>()) {
			if (!IsSyncableWorldItem(itemObject)) continue;
			yield return itemObject;
		}
	}

	/// <summary>
	/// 记忆候选物品, 决定其匹配方式 (精确 StableSceneId 或模糊候选列表).
	/// <para>
	/// 优先级:
	///   1. snapshotCandidate=true 且能生成 StableSceneId:
	///      直接写入 _items, 后续 HandleCreate 可以通过 ID 精确匹配, 跳过模糊搜索.
	///   2. 已有 NetworkId (例如之前已被 Create 注册过):
	///      直接写入/更新 _items.
	///   3. 无身份且不满足上述条件:
	///      加入 _clientCandidates (和可选的 _snapshotCandidates) 等待模糊匹配.
	/// </para>
	/// </summary>
	private static void RememberCandidate(Item_Object itemObject, bool snapshotCandidate) {
		if (!IsSyncableWorldItem(itemObject)) return;

		var identity = GetOrCreateIdentity(itemObject.gameObject);

		// 尝试分配稳定场景ID并直接注册
		if (snapshotCandidate && TryAssignStableSceneIdentity(itemObject, identity)) {
			identity.PrefabKey = GetPrefabKey(itemObject);

			if (_items.TryGetValue(identity.NetworkId, out var existing) &&
				existing != null &&
				existing != identity) {
				MPMain.LogWarning(
					$"[MP ItemSync] Duplicate client candidate NetworkId detected. " +
					$"NetworkId={identity.NetworkId}, Existing={existing.name}, New={itemObject.name}"
				);
			}

			_items[identity.NetworkId] = identity;
			return;
		}

		// 已有 NetworkId 的物品直接注册
		if (!string.IsNullOrEmpty(identity.NetworkId)) {
			if (_items.TryGetValue(identity.NetworkId, out var existing) &&
				existing != null &&
				existing != identity) {
				MPMain.LogWarning(
					$"[MP ItemSync] Duplicate remembered NetworkId detected. " +
					$"NetworkId={identity.NetworkId}, Existing={existing.name}, New={itemObject.name}"
				);
			}

			_items[identity.NetworkId] = identity;
			return;
		}

		// 无身份的物品加入候选列表
		if (!_clientCandidates.Contains(itemObject)) {
			_clientCandidates.Add(itemObject);
		}

		if (snapshotCandidate && !_snapshotCandidates.Contains(itemObject)) {
			_snapshotCandidates.Add(itemObject);
		}
	}

	/// <summary>
	/// 从所有候选列表中移除物品.
	/// </summary>
	private static void RemoveCandidate(Item_Object itemObject) {
		_clientCandidates.Remove(itemObject);
		_snapshotCandidates.Remove(itemObject);
		RemovePendingLocalDrop(itemObject);
	}

	/// <summary>
	/// 在客户端候选中查找匹配物品 (按 prefabKey 和距离).
	/// </summary>
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

	#endregion
	#region[本地丢弃管理]

	/// <summary>
	/// 记录待处理的本地丢弃.
	/// </summary>
	private static void RememberPendingLocalDrop(Item_Object itemObject, string prefabKey) {
		if (!IsSyncableWorldItem(itemObject) || string.IsNullOrEmpty(prefabKey)) return;

		RemoveDestroyedPendingLocalDrops();
		RemovePendingLocalDrop(itemObject);

		_pendingLocalDrops.Add(new PendingLocalDrop(itemObject, prefabKey, Time.time));
	}

	/// <summary>
	/// 在待处理丢弃中查找匹配物品 (按 prefabKey 和距离).
	/// </summary>
	private static Item_Object FindPendingLocalDrop(string prefabKey, Vector3 position) {
		RemoveDestroyedPendingLocalDrops();

		Item_Object best = null;
		float bestDistance = float.MaxValue;

		for (int i = _pendingLocalDrops.Count - 1; i >= 0; i--) {
			var pending = _pendingLocalDrops[i];
			if (!PrefabKeysMatch(prefabKey, pending.PrefabKey)) continue;

			float distance = (pending.ItemObject.transform.position - position).sqrMagnitude;
			if (distance > LocalDropMatchDistanceSqr || distance >= bestDistance) continue;

			best = pending.ItemObject;
			bestDistance = distance;
		}

		if (best != null) {
			RemoveCandidate(best);
		}

		return best;
	}

	/// <summary>
	/// 检查物品是否为待处理的本地丢弃.
	/// </summary>
	private static bool IsPendingLocalDrop(Item_Object itemObject) {
		if (itemObject == null) return false;

		RemoveDestroyedPendingLocalDrops();

		for (int i = 0; i < _pendingLocalDrops.Count; i++) {
			if (_pendingLocalDrops[i].ItemObject == itemObject) {
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// 从待处理丢弃中移除指定物品.
	/// </summary>
	private static void RemovePendingLocalDrop(Item_Object itemObject) {
		if (itemObject == null) return;

		for (int i = _pendingLocalDrops.Count - 1; i >= 0; i--) {
			if (_pendingLocalDrops[i].ItemObject == itemObject) {
				_pendingLocalDrops.RemoveAt(i);
			}
		}
	}

	#endregion
	#region[清理与验证]

	/// <summary>
	/// 清理已销毁的候选物品.
	/// </summary>
	private static void RemoveDestroyedCandidates() {
		for (int i = _clientCandidates.Count - 1; i >= 0; i--) {
			if (!IsSyncableWorldItem(_clientCandidates[i])) {
				_clientCandidates.RemoveAt(i);
			}
		}

		for (int i = _snapshotCandidates.Count - 1; i >= 0; i--) {
			if (!IsSyncableWorldItem(_snapshotCandidates[i])) {
				_snapshotCandidates.RemoveAt(i);
			}
		}

		RemoveDestroyedPendingLocalDrops();
	}

	/// <summary>
	/// 清理已销毁或过期的待处理丢弃.
	/// </summary>
	private static void RemoveDestroyedPendingLocalDrops() {
		float minCreatedAt = Time.time - LocalDropMaxAge;

		for (int i = _pendingLocalDrops.Count - 1; i >= 0; i--) {
			var pending = _pendingLocalDrops[i];
			if (!IsSyncableWorldItem(pending.ItemObject) || pending.CreatedAt < minCreatedAt) {
				_pendingLocalDrops.RemoveAt(i);
			}
		}
	}

	#endregion
	#region[拾取抑制]

	/// <summary>
	/// 记录拾取抑制 (丢弃后短暂时间内禁止拾取).
	/// </summary>
	private static void RememberSuppressedPickup(Item_Object itemObject) {
		if (itemObject == null || itemObject.gameObject == null) return;

		RemoveExpiredSuppressedPickups();
		_suppressedPickupObjects[itemObject.gameObject.GetInstanceID()] = Time.time + LocalDropPickupSuppressWindow;
	}

	/// <summary>
	/// 检查物品是否应抑制拾取.
	/// </summary>
	private static bool ShouldSuppressLocalPickup(Item_Object itemObject) {
		if (itemObject == null || itemObject.gameObject == null) return false;

		RemoveExpiredSuppressedPickups();

		int instanceId = itemObject.gameObject.GetInstanceID();
		if (!_suppressedPickupObjects.TryGetValue(instanceId, out float untilTime)) {
			return false;
		}

		if (Time.time > untilTime) {
			_suppressedPickupObjects.Remove(instanceId);
			return false;
		}

		MPMain.LogInfo($"[MP ItemSync] Suppressed pickup immediately after drop. Item={itemObject.name}");
		return true;
	}

	/// <summary>
	/// 清理过期的拾取抑制记录.
	/// </summary>
	private static void RemoveExpiredSuppressedPickups() {
		if (_suppressedPickupObjects.Count == 0) return;

		foreach (var instanceId in new List<int>(_suppressedPickupObjects.Keys)) {
			if (_suppressedPickupObjects.TryGetValue(instanceId, out float untilTime) && Time.time > untilTime) {
				_suppressedPickupObjects.Remove(instanceId);
			}
		}
	}

	#endregion
	#region[工具方法]

	/// <summary>
	/// 判断 Item_Object 是否为有效的可同步世界物品.
	/// <para>
	/// 排除条件:
	///   • itemObject 或其 GameObject 为 null: 已销毁
	///   • !activeInHierarchy: 已被隐藏 (例如被拾取后 SetActive(false))
	///   • scene.name 为空: 该物体是预制体资产或 DontDestroyOnLoad 对象, 不属于场景
	///   • itemData 为 null: 未关联 Item 数据, 不可同步
	///   • itemData.inBag=true: 物品在玩家背包中 (Item.inBag 在物品入库存时为 true),
	///     背包物品通过其他系统同步, 不在 ItemSyncManager 的管辖范围内
	/// </para>
	/// </summary>
	private static bool IsSyncableWorldItem(Item_Object itemObject) {
		if (itemObject == null || itemObject.gameObject == null) return false;// GameObject 为 null
		if (!itemObject.gameObject.activeInHierarchy) return false;// 已被隐藏
		if (string.IsNullOrEmpty(itemObject.gameObject.scene.name)) return false;// 是预制体
		if (itemObject.itemData == null) return false;// 未关联 Item 数据
		if (IsBlacklisted(itemObject)) return false;// 在黑名单
		if (itemObject.itemData.inBag) return false;// 在背包中

		return true; 
	}

	/// <summary>
	/// 判断 Item_Object 是否为有效的可同步丢弃物品
	/// </summary>
	private static bool IsSyncableDropItem(Item_Object itemObject) {
		if (itemObject == null || itemObject.gameObject == null) return false;// GameObject 为 null
		if (!itemObject.gameObject.activeInHierarchy) return false;// 已被隐藏
		if (string.IsNullOrEmpty(itemObject.gameObject.scene.name)) return false;// 是预制体
		if (itemObject.itemData == null) return false;// 未关联 Item 数据
		if (itemObject.itemData.inBag) return false;// 在背包中

		return true;
	}

	/// <summary>
	/// 实例化世界物品并放置在关卡根节点下.
	/// <para>
	/// ApplyingRemoteState=true 的作用: 阻止 Item_Object.Start() → InitializeItemData 触发
	/// 的拾取/丢弃事件回调 (通过 Harmony 补丁检查该标志) 进入同步逻辑.
	/// <br/>
	/// 手动调用 InitializeItemData: 确保 Item.dropObject 指向刚实例化的 Item_Object,
	/// 这一步在 Item_Object.Start() 中由游戏本体完成, 但在同步流程中我们需要立即可用.
	/// <br/>
	/// levelRoot 父级: 保持与游戏本体 Item.Drop() 的行为一致
	/// (Item.Drop 中也会 SetParent(WorldLoader.GetCurrentLevelParentRoot())).
	/// </para>
	/// </summary>
	private static Item_Object InstantiateWorldItem(string prefabKey, Vector3 position, Quaternion rotation) {
		GameObject prefab = CL_AssetManager.GetAssetGameObject(prefabKey);
		if (prefab == null) return null;

		ApplyingRemoteState = true;

		try {
			var instance = Object.Instantiate(prefab, position, rotation);
			var levelRoot = WorldLoader.GetCurrentLevelParentRoot();

			if (levelRoot != null) {
				instance.transform.SetParent(levelRoot);
			}

			var itemComponent = instance.GetComponent<Item_Object>() ??
								instance.GetComponentInChildren<Item_Object>(true);

			if (itemComponent != null && itemComponent.itemData != null) {
				itemComponent.itemData.InitializeItemData(itemComponent);
			}

			return itemComponent;
		} finally {
			ApplyingRemoteState = false;
		}
	}

	/// <summary>
	/// 获取物品的物理速度 (零速度过滤).
	/// </summary>
	private static Vector3 GetVelocity(Item_Object itemObject) {
		var rb = GetRigidbody(itemObject.gameObject);
		if (rb == null) return Vector3.zero;

		return rb.velocity.sqrMagnitude > VelocityEpsilonSqr ? rb.velocity : Vector3.zero;
	}

	/// <summary>
	/// 获取物体或其子物体的刚体组件.
	/// </summary>
	private static Rigidbody GetRigidbody(GameObject gameObject) {
		if (gameObject == null) return null;

		return gameObject.GetComponent<Rigidbody>() ?? gameObject.GetComponentInChildren<Rigidbody>();
	}

	/// <summary>
	/// 获取或添加 NetworkedItem 组件.
	/// </summary>
	private static NetworkedItem GetOrCreateIdentity(GameObject gameObject) {
		var identity = gameObject.GetComponent<NetworkedItem>();

		if (identity == null) {
			identity = gameObject.AddComponent<NetworkedItem>();
		}

		return identity;
	}

	#endregion
	#region[稳定场景ID]

	/// <summary>
	/// 通过稳定场景ID查找物品.
	/// </summary>
	private static Item_Object FindSceneItemByStableId(string networkId) {
		if (string.IsNullOrEmpty(networkId) ||
			!networkId.StartsWith("sceneitem:", System.StringComparison.Ordinal)) {
			return null;
		}

		foreach (var itemObject in EnumerateSceneItems()) {
			if (string.Equals(GetStableSceneItemId(itemObject), networkId, System.StringComparison.Ordinal)) {
				return itemObject;
			}
		}

		return null;
	}

	/// <summary>
	/// 尝试为物品分配稳定场景身份 (基于场景层级路径和变换锚点).
	/// </summary>
	private static bool TryAssignStableSceneIdentity(Item_Object itemObject, NetworkedItem identity) {
		if (itemObject == null || identity == null) return false;

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
	/// 设计目标: 主机和客户端加载同一关卡后, 对同一个 Item_Object 生成相同的 ID,
	/// 从而在快照协议中无需按距离模糊匹配, 直接精确对应.
	/// <br/>
	/// 组成部分:
	///   • 场景名: 区分不同关卡中的同名物品
	///   • 层级路径 (BuildTransformPath): "Root[0]/Child[1]" 格式,
	///     含 sibling index 以区分同名同级兄弟节点
	///   • 变换锚点 (BuildStableTransformAnchor): 量化后的本地位置+旋转,
	///     作为对路径的双重校验, 防止场景改动导致 sibling index 错位
	/// <br/>
	/// 若已有 StableSceneId 则直接返回, 避免重复计算 (字符串拼接的 GC 开销).
	/// </para>
	/// </summary>
	private static string GetStableSceneItemId(Item_Object itemObject) {
		if (itemObject == null || itemObject.gameObject == null) return string.Empty;

		if (!itemObject.gameObject.scene.IsValid() ||
			string.IsNullOrEmpty(itemObject.gameObject.scene.name)) {
			return string.Empty;
		}

		var identity = itemObject.GetComponent<NetworkedItem>();
		if (identity != null && !string.IsNullOrEmpty(identity.StableSceneId)) {
			return identity.StableSceneId;
		}

		string path = BuildTransformPath(itemObject.transform);
		if (string.IsNullOrEmpty(path)) return string.Empty;

		string anchor = BuildStableTransformAnchor(itemObject.transform);
		return $"sceneitem:{itemObject.gameObject.scene.name}:{path}|{anchor}";
	}

	/// <summary>
	/// 构建变换层级路径. 例如 "Root[0]/Child[1]".
	/// </summary>
	private static string BuildTransformPath(Transform transform) {
		if (transform == null) return string.Empty;

		var builder = new System.Text.StringBuilder();
		var parts = new Stack<string>();
		var current = transform;

		while (current != null) {
			parts.Push($"{MPUtil.CleanCloneName(current.name)}[{current.GetSiblingIndex()}]");
			current = current.parent;
		}

		while (parts.Count > 0) {
			if (builder.Length > 0) builder.Append('/');
			builder.Append(parts.Pop());
		}

		return builder.ToString();
	}

	/// <summary>
	/// 构建变换锚点字符串 (量化后的本地位置和旋转).
	/// </summary>
	private static string BuildStableTransformAnchor(Transform transform) {
		if (transform == null) return string.Empty;

		Vector3 localPosition = transform.localPosition;
		Vector3 localRotation = transform.localEulerAngles;

		return $"lp:{Quantize(localPosition.x, StableIdPositionPrecision)}," +
			   $"{Quantize(localPosition.y, StableIdPositionPrecision)}," +
			   $"{Quantize(localPosition.z, StableIdPositionPrecision)}" +
			   $"|lr:{Quantize(NormalizeAngle(localRotation.x), StableIdRotationPrecision)}," +
			   $"{Quantize(NormalizeAngle(localRotation.y), StableIdRotationPrecision)}," +
			   $"{Quantize(NormalizeAngle(localRotation.z), StableIdRotationPrecision)}";
	}

	/// <summary>
	/// 量化值以提高匹配稳定性.
	/// </summary>
	private static int Quantize(float value, float precision) {
		return Mathf.RoundToInt(value * precision);
	}

	/// <summary>
	/// 将角度归一化到 [0, 360).
	/// </summary>
	private static float NormalizeAngle(float value) {
		value %= 360f;

		if (value < 0f) {
			value += 360f;
		}

		return value;
	}

	#endregion
	#region[字符串工具]

	/// <summary>
	/// 获取物品的预制体键 (优先使用 itemData.prefabName, 否则使用清理后的物体名).
	/// </summary>
	private static string GetPrefabKey(Item_Object itemObject) {
		if (itemObject == null) return string.Empty;

		if (itemObject.itemData != null && !string.IsNullOrEmpty(itemObject.itemData.prefabName)) {
			return itemObject.itemData.prefabName;
		}

		return MPUtil.CleanCloneName(itemObject.gameObject.name);
	}

	/// <summary>
	/// 比较两个预制体键是否匹配 (忽略大小写, 忽略 Clone 后缀).
	/// </summary>
	private static bool PrefabKeysMatch(string a, string b) {
		if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;

		return string.Equals(MPUtil.CleanCloneName(a), MPUtil.CleanCloneName(b), System.StringComparison.OrdinalIgnoreCase);
	}

	#endregion
	#region[内部类型]

	/// <summary>
	/// 待处理的本地丢弃记录.
	/// <para>
	/// 生命周期:
	///   创建: 客户端本地丢弃时由 RememberPendingLocalDrop 写入
	///   消费: 收到主机 Create 消息时由 FindPendingLocalDrop 匹配后移除
	///   过期: CreatedAt + LocalDropMaxAge 到达时由 RemoveDestroyedPendingLocalDrops 清理
	/// <br/>
	/// sealed: 该类仅在 ItemSyncManager 内部使用, 无需派生.
	/// </para>
	/// </summary>
	private sealed class PendingLocalDrop {
		public PendingLocalDrop(Item_Object itemObject, string prefabKey, float createdAt) {
			ItemObject = itemObject;
			PrefabKey = prefabKey;
			CreatedAt = createdAt;
		}

		public Item_Object ItemObject { get; } // 物品对象
		public string PrefabKey { get; }       // 预制体键
		public float CreatedAt { get; }        // 创建时间 (Time.time)
	}

	#endregion
	#region [黑名单配置]

	// 手电筒,信号枪,冰枪单独处理
	private static readonly HashSet<string> _blacklistedPrefabNames = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) {
		"Item_Flashlight",
		"Item_Flaregun",
		"Item_Cryogun",
	};

	// 标签单独处理
	private static readonly HashSet<string> _blacklistedItemTags = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) {
		"artifact",
		"disk"
	};

	/// <summary>
	/// 判定当前物品预制体是否在黑名单中
	/// </summary>
	public static bool IsBlacklisted(string prefabName) {
		if (string.IsNullOrEmpty(prefabName)) return false;
		return _blacklistedPrefabNames.Contains(MPUtil.CleanCloneName(prefabName));
	}

	/// <summary>
	/// 判定当前物品标签是否在黑名单中
	/// </summary>
	public static bool IsBlacklisted(List<string> itemTags) {
		return _blacklistedItemTags.Intersect(itemTags).Any(); ;
	}

	/// <summary>
	/// 判定核心数据对象是否在黑名单中
	/// </summary>
	public static bool IsBlacklisted(Item item) {
		return item != null && (IsBlacklisted(item.prefabName)|| IsBlacklisted(item.itemTags));
	}

	/// <summary>
	/// 判定场景表现层对象是否在黑名单中
	/// </summary>
	public static bool IsBlacklisted(Item_Object itemObj) {
		return itemObj != null && IsBlacklisted(itemObj.itemData);
	}

	#endregion
}

