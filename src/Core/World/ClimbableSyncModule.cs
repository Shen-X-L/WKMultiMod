using HarmonyLib;
using Newtonsoft.Json.Linq;
using Steamworks.Data;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using WKMPMod.Asset;
using WKMPMod.Component;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.NetWork;
using WKMPMod.Patch;
using WKMPMod.Util;
using static WKMPMod.Data.MPWriterPool;
using Object = UnityEngine.Object;

namespace WKMPMod.World;

#region[枚举]

/// <summary>
/// Piton同步操作类型
/// Piton sync action type
/// </summary>
public enum PitonSyncAction : byte {
	Create = 0, // 创建
	Update = 1, // 更新
	Remove = 2, // 移除
	HammerIn = 3,   // 锤入
	Weaken = 4, // 拔松
	BreakRequest = 5,  // 拔出请求 非创建者通知创建者岩钉被拔出
	Break = 6,  // 拔出
	CreateChunkRequest = 7, // 已生成攀爬物请求: 客机向主机请求所有的已生成攀爬物
	CreateChunk = 8,      // 已生成攀爬物: 主机发送的已生成攀爬物包
}

#endregion

/// <summary>
/// Piton/可攀爬物体同步管理器
/// - 负责本地Piton创建, 状态更新和删除同步
/// - 负责接收并应用远程玩家的Piton状态
/// - 使用NetworkedPiton组件保存网络身份和上次同步状态
///
/// Piton / climbable object sync manager
/// - Handles local piton creation, state updates and removal sync
/// - Receives and applies remote player piton state
/// - Uses NetworkedPiton to store network identity and last synced state
/// </summary>
public class ClimbableSyncModule : Singleton<ClimbableSyncModule>, ISyncModule {
	// Projectile.sourceEntity字段缓存, 用于判断投射物是否属于本地玩家
	// Cached Projectile.sourceEntity field, used to check if a projectile belongs to the local player
	#region[私有字段获取]

	private static readonly AccessTools.FieldRef<Projectile, GameEntity> _sourceEntityField =
		AccessTools.FieldRefAccess<Projectile, GameEntity>("sourceEntity");
	// 添加关卡所有物
	private static readonly Action<M_Level, GameObject> _addPlacedObjectMethod =
		AccessTools.MethodDelegate<Action<M_Level, GameObject>>(AccessTools.Method(typeof(M_Level), "AddPlacedObject"));
	private static readonly AccessTools.FieldRef<CL_Handhold, List<ENT_Player.Hand>> _handsField =
		AccessTools.FieldRefAccess<CL_Handhold, List<ENT_Player.Hand>>("hands");
	private static readonly AccessTools.FieldRef<CL_Handhold, UT_SoftParent> _softParentField =
		AccessTools.FieldRefAccess<CL_Handhold, UT_SoftParent>("softParent");
	private static readonly AccessTools.FieldRef<CL_Handhold, float> _offsetAmountField =
		AccessTools.FieldRefAccess<CL_Handhold, float>("offsetAmount");

	#endregion

	#region[ISyncModule接口实现]

	public string ModuleName => "ClimbableSync";

	/// <summary>
	/// 是否开启了攀爬道具同步
	/// </summary>
	public bool IsEnabled { get; set; } = true;

	public void OnReset() {
		ResetState();
		if (MPCore.CanSync && !MPSteamworks.IsHost) SendChunkRequest();
	}

	// 没有联机情况 清空物品发送协程
	public void OnLeave() {
		_globalPersistentTable.Clear();
		if (WorldSyncManager.Instance != null)
			foreach (var coroutine in _sendRoutines.Values)
				if (coroutine != null) WorldSyncManager.Instance.StopCoroutine(coroutine);

		_sendRoutines.Clear();
		_nextLocalId = 1;

		ResetState();
	}

	public void OnEnd() => OnLeave();

	#endregion

	#region[字段和属性]

	#region[	新玩家记录同步]

	// 全局持久化数据表 (仅主机或作为全局历史状态, 解决关卡卸载数据丢失问题)
	private readonly Dictionary<ulong, ClimbableData> _globalPersistentTable = new();
	/// <summary>
	/// 每个客户端对应的快照发送协程. Key=客户端SteamId, Value=协程引用.
	/// </summary>
	private readonly Dictionary<IDType, Coroutine> _sendRoutines = new();
	private const int ChunkItemsPerFrame = 10; // 每帧补发死亡记录数量上限

	#endregion

	#region[	同步时数据]

	// 关键: 用来接收捕获到的对象
	// Used to receive captured objects
	private static List<GameObject> _capturedPitons = new List<GameObject>();

	// 已同步的CL_Handhold对象表, key为NetworkId
	// Synced handhold lookup, keyed by NetworkId
	private readonly Dictionary<ulong, NetworkedClimable> _handhold = new();

