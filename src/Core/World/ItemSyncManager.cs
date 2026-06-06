using HarmonyLib;
using Steamworks.Data;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using WKMPMod.Component;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.NetWork;
using WKMPMod.World;
using static WKMPMod.Data.MPWriterPool;
using Object = UnityEngine.Object;

namespace WKMPMod.World {
	// 物品同步操作类型枚举
	public enum ItemSyncAction : byte {
		SnapshotReset = 0,		// 快照重置
		SnapshotFinalize = 1,	// 快照完成
		Create = 2,				// 创建物品
		PickupRequest = 3,		// 拾取请求
		DropRequest = 4,		// 丢弃请求
		Remove = 5,				// 移除物品
	}

	/// <summary>
	/// 管理多人游戏中物品的同步状态. 处理物品创建、拾取、丢弃、快照以及网络消息.
	/// </summary>
	public static class ItemSyncManager {
		#region[常量]
		private const float CandidateMatchDistanceSqr = 0.5f;		// 候选匹配最大距离平方
		private const float LocalDropMatchDistanceSqr = 25f;		// 本地丢弃匹配最大距离平方
		private const float LocalDropMaxAge = 3f;					// 本地丢弃记录最大保留时间
		private const float LocalDropPickupSuppressWindow = 0.2f;	// 丢弃后拾取抑制窗口
		private const float VelocityEpsilonSqr = 0.0025f;			// 速度零阈值平方
		private const float WorldReadyTimeout = 12f;				// 世界就绪等待超时
		private const float StableIdPositionPrecision = 20f;		// 稳定ID位置量化精度
		private const float StableIdRotationPrecision = 5f;			// 稳定ID旋转量化精度

		private const int SnapshotItemsPerFrame = 10;                 // 每帧快照物品数量
		#endregion

		#region[静态字段 - 反射]
		// 反射获取 Item 类中的 dropObject 字段
		private static readonly System.Reflection.FieldInfo _dropObjectField =
			typeof(Item).GetField("dropObject",
				System.Reflection.BindingFlags.Instance |
				System.Reflection.BindingFlags.Public |
				System.Reflection.BindingFlags.NonPublic);
		#endregion

		#region[静态字段 - 状态]
		private static readonly Dictionary<string, NetworkedItem> _items = new();			// 所有已追踪物品 (NetworkId -> NetworkedItem)
		private static readonly List<Item_Object> _clientCandidates = new();				// 客户端候选物品列表
		private static readonly List<PendingLocalDrop> _pendingLocalDrops = new();			// 待处理的本地丢弃
		private static readonly List<Item_Object> _snapshotCandidates = new();				// 快照候选物品
		private static readonly Dictionary<ulong, Coroutine> _snapshotRoutines = new();		// 客户端快照协程
		private static readonly Dictionary<int, float> _suppressedPickupObjects = new();	// 抑制拾取的物品 (实例ID -> 过期时间)
		#endregion

		#region[静态字段 - 控制]
		private static ulong _nextHostItemId = 1;		// 主机下一个物品ID
		private static Coroutine _prepareRoutine;		// 准备协程
		private static Coroutine _hostDiscoveryRoutine;	// 主机发现协程
		private static bool _hostSceneItemsRegistered;	// 主机场景物品是否已注册
		#endregion

		/// <summary>
		/// 是否正在应用远程状态 (用于防止递归触发).
		/// </summary>
		public static bool ApplyingRemoteState { get; private set; }

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
		/// 本地拾取物品通知. 主机广播移除, 客户端发送拾取请求.
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
		/// 本地丢弃物品通知. 主机直接注册并广播, 客户端记录并发送请求.
		/// </summary>
		public static void NotifyLocalDrop(Item item) {
			if (ApplyingRemoteState || item == null || !MPCore.CanSync) return;

			var itemObject = ResolveDropObject(item);
			if (!IsSyncableWorldItem(itemObject)) return;

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
		public static void HandleItemState(ulong senderId, DataReader reader) {
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
		/// 世界准备协程: 等待世界就绪, 主机注册场景物品, 客户端捕获候选.
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
		/// 向客户端发送物品快照协程: 先重置, 再逐帧发送物品, 最后完成.
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
		/// 客户端收到快照完成: 隐藏未匹配的场景候选物品.
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
		/// 客户端收到创建消息: 匹配候选或实例化新物品, 并应用状态.
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
		/// 应用创建状态到物品: 设置位置旋转速度, 并可选触发丢弃回调.
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
					AssignDropObject(itemObject.itemData, itemObject);
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
		/// 遗忘物品: 隐藏并可能销毁, 从追踪中移除.
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
		/// 记忆候选物品: 优先分配稳定场景ID, 否则加入候选列表.
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
		/// 检查物品是否为有效的可同步世界物品.
		/// </summary>
		private static bool IsSyncableWorldItem(Item_Object itemObject) {
			if (itemObject == null || itemObject.gameObject == null) return false;
			if (!itemObject.gameObject.activeInHierarchy) return false;
			if (string.IsNullOrEmpty(itemObject.gameObject.scene.name)) return false;
			if (itemObject.itemData == null) return false;

			return !itemObject.itemData.inBag; // 不在背包中
		}

		/// <summary>
		/// 实例化世界物品并放置在关卡根节点下.
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
		/// 通过反射获取 Item 的 dropObject 字段.
		/// </summary>
		private static Item_Object ResolveDropObject(Item item) {
			if (item == null) return null;

			if (_dropObjectField == null) return null;

			try {
				return _dropObjectField.GetValue(item) as Item_Object;
			} catch (System.Exception ex) {
				MPMain.LogWarning($"[MP ItemSync] Failed to resolve dropObject via reflection: {ex.Message}");
				return null;
			}
		}

		/// <summary>
		/// 通过反射设置 Item 的 dropObject 字段.
		/// </summary>
		private static void AssignDropObject(Item item, Item_Object itemObject) {
			if (item == null || itemObject == null) return;

			if (_dropObjectField == null) return;

			try {
				_dropObjectField.SetValue(item, itemObject);
			} catch (System.Exception ex) {
				MPMain.LogWarning($"[MP ItemSync] Failed to assign dropObject via reflection: {ex.Message}");
			}
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
		/// 生成场景物品的稳定唯一ID.
		/// 格式: sceneitem:{场景名}:{层级路径}|{变换锚点}
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
				parts.Push($"{CleanCloneName(current.name)}[{current.GetSiblingIndex()}]");
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

			return CleanCloneName(itemObject.gameObject.name);
		}

		/// <summary>
		/// 比较两个预制体键是否匹配 (忽略大小写, 忽略 Clone 后缀).
		/// </summary>
		private static bool PrefabKeysMatch(string a, string b) {
			if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;

			return string.Equals(CleanCloneName(a), CleanCloneName(b), System.StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>
		/// 去除 "(Clone)" 后缀并修剪空白.
		/// </summary>
		private static string CleanCloneName(string value) {
			if (string.IsNullOrEmpty(value)) return string.Empty;

			return value.Replace("(Clone)", string.Empty).Trim();
		}
		#endregion

		#region[内部类型]
		/// <summary>
		/// 待处理的本地丢弃记录. 包含物品引用、预制体键和创建时间.
		/// sealed 表示该类不可被继承, 用于防止进一步派生.
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
	}
}