	// 已同步的CL_Handhold对象表, key为NetworkId
	// Synced handhold lookup, keyed by NetworkId
	private readonly Dictionary<ulong, NetworkedClimable> _localHandhold = new();

	// 玩家正在抓握的 攀爬物 和 攀爬时间记录
	private readonly List<(NetworkedClimable Identity, float LastTime)> _holdingList = new();
	// 建立 CL_Handhold 到 NetworkedClimable 的映射表
	private static readonly Dictionary<CL_Handhold, NetworkedClimable> _handholdLookup = new();

	// 下一个本地ID
	private ulong _nextLocalId = 1;

	/// <summary>
	/// 是否正在应用远程状态
	/// 用于防止应用远程数据时再次触发本地广播, 造成循环同步
	///
	/// Whether remote state is currently being applied
	/// Used to prevent broadcasting again while applying remote data
	/// </summary>
	public bool ApplyingRemoteState { get; private set; }

	/// <summary>
	/// 非创建者拔出岩钉时创建的临时掉落物
	/// </summary>
	private static readonly List<(ulong id, GameObject obj)> _breakItemObject = new();

	#endregion

	#region[	分帧发送状态机]

	// 周期性更新间隔 (秒)
	private const float PeriodicUpdateInterval = 0.10f;
	private int _maxSyncPerFrame = 10; // 每次最多广播物品数量
	private float _timer = 0f;
	private bool _isSweeping = false;
	private int _sweepIndex = 0;

	// 缓存本轮待发送的物品队列, 避免产生 GC
	private readonly List<NetworkedClimable> _sweepQueue = new();
	private readonly List<NetworkedClimable> _batchBuffer = new();
	private readonly List<ulong> _localKeysCache = new List<ulong>();

	#endregion

	#endregion

	#region[生命周期函数]

	/// <summary>
	/// 完全重置敌人同步状态: 停止所有协程, 清空字典, 重置标志.
	/// </summary>
	public void ResetState() {
		// 重置分帧打包状态机
		_timer = 0f;
		_isSweeping = false;
		_sweepIndex = 0;
		_sweepQueue.Clear();
		_batchBuffer.Clear();
		_localKeysCache.Clear();
		// 清空已攀爬物字典
		_capturedPitons.Clear();
		_handhold.Clear();
		_localHandhold.Clear();
		_holdingList.Clear();
		_handholdLookup.Clear();
		_breakItemObject.Clear();
		ApplyingRemoteState = false;
	}

	#endregion

	#region[API 捕获与注册]

	/// <summary>
	/// List.Add 方法的包装, 供 IL 调用.
	/// 保存捕获到的Piton对象 (仅当包含CL_Handhold组件时).
	/// </summary>
	public static void SaveCapturedPiton(GameObject go) {
		if (go?.GetComponentInChildren<CL_Handhold>(true) != null) _capturedPitons.Add(go);
	}

	/// <summary>
	/// 注册本地新放置的Piton (基于IL捕获列表).
	/// 通过修改IL代码, 直接返回HandItem_Piton组件, 并广播Create消息.
	///
	/// Registers a newly placed local piton
	/// Finds the newly spawned climbable using the known handhold list and broadcasts a Create message
	/// </summary>
	public void RegisterNewLocalPiton() {
		if (!MPCore.IsReady || ApplyingRemoteState) {
			_capturedPitons.Clear();
			return;
		}

		foreach (var piton in _capturedPitons) {
			var root = GetClimbableRoot(piton);
			if (root == null) continue;
			RegisterLocalClimbable(root);
		}

		// 清理捕获列表以备下次使用
		_capturedPitons.Clear();
	}

	/// <summary>
	/// 注册本地投射物生成的可攀爬物体 (基于IL捕获列表).
	/// 例如由射击类物品产生的钩点/可攀爬对象.
	///
	/// Registers a climbable spawned by a local projectile
	/// For example climbable points created by shoot-type items
	/// </summary>
	public void RegisterNewLocalProjectileClimbable(Projectile source, RaycastHit hit) {
		if (!MPCore.IsReady || ApplyingRemoteState || source == null || !IsLocalProjectile(source)) {
			_capturedPitons.Clear();
			return;
		}

		foreach (var piton in _capturedPitons) {
			var root = GetClimbableRoot(piton);
			if (root == null) continue;
			RegisterLocalClimbable(root);
		}

		_capturedPitons.Clear();
	}

	/// <summary>
	/// 添加玩家正在抓握的攀爬物
	/// </summary>
	public void OnLocalHandholdGrabbed(NetworkedClimable identity) {
		if (identity == null) return;

		// 本地物品靠其他操作管理生命周期
		if (_localHandhold.ContainsKey(identity.NetworkId)) return;

		// 查重：若已存在则不重复添加
		for (int i = 0; i < _holdingList.Count; i++)
			if (_holdingList[i].Identity == identity) return;

		_holdingList.Add((identity, Time.time));
	}

	/// <summary>
	/// 拔出并广播
	/// </summary>
	public void OnLocalHandholdReleased(NetworkedClimable identity) {
		if (identity == null) return;

		// 本地物品靠其他操作管理生命周期
		if (_localHandhold.ContainsKey(identity.NetworkId)) return;

		for (int i = _holdingList.Count - 1; i >= 0; i--) {
			if (_holdingList[i].Identity == identity) {
				// 当最后一只手放开时, 结算并发送最后的时间差, 然后移除
				if (_handsField(identity.Handhold)?.Count == 0) {
					float deltaTime = Time.time - _holdingList[i].LastTime;
					SendWeaken(identity, deltaTime);
					_holdingList.RemoveAt(i);
				}
				break;
			}
		}
	}

	#endregion

	#region[协程与分帧同步]

	/// <summary>
	/// 由 WorldSyncManager 每帧在 LateUpdate 调用
	/// </summary>
	public void OnSyncUpdate(float deltaTime) {
		if (!MPCore.CanSync || !IsEnabled) {
			_timer = 0f;
			_isSweeping = false;
			_sweepIndex = 0;
			_sweepQueue.Clear();
			_batchBuffer.Clear();
			_localKeysCache.Clear();
			return;
		}
		// 处于空闲状态:累加定时器, 等待下一个同步周期到来
		if (!_isSweeping) {
			_timer += deltaTime;
			if (_timer >= PeriodicUpdateInterval) {
				// 保留余数, 保证计时精准
				_timer %= PeriodicUpdateInterval;
				// 开启新一轮的全局扫描
				StartNewSweep();
				// 手持攀爬物拔出状态更新
				HoldWeaken();
			}
		}
		// 处于处理数据包状态 连续跨帧处理数据包
		if (_isSweeping) FlushNextBatch();
	}

	/// <summary>
	/// 开启新一轮同步, 扫描并收集所有需要同步的敌人
	/// </summary>
	private void StartNewSweep() {
		_sweepQueue.Clear();
		_sweepIndex = 0;

		_localKeysCache.Clear();

		foreach (var key in _localHandhold.Keys) {
			_localKeysCache.Add(key);
		}

		for (int i = 0; i < _localKeysCache.Count; i++) {
			ulong networkId = _localKeysCache[i];
			// 容错：防止因其他逻辑提前从字典中删除了 key
			if (!_localHandhold.TryGetValue(networkId, out var identity)) continue;
			// 移除检测
			if (identity == null || identity.gameObject == null || !identity.gameObject.activeInHierarchy)
				BroadcastRemove(networkId);
			// 变化检测
			else if (identity.HasMeaningfulChange())
				_sweepQueue.Add(identity);
		}

		// 如果本轮有需要更新的生物, 开启冲刷标志
		if (_sweepQueue.Count > 0) _isSweeping = true;
	}

	/// <summary>
	/// 连续分帧打包 单帧最多打包并发送 _maxSyncPerFrame 个敌人
	/// </summary>
	private void FlushNextBatch() {
		_batchBuffer.Clear();

		// 截取当前帧能容纳的上限数据
		while (_sweepIndex < _sweepQueue.Count && _batchBuffer.Count < _maxSyncPerFrame) {
			var identity = _sweepQueue[_sweepIndex];
			_sweepIndex++;

			// 跨帧二次有效性校验 (防止在前几帧冲刷期间生物被彻底 Destroy)
			if (identity != null && identity.gameObject != null && identity.gameObject.activeInHierarchy) {
				_batchBuffer.Add(identity);
			}
		}

		// 真正发包并刷新 RememberState
		if (_batchBuffer.Count > 0) BroadcastUpdate(_batchBuffer);

		// 如果队列已经全部发完, 关闭冲刷, 等待下一个 _syncInterval 触发
		if (_sweepIndex >= _sweepQueue.Count) {
			_isSweeping = false;
			_sweepQueue.Clear();
		}
	}

	/// <summary>
	/// 手持攀爬物拔出状态更新
	/// </summary>
	private void HoldWeaken() {
		float currentTime = Time.time;

		// 倒序 for 循环: 允许直接原地修改元素以及安全执行 RemoveAt
		for (int i = _holdingList.Count - 1; i >= 0; i--) {
			var (identity, lastTime) = _holdingList[i];

			// 物体为空||无手抓握时 直接剔除
			if (identity == null || identity.Handhold == null || _handsField(identity.Handhold)?.Count == 0) {
				_holdingList.RemoveAt(i);
				continue;
			}

			// 计算本次时间差并更新元组
			float deltaTime = currentTime - lastTime;
			_holdingList[i] = (identity, currentTime); // 直接更新 List 中的 ValueTuple

			// 发送衰减同步包
			SendWeaken(identity, deltaTime);
		}
	}

	/// <summary>
	/// 分帧向客户端补发攀爬物表
	/// 接收函数: <see cref="HandleCreateChunk"/>
	/// </summary>
	private IEnumerator SendCreateChunksRoutine(IDType clientId) {
		List<ClimbableData> itemsToSend = new List<ClimbableData>();

		// 收集当前全场记录（优先获取 Handhold 字典中的最新状态）
		foreach (var kvp in _handhold) {
			if (kvp.Value != null && kvp.Value.IsValid) {
				itemsToSend.Add(kvp.Value.data);
			}
		}

		int index = 0;
		while (index < itemsToSend.Count) {
			int count = Mathf.Min(ChunkItemsPerFrame, itemsToSend.Count - index);

			var writer = GetWriter(MPSteamworks.UserSteamId, clientId, PacketType.PitonStateSync);
			writer.Put((byte)PitonSyncAction.CreateChunk);
			writer.Put((ushort)count);

			for (int i = 0; i < count; i++) {
				writer.Put(itemsToSend[index + i]);
			}

			MPSteamworks.Instance.SendToPeer(clientId, writer, SendType.Reliable);

			index += count;
			yield return null; // 分帧避让, 防止瞬间发包造成卡顿
		}

		_sendRoutines.Remove(clientId);
	}

	#endregion

	#region[对象生成和注册]

	/// <summary>
	/// 注册本地可攀爬对象并广播Create
	///
	/// Registers a local climbable object and broadcasts Create
	/// </summary>
	private void RegisterLocalClimbable(GameObject root) {
		var prefabKey = MPUtil.CleanCloneName(root.name);
		if (root == null || string.IsNullOrEmpty(prefabKey)) return;

		var identity = GetOrCreateIdentity(root);
		if (!identity.IsValid) {
			identity.data.networkId = MPUtil.Hash64($"{MPSteamworks.UserSteamId}:{_nextLocalId++}");
			identity.data.ownerId = MPSteamworks.UserSteamId;
			identity.data.prefabKey = prefabKey;
			identity.RememberState();
		}

		_handhold[identity.NetworkId] = identity;
		_localHandhold[identity.NetworkId] = identity;
		_globalPersistentTable[identity.NetworkId] = identity.data;

		BroadcastCreate(identity);
	}

	/// <summary>
	/// 获取可攀爬对象的根节点
	/// 根节点通常是当前Level Root下的直接子对象
	///
	/// Gets the root object for a climbable
	/// The root is usually the direct child below the current level root
	/// </summary>
	private GameObject GetClimbableRoot(GameObject obj) {
		if (obj == null) return null;

		var levelRoot = WorldLoader.initialized ? WorldLoader.GetCurrentLevelParentRoot() : null;
		var current = obj.transform;
		while (current.parent != null && current.parent != levelRoot) {
			current = current.parent;
		}

		return current.gameObject;
	}

	/// <summary>
	/// 注册映射 (由 NetworkedClimable 在 Awake 调用)
	/// </summary>
	public static void RegisterLookup(CL_Handhold handhold, NetworkedClimable identity) {
		if (handhold != null && identity != null) _handholdLookup[handhold] = identity;
	}

	/// <summary>
	/// 注销映射 (由 NetworkedClimable 在 OnDestroy 调用)
	/// </summary>
	public static void UnregisterLookup(CL_Handhold handhold) {
		if (handhold != null) _handholdLookup.Remove(handhold);
	}

	/// <summary>
	/// O(1) 极速查询
	/// </summary>
	public static bool TryGetNetworkIdentity(CL_Handhold handhold, out NetworkedClimable identity) {
		return _handholdLookup.TryGetValue(handhold, out identity);
	}

	#endregion

	#region[网络数据发送]

	/// <summary>
	/// 广播可攀爬物被创建
	/// 接收函数: <see cref="HandleCreate"/>
	/// </summary>
	public void BroadcastCreate(NetworkedClimable identity) {
		if (!MPCore.CanSync || identity == null || !identity.IsValid) return;
		var data = identity.data;
		var writer = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.PitonStateSync);
		writer.Put((byte)PitonSyncAction.Create);
		writer.Put(data);
		MPSteamworks.Instance.Broadcast(writer, SendType.Reliable);
	}

	/// <summary>
	/// 周期性广播Piton状态变化
	/// 只有位置, 旋转, 激活状态或secure状态有明显变化时才发送
	/// 接收函数: <see cref="HandleUpdate"/>
	/// 
	/// Periodically broadcasts piton state changes
	/// Only sends when position, rotation, active state or secure state changed meaningfully
	/// </summary>
	public void BroadcastUpdate(List<NetworkedClimable> batch) {
		if (!IsEnabled || batch == null || batch.Count == 0) return;

		var writer = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.PitonStateSync);
		writer.Put((byte)PitonSyncAction.Update);
		writer.Put((byte)batch.Count);

		for (int i = 0; i < batch.Count; i++) {
			var identity = batch[i];
			identity.RememberState();
			writer.Put(identity.NetworkId);
			writer.Put(identity.transform.position);
			writer.Put(identity.transform.rotation);
			writer.Put(identity.data.secureAmount);
			writer.Put(identity.data.secure);
		}

		MPSteamworks.Instance.Broadcast(writer, SendType.Unreliable | SendType.NoNagle);
	}

	/// <summary>
	/// 广播物品被移除 不参与网络同步 仅本地更新
	/// 接收函数: <see cref="HandleRemove"/>
	/// </summary>
	public void BroadcastRemove(ulong networkId) {
		var writer = MPWriterPool.GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.PitonStateSync);
		writer.Put((byte)PitonSyncAction.Remove);
		writer.Put(networkId);

		MPSteamworks.Instance.Broadcast(writer, SendType.Reliable);

		_handhold.Remove(networkId);
		_localHandhold.Remove(networkId);
	}

	// 转接到实例方法
	public static void CreateBreakObject(GameObject gameObject, CL_Handhold handhold) =>
		Instance?.SendBreakInternal(gameObject, handhold);
	/// <summary>
	/// 岩钉被拔出后 
	/// 非创建者: 创建临时掉落物 向主机通知创建被拔出
	/// 创建者: 广播拔出 创建掉落物
	/// HOOK调用: <see cref="Patch_CL_Handhold_PitonSync.Transpiler"/>
	/// 接收函数: <see cref="HandlePitonBreakRequest"/>
	/// </summary>
	public void SendBreakInternal(GameObject gameObject, CL_Handhold handhold) {
		if (!MPCore.CanSync || ApplyingRemoteState || handhold == null || gameObject == null) return;
		// 广播岩钉拔出
		if (!TryGetNetworkIdentity(handhold, out var identity) || !identity.IsValid) return;

		// 非创建者
		if (!_localHandhold.ContainsKey(identity.NetworkId) && identity.data.ownerId != MPSteamworks.UserSteamId) {
			// 掉落物生成记录
			if (gameObject.TryGetComponent<Item_Object>(out var item_Object)) {
				_breakItemObject.Add((identity.NetworkId, gameObject));
			}
			// 发送拔出请求信息
			var writer = GetWriter(MPSteamworks.UserSteamId, identity.data.ownerId, PacketType.PitonStateSync);
			writer.Put((byte)PitonSyncAction.BreakRequest);
			writer.Put(identity.NetworkId);
			MPSteamworks.Instance.SendToPeer(identity.data.ownerId, writer, SendType.Reliable);

			_globalPersistentTable.Remove(identity.NetworkId);
		} else {
			// 生成同步掉落物
			if (gameObject.TryGetComponent<Item_Object>(out var item_Object))
				DroppedItemManager.SyncAndBroadcast(item_Object);
			BroadcastBreak(identity);
		}
	}

	/// <summary>
	/// 创建者: 广播拔出 创建掉落物
	/// 接收函数: <see cref="HandlePitonBreak"/>
	/// </summary>
	public void BroadcastBreak(NetworkedClimable identity) {
		// 广播拔出信息
		var writer = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.PitonStateSync);
		writer.Put((byte)PitonSyncAction.Break);
		writer.Put(identity.NetworkId);
		MPSteamworks.Instance.Broadcast(writer, SendType.Reliable);
		_handhold.Remove(identity.NetworkId);
		_localHandhold.Remove(identity.NetworkId);
		_globalPersistentTable.Remove(identity.NetworkId);
	}

	/// <summary>
	/// 向创建者发送锤击/加固后的Piton状态
	/// 接收函数: <see cref="HandlePitonHammerIn"/>
	/// Broadcasts piton state after hammering/securing
	/// </summary>
	public void BroadcastHammerIn(CL_Handhold handhold, float amount) {
		if (!MPCore.CanSync || ApplyingRemoteState || handhold == null) return;

		if (!TryGetNetworkIdentity(handhold, out var identity) || !identity.IsValid) return;
		if (_localHandhold.ContainsKey(identity.NetworkId)) return;

		var writer = GetWriter(MPSteamworks.UserSteamId, identity.data.ownerId, PacketType.PitonStateSync);
		writer.Put((byte)PitonSyncAction.HammerIn);
		writer.Put(identity.NetworkId);
		writer.Put(amount);

		MPSteamworks.Instance.SendToPeer(identity.data.ownerId, writer, SendType.Reliable);
	}

	/// <summary>
	/// 客机向主机请求攀爬物表
	/// 接收函数: <see cref="HandleChunkRequest"/>
	/// </summary>
	public void SendChunkRequest() {
		if (MPSteamworks.IsHost || !IsEnabled) return;
		var writer = GetWriter(MPSteamworks.UserSteamId, MPSteamworks.Instance.HostSteamId, PacketType.PitonStateSync);
		writer.Put((byte)PitonSyncAction.CreateChunkRequest);

		MPSteamworks.Instance.SendToHost(writer, SendType.Reliable);
	}

	/// <summary>
	/// 客机告知创建者 岩钉松动
	/// 接收函数: <see cref="HandleWeaken"/>
	/// </summary>
	public void SendWeaken(NetworkedClimable identity, float weakenTime) {
		var writer = GetWriter(MPSteamworks.UserSteamId, identity.data.ownerId, PacketType.PitonStateSync);
		writer.Put((byte)PitonSyncAction.Weaken);
		writer.Put(identity.NetworkId);
		writer.Put(weakenTime);

		MPSteamworks.Instance.SendToPeer(identity.data.ownerId, writer, SendType.Reliable);
	}

	#endregion

	#region[网络数据处理]

	/// <summary>
	/// 生成可攀爬物并应用状态
	/// 发送函数: <see cref="BroadcastCreate"/>
	/// </summary>
	public void HandleCreate(IDType senderId, DataReader reader) {
		var data = reader.Get<ClimbableData>();

		// 已存在则更新状态
		if (_handhold.TryGetValue(data.networkId, out var existing) && existing != null) {
			existing.BindData(data);
			return;
		}

		// 解析预制体并实例化
		var prefab = MPAssetManager.GetHandholdPrefab(data.prefabKey);
		if (prefab == null) {
			MPMain.LogError($"[MP ClimbableSync] Could not resolve prefab '{data.prefabKey}' for {data.networkId}.");
			return;
		}

		var climbableObject = Object.Instantiate(prefab, data.position, data.rotation);

		// 绑定到关卡
		TryAddPlacedObjectToLevel(climbableObject);

		var identity = GetOrCreateIdentity(climbableObject);

		if (!identity.IsValid) identity.BindData(data);

		_handhold[data.networkId] = identity;
		_globalPersistentTable[data.networkId] = data;
	}

	/// <summary>
	/// 物品在生成端的更新数据
	/// 发送函数: <see cref="BroadcastUpdate"/>
	/// </summary>
	public void HandleUpdate(DataReader reader) {
		byte count = reader.GetByte();
		ApplyingRemoteState = true;
		try {
			for (int i = 0; i < count; i++) {
				ulong networkId = reader.GetULong();
				Vector3 position = reader.GetVector3();
				Quaternion rotation = reader.GetQuaternion();
				float secureAmount = reader.GetFloat();
				bool secure = reader.GetBool();

				if (_handhold.TryGetValue(networkId, out var identity) && identity != null)
					ApplyState(identity, position, rotation, secureAmount, secure);
				if (MPSteamworks.IsHost && _globalPersistentTable.TryGetValue(networkId, out var record) && record != null)
					record.BindData(position, rotation, secureAmount, secure);
			}
		} finally {
			ApplyingRemoteState = false;
		}
	}

	/// <summary>
	/// 物品在生成端被关闭 接收端 删除记录 并 停止更新
	/// 发送函数: <see cref="BroadcastRemove"/>
	/// </summary>
	public void HandleRemove(DataReader reader) {
		ulong networkId = reader.GetULong();
		if (_handhold.TryGetValue(networkId, out var identity)) _handhold.Remove(networkId);
	}

	/// <summary>
	/// 应用锤入
	/// 发送函数: <see cref="BroadcastHammerIn"/>
	/// </summary>
	public void HandlePitonHammerIn(DataReader reader) {
		ulong networkId = reader.GetULong();
		float amount = reader.GetFloat();
		if (!_localHandhold.TryGetValue(networkId, out var identity) || identity == null) return;
		ApplyingRemoteState = true;
		try {
			identity.Handhold.HammerIn(amount);
		} finally {
			ApplyingRemoteState = false;
		}
	}

	/// <summary>
	/// 销毁对应岩钉 回收掉落物
	/// 发送函数: <see cref="BroadcastBreak"/>
	/// </summary>
	public void HandlePitonBreak(DataReader reader) {
		ulong networkId = reader.GetULong();
		// 回收非同步掉落物
		for (int i = 0; i < _breakItemObject.Count; ++i) if (_breakItemObject[i].id == networkId) {
			MPMain.LogTest("HandlePitonBreak");
			GameObject.Destroy(_breakItemObject[i].obj);
			_breakItemObject.RemoveAt(i);
			--i;
		}
		if (!_handhold.TryGetValue(networkId, out var identity) || identity == null) return;
		if (identity.gameObject != null) {
			var hands = _handsField(identity.Handhold);
			while (hands?.Count > 0) {
				hands[0].DropHand();
			}
			GameObject.Destroy(identity.gameObject);
		}

		_handhold.Remove(networkId);
		_localHandhold.Remove(networkId);
		_globalPersistentTable.Remove(networkId);
	}

	/// <summary>
	/// 销毁对应岩钉 回收掉落物
	/// 发送函数: <see cref="SendBreakInternal"/>
	/// </summary>
	public void HandlePitonBreakRequest(DataReader reader) {
		ulong networkId = reader.GetULong();
		if (!_localHandhold.TryGetValue(networkId, out var identity) || identity == null) return;
		if (identity.gameObject != null && identity.Handhold != null) {
			var handhold = identity.Handhold;
			// 松手
			var hands = _handsField(handhold);
			while (hands?.Count > 0) {
				hands[0].DropHand();
			}
			// 生成同步掉落物
			var breakObj = Object.Instantiate(handhold.breakObject, handhold.transform.position, handhold.transform.rotation);
			if (breakObj.TryGetComponent<Item_Object>(out var item_Object)) {
				DroppedItemManager.SyncAndBroadcast(item_Object);
			}
			// 广播销毁
			BroadcastBreak(identity);
			// 移除攀爬物
			GameObject.Destroy(identity.gameObject);
		}
		_handhold.Remove(networkId);
		_localHandhold.Remove(networkId);
		_globalPersistentTable.Remove(networkId);
	}

	/// <summary>
	/// 客机接收并批量实例化主机补发的攀爬物 Chunk 包
	/// 发送函数: <see cref="SendCreateChunksRoutine"/>
	/// </summary>
	public void HandleCreateChunk(DataReader reader) {
		ushort count = reader.GetUShort();
		for (int i = 0; i < count; i++) {
			var data = reader.Get<ClimbableData>();

			// 若本地已存在该 ID 的对象, 直接更新状态
			if (_handhold.TryGetValue(data.networkId, out var existing) && existing != null) {
				existing.BindData(data);
				continue;
			}

			// 解析预制体并实例化
			var prefab = MPAssetManager.GetHandholdPrefab(data.prefabKey);
			if (prefab == null) {
				MPMain.LogError($"[MP ClimbableSync] Could not resolve prefab '{data.prefabKey}' for {data.networkId}.");
				continue;
			}

			var climbableObject = Object.Instantiate(prefab, data.position, data.rotation);
			var levelRoot = WorldLoader.GetCurrentLevelParentRoot();
			if (levelRoot != null) climbableObject.transform.SetParent(levelRoot);

			TryAddPlacedObjectToLevel(climbableObject);

			var identity = GetOrCreateIdentity(climbableObject);
			if (!identity.IsValid) identity.BindData(data);

			_handhold[data.networkId] = identity;
			_globalPersistentTable[data.networkId] = data;
		}
	}

	/// <summary>
	/// 处理客机的已生成攀爬物请求 (仅主机执行)
	/// 发送函数: <see cref="SendChunkRequest"/>
	/// </summary>
	public void HandleChunkRequest(IDType senderId) {
		if (!MPCore.CanSync || !MPSteamworks.IsHost || WorldSyncManager.Instance == null) return;
		if (senderId == 0 || senderId == MPSteamworks.UserSteamId) return;

		if (_sendRoutines.TryGetValue(senderId, out var existing) && existing != null)
			WorldSyncManager.Instance.StopCoroutine(existing);
		_sendRoutines[senderId] = WorldSyncManager.Instance.StartCoroutine(SendCreateChunksRoutine(senderId));
	}

	/// <summary>
	/// 接收客机拔松/拉拽通知, 同步降低加固值与产生前向偏移位移
	/// 发送函数: <see cref="SendWeaken"/>
	/// </summary>
	public void HandleWeaken(DataReader reader) {
		ulong networkId = reader.GetULong();
		float weakenTime = reader.GetFloat();

		if (!_localHandhold.TryGetValue(networkId, out var identity) || identity == null) return;

		var handhold = identity.Handhold;
		if (handhold == null || handhold.secure) return; // 已经彻底加固的不可拔松

		ApplyingRemoteState = true;
		try {
			// 扣减加固值 (速率: Time.deltaTime * 0.115f)
			handhold.secureAmount -= weakenTime * 0.115f;

			// 计算位移向量 (速率: Time.fixedDeltaTime * 0.05f)
			float moveDistance = weakenTime * 0.05f;
			Vector3 moveVector = handhold.transform.forward * moveDistance;

			// 应用软父级/Transform位移与 offsetAmount 累加
			var softParent = _softParentField(handhold);
			if (softParent != null) {
				softParent.SoftMove(moveVector);
			} else {
				handhold.transform.position += moveVector;
			}
			_offsetAmountField(handhold) += moveDistance;

			// 同步更新本地 NetworkedClimable 数据缓存
			identity.data.secureAmount = handhold.secureAmount;
			identity.data.position = handhold.transform.position;

			// 若加固值归零/小于0, 触发断裂与拔出广播
			if (handhold.secureAmount < 0f) {
				var hands = _handsField(handhold);
				while (hands?.Count > 0) {
					hands[0].DropHand();
				}
				var breakObj = Object.Instantiate(handhold.breakObject, handhold.transform.position, handhold.transform.rotation);

				Object.Destroy(handhold.gameObject);
			}

		} finally {
			ApplyingRemoteState = false;
		}
	}

	/// <summary>
	/// 处理收到的Piton同步数据包
	/// 根据Action类型执行创建, 更新或删除
	///
	/// Handles an incoming piton sync packet
	/// Applies create, update or remove depending on the action type
	/// </summary>
	public void HandlePitonState(IDType senderId, DataReader reader) {
		var action = (PitonSyncAction)reader.GetByte();
		switch (action) {
			case PitonSyncAction.Create:
				HandleCreate(senderId, reader);
				break;
			case PitonSyncAction.Update:
				HandleUpdate(reader);
				break;
			case PitonSyncAction.Remove:
				HandleRemove(reader);
				break;
			case PitonSyncAction.HammerIn:
				HandlePitonHammerIn(reader);
				break;
			case PitonSyncAction.Weaken:
				HandleWeaken(reader);
				break;
			case PitonSyncAction.BreakRequest:
				HandlePitonBreakRequest(reader);
				break;
			case PitonSyncAction.Break:
				HandlePitonBreak(reader);
				break;
			case PitonSyncAction.CreateChunkRequest:
				HandleChunkRequest(senderId);
				break;
			case PitonSyncAction.CreateChunk:
				HandleCreateChunk(reader);
				break;
		}
	}

	/// <summary>
	/// 将同步状态应用到NetworkedPiton对象
	///
	/// Applies synced state to a NetworkedPiton object
	/// </summary>
	private void ApplyState(NetworkedClimable identity, Vector3 position, Quaternion rotation,
		float secureAmount, bool secure
	) {
		identity.transform.SetPositionAndRotation(position, rotation);

		var handhold = identity.Handhold;
		if (handhold != null) {
			handhold.Initialize();
			handhold.secureAmount = secureAmount;
			handhold.secure = secure;
		}

		identity.data.BindData(position, rotation, secureAmount, secure);
	}

	#endregion

	#region[工具函数]

	/// <summary>
	/// 判断投射物是否由本地玩家发射
	///
	/// Checks whether the projectile was fired by the local player
	/// </summary>
	private bool IsLocalProjectile(Projectile projectile) {
		var localPlayer = ENT_Player.GetPlayer();
		if (projectile == null || localPlayer == null) return false;

		var sourceEntity = _sourceEntityField(projectile);
		return sourceEntity == localPlayer;
	}

	/// <summary>
	/// 将远程生成的可攀爬对象注册到当前关卡的PlacedObject列表
	/// 这样游戏内部系统可以正常识别它 在关卡关闭时关闭攀爬对象
	///
	/// Adds a remotely spawned climbable object to the current level's placed object list
	/// This allows internal game systems to recognize it properly
	/// </summary>
	private void TryAddPlacedObjectToLevel(GameObject climbableObject) {
		if (!WorldLoader.initialized || climbableObject == null) return;

		try {
			var level = WorldLoader.GetClosestLevelToPosition(climbableObject.transform.position).GetLevel();
			if (level != null){
				climbableObject.transform.SetParent(level.GetParentRoot());
				_addPlacedObjectMethod(level, climbableObject);
			}
		} catch (Exception e) {
			MPMain.LogWarning($"[MP ClimbableSync] Could not register remote climbable as placed object: {e.Message}");
		}
	}

	/// <summary>
	/// 获取或创建NetworkedPiton组件
	///
	/// Gets or creates the NetworkedPiton component
	/// </summary>
	private NetworkedClimable GetOrCreateIdentity(GameObject obj) {
		return obj.GetComponent<NetworkedClimable>() ?? obj.AddComponent<NetworkedClimable>();
	}

	#endregion
}