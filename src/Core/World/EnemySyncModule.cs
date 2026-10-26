using Steamworks;
using Steamworks.Data;
using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.VisualScripting;
using UnityEngine;
using WKMPMod.Components;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.NetWork;
using WKMPMod.RemotePlayers;
using WKMPMod.Util;
using static WKMPMod.Data.MPWriterPool;
using static WKMPMod.Util.ReusableCollectionPool;

namespace WKMPMod.World;

#region[优化实体订阅者分组]

/// <summary>
/// 自定义结构体 List<T> 用于作为 Dictionary 的键
/// </summary>
public readonly struct GroupKey<T> : IEquatable<GroupKey<T>> where T : struct {
	private readonly T[] _array;
	private readonly int _length;
	private readonly int _hashCode;

	public GroupKey(T[] array, int length, int hashCode) {
		_array = array;
		_length = length;
		_hashCode = hashCode;
	}

	public ReadOnlySpan<T> Span => new ReadOnlySpan<T>(_array, 0, _length);

	public bool Equals(GroupKey<T> other) {
		if (_length != other._length) return false;
		for (int i = 0; i < _length; i++)
			if (!EqualityComparer<T>.Default.Equals(_array[i], other._array[i])) return false;
		return true;
	}

	public override bool Equals(object obj) => obj is GroupKey<T> other && Equals(other);
	public override int GetHashCode() => _hashCode;
}

/// <summary>
/// 实体订阅者分组管理器
/// </summary>
public class CollectionGroupingManager<K, V> : IDisposable where K : struct, IEquatable<K>, IComparable<K> {
	// 反转后的字典：Key是订阅者组合(Struct), Value是拥有该组合的EntityId集合
	private Dictionary<GroupKey<K>, List<V>> _groupedData;

	// 跟踪记录所有从 ArrayPool 借来的 Key 数组, 防止内存泄漏, 并在 Clear 时统一归还
	private readonly List<K[]> _rentedArrays = new();

	// HashSet 对象池, 用于复用 Value 中的 HashSet<V>
	private readonly Stack<List<V>> _listPool = new();

	#region[生命周期 API]

	// 创建 / 初始化
	public CollectionGroupingManager(int capacity = 32) {
		_groupedData = new Dictionary<GroupKey<K>, List<V>>(capacity);
	}

	// 清空对象与内存 复用底层容器 归还数组和 HashSet 到池中
	public void Clear() {
		// 归还所有 Value 的 HashSet 到对象池
		foreach (var kvp in _groupedData) {
			var set = kvp.Value;
			set.Clear();
			if (_listPool.Count < 1024) // 防止池无限膨胀
				_listPool.Push(set);
		}
		_groupedData.Clear();

		// 归还 Key 内部借用的临时数组到 ArrayPool
		foreach (var arr in _rentedArrays)
			System.Buffers.ArrayPool<K>.Shared.Return(arr);
		_rentedArrays.Clear();
	}

	// 清空内存并析构 (Dispose)
	public void Dispose() {
		Clear();
		_groupedData = null;
		_listPool.Clear();
	}

	#endregion

	#region[循环记录 API]

	/// <summary>
	/// 传入实体 ID 及其订阅者集合
	/// </summary>
	public void Record(V entityId, HashSet<K> subscribers) {
		if (subscribers == null || subscribers.Count == 0) return;
		int count = subscribers.Count;
		// 从 ArrayPool 借一个临时数组来排序和计算哈希, 避免每次 new 数组产生 GC
		K[] tempArr = System.Buffers.ArrayPool<K>.Shared.Rent(count);
		subscribers.CopyTo(tempArr, 0);
		// 确保顺序无关
		Array.Sort(tempArr, 0, count);
		// 计算独立于顺序的哈希码
		int hash = 17;
		for (int i = 0; i < count; i++) {
			hash = hash * 31 + tempArr[i].GetHashCode();
		}
		// 记录该数组, 因为字典的 Key 持有它, 不能立即归还, 必须等到 Clear()
		_rentedArrays.Add(tempArr);
		var key = new GroupKey<K>(tempArr, count, hash);
		// 获取或创建对应的实体 ID 集合
		if (!_groupedData.TryGetValue(key, out var entityIds)) {
			if (_listPool.Count > 0) entityIds = _listPool.Pop();
			else entityIds = new List<V>();
			_groupedData[key] = entityIds;
		}
		entityIds.Add(entityId);
	}

	#endregion

	#region[迭代器 API]

	/// <summary>
	/// 提供无额外分配的结构体迭代器
	/// </summary>
	public Enumerator GetEnumerator() => new Enumerator(_groupedData.GetEnumerator());

	public struct Enumerator : IEnumerator<KeyValuePair<GroupKey<K>, List<V>>> {
		private Dictionary<GroupKey<K>, List<V>>.Enumerator _inner;

		public Enumerator(Dictionary<GroupKey<K>, List<V>>.Enumerator inner) {
			_inner = inner;
		}

		public KeyValuePair<GroupKey<K>, List<V>> Current => _inner.Current;
		object IEnumerator.Current => Current;
		public bool MoveNext() => _inner.MoveNext();
		public void Reset() => ((IEnumerator)_inner).Reset();
		public void Dispose() => _inner.Dispose();
	}

	#endregion

	#region[仿Dic API]

	public int Count => _groupedData.Count;

	/// <summary>
	/// 根据分组 Key 获取对应的实体集合
	/// </summary>
	public bool TryGetValue(GroupKey<K> key, out List<V> valueList) {
		return _groupedData.TryGetValue(key, out valueList);
	}

	#endregion
}

#endregion

#region[枚举 - 敌人同步操作类型]

/// <summary>
/// 敌人同步操作类型 (主机权威模型).
/// SnapshotReset=快照重置, State=状态更新, Remove=移除, DamageRequest=伤害请求.
/// </summary>
public enum EnemySyncAction : byte {

	OwnerRequest = 1,   // 所有权请求: 客机请求主机分配所有权
	OwnerResponse = 2, // 所有权响应: 主机响应/广播实体所有权
	OwnerAbandon = 3,   // 所有权放弃: 所有者广播放弃所有权, 其他客户端可以请求所有权
	SubscriptionRequest = 4, // 订阅请求: 客机向所有者进行订阅请求, 所有者会记录订阅者并广播状态给订阅者
	UnsubscriptionRequest = 5, // 取消订阅请求: 客机向所有者进行取消订阅请求, 所有者会移除订阅者并停止广播状态给该订阅者
	SnapshotRequest = 6,    // 生物同步快照请求: 客机请求主机发送 (生物死亡 生物所有者)
	SnapshotChunk = 7,      // 生物同步快照响应: 主机发送的 (生物死亡 生物所有者) 记录包

	StateBatch = 10,     // 状态更新包: 实体所有者发送同步位置/旋转/生命值
	Damage = 11,         // 伤害广播: 客机广播对实体造成伤害
	Kill = 12,           // 实体死亡: 杀死实体
	Create = 13,        // 生物创建: 暂时不实现
	CustomFeature = 14,   // 自定义模块同步消息
	CustomFeatureReply = 15,    // 自定义模块同步失败消息 (对象不存在/模块不存在)
}

#endregion

/// 实体启用, 注册网络实体 ID, 请求接管或订阅
///		-> 接管尝试或订阅, 检查本地所有权记录
///			-> 没有所有者 向主机发送接管请求
///			-> 已有有效所有者 向所有者发送订阅请求

/// 实体禁用, 注销网络实体 ID, 广播放弃或退订阅
///		-> 是所有者 广播放弃所有权
///		-> 不是所有者 向所有者发送退订阅

/// 接管流程 
/// 主机接收接管请求
///		-> 查询是否已经有所有者
///			-> 没有所有者, 发送者为新所有者, 并广播
///			-> 有所有者, 向发送者发送正确所有者
///				-> 客户端收到新所有者, 更新本地所有权记录
///					-> 新所有者其他人 && 存在实体, 向新所有者发送订阅请求
///					-> 新所有者是自己 && 存在实体, 创建订阅并接管实体

/// 放弃流程
/// 非所有者接收放弃广播 / 订阅SteamMemberLeave
///		-> 没有相应实体, 删除对应所有权记录
///		-> 有相应实体 && 是主机, 默认成为新所有者并广播
///		-> 有相应实体 && 非主机, 进行接管尝试或订阅

/// 订阅流程
/// 接收订阅
///		-> 是所有者, 加入订阅表, (可以向订阅者立刻发送一份数据)
///		-> 有其他所有者, 忽略(可以换成发送所有者并向主机确认)
///		-> 无所有者, 进行缓存记录

/// <summary>
/// Host-authoritative denizen/enemy transform, health and death synchronization.
/// Existing scene enemies are matched by a stable hierarchy id, so clients do not
/// need to instantiate enemy prefabs to stay aligned with the host.
/// 敌人同步管理器 P2P请求并记录 发送坐标变换, 生命值和死亡同步.
/// 现有场景敌人通过稳定的层级结构 ID 匹配, 因此客户端无需实例化敌人预制体即可与主机保持一致.
/// 相关网络组件 <see cref="NetworkedGameEntity"/>
/// </summary>
public class EnemySyncModule : Singleton<EnemySyncModule>, ISyncModule {

	#region[ISyncModule接口实现]

	public string ModuleName => "EnemySync";

	/// <summary>
	/// 是否开启了生物同步
	/// </summary>
	public bool IsEnabled { get; set; }

	public void OnResetMap() {
		ResetState();
	}

	// 没有联机情况 清空死亡生物记录和死亡生物记录发送协程
	public void OnLeave() {
		// 停止残留的异步协程
		_diedEntities.Clear();
		if (WorldSyncManager.Instance != null)
			foreach (var coroutine in _snapshotSyncRoutines.Values)
				if (coroutine != null) WorldSyncManager.Instance.StopCoroutine(coroutine);
		_snapshotSyncRoutines.Clear();
		ResetState();
	}

	public void OnEnd() => OnLeave();

	#endregion

	#region[字段和属性]

	#region[	新玩家记录同步]

	/// <summary>
	/// 已经死亡或移除的生物,用于同步给新加入的玩家 string储存死亡方式
	/// </summary>
	private readonly Dictionary<ulong, string> _diedEntities = new();

	/// <summary>
	/// 实体所有者
	/// </summary>
	private readonly Dictionary<ulong, IDType> _entityOwners = new();

	/// <summary>
	/// 每个客户端对应的快照发送协程. Key=客户端SteamId, Value=协程引用.
	/// </summary>
	private readonly Dictionary<IDType, Coroutine> _snapshotSyncRoutines = new();
	private const int ChunkItemsPerFrame = 20; // 每帧补发死亡记录数量上限

	#endregion

	#region[	同步时数据]

	/// <summary>
	/// 有所有权的实体和订阅者列表
	/// </summary>
	private readonly Dictionary<ulong, HashSet<IDType>> _entitySubscribers = new();

	/// <summary>
	/// 所有已注册的敌人字典. Key=NetworkId, Value=NetworkedEnemy 组件.
	/// </summary>
	public readonly Dictionary<ulong, NetworkedGameEntity> enemies = new();

	/// <summary>
	/// 按实例ID索引的敌人字典, 用于快速查找场景中已存在的敌人.
	/// </summary>
	private readonly Dictionary<int, NetworkedGameEntity> _byInstanceId = new();

	/// <summary>
	/// 记录暂未确认所有权的待处理订阅 (Key: networkId, Value: 客户端SteamId集合)
	/// </summary>
	private readonly Dictionary<ulong, List<IDType>> _pendingSubscriptions = new();

	/// <summary>
	/// 是否正在应用远程状态. 用于防止应用远程数据时再次触发本地广播造成循环.
	/// </summary>
	public bool ApplyingRemoteState { get; private set; }

	#endregion

	#region[	分帧更新控制]

	private float _syncInterval = 0.20f;  // 状态同步间隔 (秒 约5Hz)
	private int _maxScanPerFrame = 50;    // 单帧最多扫描/检查的敌人数量 (扫描上限)
	private int _maxSyncPerFrame = 20;    // 单帧最多打包发送的敌人数量 (发送上限)
	private float _timer = 0f;
	// 扫描控制字段
	private bool _isScanning = false;
	private int _scanIndex = 0;
	// 发送控制字段
	private bool _isSweeping = false;
	private int _groupIndex = 0;              // 当前发送到第几个 GroupKey
	private int _entityIndexWithinGroup = 0;  // 当前 GroupKey 内部发送到了第几个实体

	// 缓存与队列
	private readonly List<NetworkedGameEntity> _batchBuffer;
	private readonly List<ulong> _localKeysCache = new List<ulong>();
	private readonly List<NetworkedGameEntity> _pendingAbandonList = new();
	// 更新广播订阅者分组器
	private readonly CollectionGroupingManager<IDType, NetworkedGameEntity> _groupManager = new();
	private readonly List<GroupKey<IDType>> _activeGroupKeys = new();
	private void ResetSweepState() {
		_timer = 0f;
		_isScanning = false;
		_scanIndex = 0;
		_isSweeping = false;
		_groupIndex = 0;
		_entityIndexWithinGroup = 0;
		_batchBuffer.Clear();
		_localKeysCache.Clear();
		_pendingAbandonList.Clear();
		_groupManager.Clear();
		_activeGroupKeys.Clear();
	}

	#endregion

	#endregion

	#region[生命周期函数]

	private EnemySyncModule() {
		// 重置标志位与配置
		IsEnabled = MPConfig.EnemySync;
		float freq = MPConfig.EnemySendFrequency > 0 ? MPConfig.EnemySendFrequency : 5f;
		_syncInterval = Mathf.Max(0.016f, 1f / freq);
		_maxSyncPerFrame = MPConfig.MaxEnemySendCount;
		_batchBuffer = new(_maxSyncPerFrame);
		// 订阅玩家离开事件
		MPEventBusNet.OnLobbyMemberLeave += HandleOwnerLeave;
	}

	/// <summary>
	/// 场景重置
	/// </summary>
	public void ResetState() {
		// 重置分帧打包状态机
		ResetSweepState();
		// 清空已同步生物字典
		enemies.Clear();
		_entitySubscribers.Clear();
		_byInstanceId.Clear();
		ApplyingRemoteState = false;
		// 广播所有权放弃
		BroadcastOwnerAbandon(networkIds: Array.Empty<ulong>(), abandonAll: true);
	}

	#endregion

	#region[Hook 事件接入 接口]

	/// <summary>
	/// 实体启用时的增量回调 (由 Harmony Patch 调用)
	/// </summary>
	public void OnEntityEnabled(GameEntity entity) {
		if (entity == null || !MPCore.IsReady) return;
		if (!IsSyncableEnemy(entity)) return;

		// 主机与客机均在实体启用时直接建立身份绑定
		EnsureIdentity(entity);
	}

	/// <summary>
	/// 实体禁用/销毁时的增量回调 (由 Harmony Patch 调用)
	/// </summary>
	public void OnEntityDisabled(GameEntity entity) {
		if (entity == null || ApplyingRemoteState) return;
		int instanceId = entity.GetInstanceID();
		if (_byInstanceId.TryGetValue(instanceId, out var identity)) {
			RemoveEnemyRecord(identity);
			AbandonOwnerOrUnsubscribe(identity);
		}
	}

	/// <summary>
	/// 生物死亡时主机额外记录
	/// </summary>
	public void OnEntityKill(GameEntity entity, string type) {
		if (entity == null) return;
		// 存在组件, 进行记录并广播
		if (entity.TryGetComponent<NetworkedGameEntity>(out var identity)
			&& !_diedEntities.ContainsKey(identity.networkId) && identity.IsOwnedByMe) {
			// 进行额外记录并广播
			_diedEntities[identity.networkId] = type;
			BroadcastKill(identity, type);
		}
		// 消除生物记录
		int instanceId = entity.GetInstanceID();
		if (_byInstanceId.TryGetValue(instanceId, out var existingIdentity)) {
			RemoveEnemyRecord(existingIdentity);
			AbandonOwnerOrUnsubscribe(existingIdentity);
		}
	}

	#endregion

	#region[API]

	/// <summary>
	/// 处理接收到的敌人同步数据包: 根据操作类型分发到对应处理方法.
	/// </summary>
	public void HandleEnemyState(IDType senderId, DataReader reader) {
		var action = (EnemySyncAction)reader.GetByte();
		try {
			switch (action) {
				case EnemySyncAction.OwnerRequest:
					HandleOwnerRequest(senderId, reader);
					break;
				case EnemySyncAction.OwnerResponse:
					HandleOwnerResponse(reader);
					break;
				case EnemySyncAction.OwnerAbandon:
					HandleOwnerAbandon(senderId, reader);
					break;
				case EnemySyncAction.SubscriptionRequest:
					HandleSubscriptionRequest(senderId, reader);
					break;
				case EnemySyncAction.UnsubscriptionRequest:
					HandleUnsubscriptionRequest(senderId, reader);
					break;
				case EnemySyncAction.StateBatch:
					HandleState(senderId, reader);
					break;
				case EnemySyncAction.Damage:
					HandleDamage(senderId, reader);
					break;
				case EnemySyncAction.Kill:
					HandleKill(reader);
					break;
				case EnemySyncAction.SnapshotRequest:
					HandleSnapshotRequest(senderId);
					break;
				case EnemySyncAction.SnapshotChunk:
					HandleSnapshotChunk(reader);
					break;
				case EnemySyncAction.CustomFeature:
					HandleCustomFeature(senderId, reader);
					break;
				case EnemySyncAction.CustomFeatureReply:
					HandleCustomFeatureReply(senderId, reader);
					break;
				default:
					MPMain.LogWarning($"[MP EnemySync] Unknown action: {action}");
					break;
			}
		} catch (Exception ex) {
			MPMain.LogError($"[MP EnemySync] Failed to apply {action}: {ex.Message}");
		}
	}

	#endregion

	#region[分帧同步]

	/// <summary>
	/// 由 WorldSyncManager 每帧在 LateUpdate 调用
	/// </summary>
	public void OnSyncUpdate(float deltaTime) {
		if (!MPCore.CanSync || !IsEnabled) {
			ResetSweepState();
			return;
		}

		// 1. 空闲阶段: 累加定时器, 等待下一个同步周期到来
		if (!_isScanning && !_isSweeping) {
			_timer += deltaTime;
			if (_timer >= _syncInterval) {
				_timer = Mathf.Max(0f, _timer - _syncInterval); // 保留余数保证计时精准
				StartNewScanCycle();                            // 触发新一轮扫描
			}
		}

		// 2. 分帧扫描阶段: 每帧最多检查 _maxScanPerFrame 个敌人
		if (_isScanning) StepScan();

		// 进行可能的残留对象清理
		FlushAbandonBuffer();

		// 3. 分帧发送阶段: 扫描完成后, 分帧将数据冲刷给客户端
		if (_isSweeping) FlushNextBatch();
	}

	/// <summary>
	/// 开启新一轮同步, 拍照缓存当前所有敌人的 Key
	/// </summary>
	private void StartNewScanCycle() {
		_scanIndex = 0;
		_localKeysCache.Clear();
		_groupManager.Clear();

		_localKeysCache.AddRange(_entitySubscribers.Where(kvp => kvp.Value != null).Select(kvp => kvp.Key));

		// 如果当前有生物存在, 启动分帧扫描
		if (_localKeysCache.Count > 0) _isScanning = true;
	}

	/// <summary>
	/// 开启新一轮同步, 扫描并收集所有需要同步的敌人
	/// </summary>
	private void StepScan() {
		int scannedThisFrame = 0;

		// 逐个检查, 检查数量达到上限 50 或 遍历完列表 时停下
		while (_scanIndex < _localKeysCache.Count && scannedThisFrame < _maxScanPerFrame) {
			ulong networkId = _localKeysCache[_scanIndex];
			_scanIndex++;
			scannedThisFrame++; // 只要检查了一个敌人, 计数器就 +1

			// 防止因其他逻辑提前从字典中删除了 key
			if (!enemies.TryGetValue(networkId, out var identity)) continue;
			if (identity == null) {
				enemies.Remove(networkId);
				continue;
			}
			// 移除检测
			if (identity.IsRemoved()) {
				RemoveEnemyRecord(identity);
				_pendingAbandonList.Add(identity);
				continue;
			}
			// 有变动 && 有监听者
			else if (identity.HasMeaningfulChange && _entitySubscribers.TryGetValue(identity.networkId, out var subs))
				_groupManager.Record(identity, subs);
		}

		// 检查是否已扫描完全部敌人
		if (_scanIndex >= _localKeysCache.Count) {
			_isScanning = false;
			_localKeysCache.Clear(); // 释放缓存引用

			// 如果有需要同步的敌人, 进入发送冲刷阶段
			_activeGroupKeys.Clear();
			foreach (var kvp in _groupManager)
				if (kvp.Value != null && kvp.Value.Count > 0)
					_activeGroupKeys.Add(kvp.Key);

			if (_activeGroupKeys.Count > 0) {
				_isSweeping = true;
				_groupIndex = 0;
				_entityIndexWithinGroup = 0;
			}
		}
	}

	/// <summary>
	/// 失效对象清理
	/// </summary>
	private void FlushAbandonBuffer() {
		if (_pendingAbandonList.Count > 0) {
			MPMain.LogDebug("FlushAbandonBuffer");
			AbandonOwnerOrUnsubscribe(_pendingAbandonList);
		}
		_pendingAbandonList.Clear();
	}

	/// <summary>
	/// 连续分帧打包 单帧最多打包并发送 _maxSyncPerFrame 个敌人
	/// </summary>
	private void FlushNextBatch() {
		_batchBuffer.Clear();
		int remainingQuota = _maxSyncPerFrame;
		bool hasSentDataThisFrame = false;

		while (_groupIndex < _activeGroupKeys.Count) {
			var currentGroupKey = _activeGroupKeys[_groupIndex];
			if (!_groupManager.TryGetValue(currentGroupKey, out var entityList) || entityList == null || entityList.Count == 0) {
				// 如果组为空或已经处理完, 直接跳到下一组
				_groupIndex++;
				_entityIndexWithinGroup = 0;
				continue;
			}
			// 计算当前组中还剩下多少个未发送的实体
			int remainingInGroup = entityList.Count - _entityIndexWithinGroup;
			// 剩余配额足够装下当前组的所有剩余实体 -> 整组发送
			if (remainingQuota >= remainingInGroup) {
				// 收集组内未发送的实体到 _batchBuffer
				for (int i = _entityIndexWithinGroup; i < entityList.Count; i++) {
					var identity = entityList[i];
					if (identity != null && identity.gameObject != null && identity.gameObject.activeInHierarchy)
						_batchBuffer.Add(identity);
				}
				// 发送当前整组数据
				if (_batchBuffer.Count > 0) {
					BroadcastStateBatch(currentGroupKey, _batchBuffer);
					hasSentDataThisFrame = true;
				}
				// 扣减当前帧剩余配额, 并推进到下一个组
				remainingQuota -= remainingInGroup;
				_groupIndex++;
				_entityIndexWithinGroup = 0;
				_batchBuffer.Clear();
			} else if (hasSentDataThisFrame) {
				// 配额不够, 且本帧已经发送过数据 -> 放弃凑包, 剩余数据留到下一帧发送
				break;
				// 配额不够, 且本帧尚未发送过数据 -> 单组自身超出了 _maxSyncPerFrame, 强行分帧截取
			} else {
				int targetIndex = _entityIndexWithinGroup + remainingQuota;
				for (int i = _entityIndexWithinGroup; i < targetIndex; i++) {
					var identity = entityList[i];
					if (identity != null && identity.gameObject != null && identity.gameObject.activeInHierarchy)
						_batchBuffer.Add(identity);
				}
				// 发送截取的部分数据
				if (_batchBuffer.Count > 0) BroadcastStateBatch(currentGroupKey, _batchBuffer);
				// 游标停留在当前组的未发送位置, 下一帧继续发送当前组
				_entityIndexWithinGroup = targetIndex;
				_batchBuffer.Clear();
				// 本帧配额用尽, 直接退出循环
				break;
			}
		}

		// 如果所有组都已经全部发送完毕, 关闭冲刷, 等待下一个 _syncInterval 触发
		if (_groupIndex >= _activeGroupKeys.Count) {
			_isSweeping = false;
			_activeGroupKeys.Clear();
			_groupManager.Clear();
			_groupIndex = 0;
			_entityIndexWithinGroup = 0;
		}
	}

	/// <summary>
	/// 分帧向客户端补发快照 (生物死亡表 生物所有者表)
	/// 接收函数: <see cref="HandleSnapshotChunk"/>
	/// </summary>
	private IEnumerator SendSnapshotRoutine(IDType clientId) {
		var ownerList = new List<KeyValuePair<ulong, IDType>>(_entityOwners);
		int ownerTotal = ownerList.Count;
		int ownerIndex = 0;

		while (ownerIndex < ownerTotal) {
			int countToSend = Mathf.Min(ChunkItemsPerFrame, ownerTotal - ownerIndex);
			var writer = GetWriter(MPSteamworks.UserSteamId, clientId, PacketType.EnemySync);

			writer.Put((byte)EnemySyncAction.SnapshotChunk);
			writer.Put((byte)(0)); // 标记类型：0 = 所有权映射
			writer.Put(countToSend);

			for (int i = 0; i < countToSend; i++) {
				var item = ownerList[ownerIndex + i];
				writer.Put(item.Key);
				writer.Put(item.Value);
			}

			MPSteamworks.Instance.SendToPeer(clientId, writer, SendType.Reliable);
			ownerIndex += countToSend;
			yield return null;
		}

		var diedList = new List<KeyValuePair<ulong, string>>(_diedEntities);
		int diedTotal = diedList.Count;
		int diedIndex = 0;

		while (diedIndex < diedTotal) {
			int countToSend = Mathf.Min(ChunkItemsPerFrame, diedTotal - diedIndex);
			var writer = GetWriter(MPSteamworks.UserSteamId, clientId, PacketType.EnemySync);

			writer.Put((byte)EnemySyncAction.SnapshotChunk);
			writer.Put((byte)(1)); // 标记类型：1 = 死亡记录
			writer.Put(countToSend);

			for (int i = 0; i < countToSend; i++) {
				var item = diedList[diedIndex + i];
				writer.Put(item.Key);
				writer.Put(item.Value ?? string.Empty);
			}

			MPSteamworks.Instance.SendToPeer(clientId, writer, SendType.Reliable);
			diedIndex += countToSend;
			yield return null;
		}

		_snapshotSyncRoutines.Remove(clientId);
	}

	#endregion

	#region[扫描与生物网络身份]

	/// <summary>
	/// 确保敌人有 NetworkedEnemy 组件
	/// 从缓存查找或创建, 分配稳定 NetworkId.
	/// 如果该生物已经被记录为死亡, 则立即杀死该生物. 并且不会再注册到字典中.
	/// </summary>
	private NetworkedGameEntity EnsureIdentity(GameEntity entity) {
		var syncRoot = entity.transform;
		if (syncRoot == null) return null;
		// 按实例ID缓存查找
		int instanceId = syncRoot.GetInstanceID();
		if (_byInstanceId.TryGetValue(instanceId, out var existing) && existing != null) return existing;
		// 获取或添加 NetworkedEnemy 组件
		var identity = syncRoot.GetComponent<NetworkedGameEntity>() ?? syncRoot.AddComponent<NetworkedGameEntity>();
		// 构建稳定的 NetworkId
		if (identity.networkId == 0) {
			ulong networkId = BuildStableNetworkId(syncRoot);
			// 存在重复ID生物 线性偏移hash
			while (enemies.ContainsKey(networkId)) {
				networkId++;
			}
			identity.Initialize(networkId);
		}
		// 该生物已经被记录 杀死该生物
		if (_diedEntities.TryGetValue(identity.networkId, out var diedType)) {
			if (string.IsNullOrEmpty(diedType)) entity.Kill("diedSync");
			else entity.Kill(diedType);
			entity.health = 0f;
			return identity;
		}
		// 如果有所有者记录, 则设置当前所有者
		if (_entityOwners.TryGetValue(identity.networkId, out var ownerId)) {
			identity.CurrentOwnerId = ownerId;
		}
		// 注册到字典
		enemies[identity.networkId] = identity;
		_byInstanceId[identity.gameObject.GetInstanceID()] = identity;
		// 请求主机分配所有权或订阅状态
		RequestOwnerOrSubscription(identity);
		return identity;
	}

	/// <summary>
	/// 从本地所有字典中注销并移除敌人记录.
	/// </summary>
	private void RemoveEnemyRecord(NetworkedGameEntity identity) {
		if (identity == null) return;
		if (identity.networkId != 0) enemies.Remove(identity.networkId);
		_byInstanceId.Remove(identity.gameObject.GetInstanceID());
	}

	/// <summary>
	/// 构建稳定的 Hash NetworkId: "{层级路径}".
	/// 战役模式使用 BuildNamePath(不会出现重复关卡,不需要排序)
	/// 其他模式使用 BuildFullPath(可能出现重复关卡,需要对象间排序)
	/// </summary>
	private ulong BuildStableNetworkId(Transform transform) {
		return MPUtil.Hash64(MPGameModeManager.IsCampaignMode ? MPUtil.BuildNamePath(transform) : MPUtil.BuildFullPath(transform));
	}

	#endregion

	#region[所有权管理]

	/// <summary>
	/// 设置实体新所有权
	/// </summary>
	/// <returns>
	/// 所有者 ID 无效 || 实体网络 ID 无效 || 本机成为所有者但实体 ID 无效,则返回 false<br/>
	/// 其他情况下返回 true
	/// </returns>
	private bool SetEntityOwner(ulong networkId, IDType newOwnerId) {
		if (networkId == 0 || newOwnerId == 0) return false;
		// 是否为所有者
		bool isLocalOwner = newOwnerId == MPSteamworks.UserSteamId;
		// 没有对应实体
		if (!enemies.TryGetValue(networkId, out var identity) || identity == null) {
			// 是所有者 返回错误并要求调用方放弃所有权
			if (isLocalOwner) return false;
			// 不是所有者 仅记录
			_entityOwners[networkId] = newOwnerId;
			return true;
		}
		// 设置新所有者
		identity.CurrentOwnerId = newOwnerId;
		if (isLocalOwner) {
			// 本机成为所有者, 确保订阅者列表存在
			if (!_entitySubscribers.TryGetValue(networkId, out var subscribers)) {
				subscribers = new HashSet<IDType>();
				_entitySubscribers[networkId] = subscribers;
			}
			// 将缓存写入
			if (_pendingSubscriptions.TryGetValue(networkId, out var pendingSet)) {
				subscribers.AddRange(pendingSet);
				_pendingSubscriptions.Remove(networkId); // 清空暂存
				MPMain.LogTest($"[EnemySync] SetEntityOwner. Processed buffered subscriptions for entity {networkId}.");
				BroadcastFullStateBatch(pendingSet, new List<NetworkedGameEntity> { identity });
			}
		} else {
			// 本机不再是所有者, 清除本机维护的订阅者列表
			_entitySubscribers.Remove(networkId);
		}
		return true;
	}

	/// <summary>
	/// 其他客机接收实体所有者放弃所有权信息
	/// </summary>
	public void ClearEntityOwner(IReadOnlyList<ulong> networkIds) {
		if (networkIds == null || networkIds.Count == 0) return;
		// 重新申请/订阅实体
		var identities = RentList<NetworkedGameEntity>(networkIds.Count);
		foreach (var id in networkIds) {
			_entityOwners.Remove(id);
			if (enemies.TryGetValue(id, out var identity) && identity != null) {
				identity.CurrentOwnerId = 0;
				identities.Add(identity);
			}
		}
		if (identities.Count > 0) RequestOwnerOrSubscription(identities);
		// 归还内存
		ReturnList(identities);
	}

	/// <summary>
	/// 所有者放弃实体所有权
	/// </summary>
	public void AbandonOwnership(IReadOnlyList<NetworkedGameEntity> identities) {
		if (identities == null) return;
		// 所有者放弃实体
		var abandonedIds = RentList<ulong>();
		foreach (var identity in identities) {
			if (identity != null && identity.IsOwnedByMe) {
				identity.CurrentOwnerId = 0;
				abandonedIds.Add(identity.networkId);
				_entityOwners.Remove(identity.networkId);
				_entitySubscribers.Remove(identity.networkId);
				_pendingSubscriptions.Remove(identity.networkId);
			}
		}
		if (abandonedIds.Count > 0) {
			BroadcastOwnerAbandon(abandonedIds);
		}
		// 归还内存
		ReturnList(abandonedIds);
	}

	/// <summary>
	/// 请求所有权或向所有者请求订阅
	/// </summary>
	public void RequestOwnerOrSubscription(NetworkedGameEntity identity) {
		if (identity == null || identity.networkId == 0 || identity.IsOwnedByMe) return;
		// 有所有者 && 所有者在成员列表中, 客机向所有者请求订阅状态
		if (identity.HasOwner && MPSteamworks.ContainsMemberID(identity.CurrentOwnerId)) {
			SendSubscriptionRequest(new[] { identity.networkId }, identity.CurrentOwnerId);
			return;
		}
		// 没有所有者 || 所有者不在成员列表中 分配所有权
		if (MPSteamworks.IsHost) {
			// 主机优先自行分配所有权给自己
			SetEntityOwner(identity.networkId, MPSteamworks.UserSteamId);
			BroadcastOwnerResponse(new[] { identity.networkId }, MPSteamworks.UserSteamId);
			return;
		} else {
			identity.CurrentOwnerId = 0;
			// 客机向主机请求分配所有权
			SendOwnerRequest(new[] { identity.networkId });
			return;
		}
	}
	public void RequestOwnerOrSubscription(IReadOnlyList<NetworkedGameEntity> identities) {
		if (!MPCore.CanSync || !IsEnabled || identities == null || identities.Count == 0) return;
		// 请求所有权
		var ownerRequests = RentList<ulong>();
		// 主机自行分配所有权
		var localClaims = RentList<ulong>();
		// 目标订阅
		var subscriptionsByOwner = RentDictionary<IDType, List<ulong>>();
		foreach (var identity in identities) {
			if (identity == null || identity.networkId == 0 || identity.IsOwnedByMe) continue;
			if (identity.HasOwner && MPSteamworks.ContainsMemberID(identity.CurrentOwnerId)) {
				// 有所有者 && 所有者在成员列表中, 客机向所有者请求订阅状态
				if (!subscriptionsByOwner.TryGetValue(identity.CurrentOwnerId, out var ids))
					subscriptionsByOwner[identity.CurrentOwnerId] = ids = RentList<ulong>();
				ids.Add(identity.networkId);
			} else if (MPSteamworks.IsHost) {
				// 没有所有者 || 所有者不在成员列表中 && 是主机 -> 主机优先自行分配所有权给自己
				if (SetEntityOwner(identity.networkId, MPSteamworks.UserSteamId))
					localClaims.Add(identity.networkId);
			} else {
				// 没有所有者 || 所有者不在成员列表中 && 是客机 -> 客机向主机请求分配所有权
				identity.CurrentOwnerId = 0;
				ownerRequests.Add(identity.networkId);
			}
		}
		if (ownerRequests.Count > 0) SendOwnerRequest(ownerRequests);
		foreach (var pair in subscriptionsByOwner) SendSubscriptionRequest(pair.Value, pair.Key);
		if (localClaims.Count > 0) BroadcastOwnerResponse(localClaims, MPSteamworks.UserSteamId);
		// 归还内存
		foreach (var pair in subscriptionsByOwner) ReturnList(pair.Value);
		ReturnDictionary(subscriptionsByOwner);
		ReturnList(localClaims);
		ReturnList(ownerRequests);
	}

	/// <summary>
	/// 放弃实体所有权或取消订阅
	/// </summary>
	public void AbandonOwnerOrUnsubscribe(NetworkedGameEntity identity) {
		if (identity == null || identity.networkId == 0) return;
		if (identity.IsOwnedByMe) {
			// 如果是所有者, 放弃所有权
			AbandonOwnership(new[] { identity });
		} else if (identity.HasOwner) {
			// 如果是订阅者, 向所有者发送取消订阅请求
			SendUnsubscriptionRequest(new[] { identity.networkId }, identity.CurrentOwnerId);
		}
	}
	public void AbandonOwnerOrUnsubscribe(IReadOnlyList<NetworkedGameEntity> identities) {
		if (identities == null || identities.Count == 0) return;
		// 所有者放弃所有权
		var owned = RentList<NetworkedGameEntity>();
		// 客机退订阅
		var unsubscriptions = RentDictionary<IDType, List<ulong>>();
		foreach (var identity in identities) {
			if (identity == null || identity.networkId == 0) continue;
			// 如果是所有者, 放弃所有权
			if (identity.IsOwnedByMe) owned.Add(identity);
			// 如果是订阅者, 向所有者发送取消订阅请求
			else if (identity.HasOwner) {
				if (!unsubscriptions.TryGetValue(identity.CurrentOwnerId, out var ids))
					unsubscriptions[identity.CurrentOwnerId] = ids = RentList<ulong>();
				ids.Add(identity.networkId);
			}
		}
		if (owned.Count > 0) AbandonOwnership(owned);
		foreach (var pair in unsubscriptions) SendUnsubscriptionRequest(pair.Value, pair.Key);
		// 归还内存
		foreach (var pair in unsubscriptions) ReturnList(pair.Value);
		ReturnDictionary(unsubscriptions);
		ReturnList(owned);
	}

	/// <summary>
	/// 作为所有者接受其他玩家对实体的订阅
	/// </summary>
	private void AddSubscriber(IReadOnlyList<ulong> networkIds, IDType subscriberId) {
		var identityList = RentList<NetworkedGameEntity>();
		foreach (var networkId in networkIds) {
			if (!_entitySubscribers.TryGetValue(networkId, out var subscribers)) {
				subscribers = new HashSet<IDType>();
				_entitySubscribers[networkId] = subscribers;
			}
			if (subscribers.Add(subscriberId)) {
				if (enemies.TryGetValue(networkId, out var identity) && identity != null)
					identityList.Add(identity);
				// TODO 实体不存在时放弃
			}
		}
		// 广播(单发)实体状态
		BroadcastFullStateBatch(new List<IDType> { subscriberId }, identityList);
		ReturnList(identityList);
	}

	/// <summary>
	/// 玩家离线 对其所有实体进行清除
	/// </summary>
	private void HandleOwnerLeave(Friend friend) {
		if (!MPCore.CanSync || !IsEnabled) return;
		// 只清除该发送者拥有的实体
		var abandonedIds = _entityOwners.Where(pair => pair.Value == friend.Id).Select(pair => pair.Key).ToList();
		ClearEntityOwner(abandonedIds);
	}

	#endregion

	#region[网络数据发送]

	/// <summary>
	/// 广播本地敌人受伤通知
	/// 接收函数: <see cref="HandleDamage"/>
	/// </summary>
	public void BroadcastEnemyDamage(GameEntity entity, Damageable.DamageInfo info) {
		if (!MPCore.CanSync || !IsEnabled || ApplyingRemoteState || info == null) return;
		if (!IsSyncableEnemy(entity)) return;
		var identity = EnsureIdentity(entity);
		if (identity == null || identity.networkId == 0) return;
		bool isSourcePlayer = info.sourceEntity != null && info.sourceEntity is ENT_Player;
		// 构建并发送伤害请求数据包
		var writer = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.EnemySync);
		writer.Put((byte)EnemySyncAction.Damage);
		writer.Put(identity.networkId);
		writer.Put(info.amount);
		writer.Put(info.type);
		writer.Put(info.tags);
		writer.Put(info.position);
		writer.Put(info.force);
		writer.Put(isSourcePlayer);
		MPSteamworks.Instance.Broadcast(writer, SendType.Reliable);
	}

	/// <summary>
	/// 所有者广播敌人状态包
	/// 接收函数: <see cref="HandleState"/>
	/// </summary>
	private void BroadcastStateBatch(GroupKey<IDType> subGroup, List<NetworkedGameEntity> batch) {
		if (!IsEnabled || batch == null || batch.Count == 0) return;
		var writer = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.EnemySync);
		writer.Put((byte)EnemySyncAction.StateBatch);
		writer.Put((byte)batch.Count);

		for (int i = 0; i < batch.Count; i++) {
			var identity = batch[i];
			writer.Put(identity.networkId);
			// 记录长度前缀保存的偏移位置
			int lengthOffset = writer.Position;
			// 先写入 2 字节占位符 (ushort)
			writer.Put((ushort)0);
			// 记录数据段起始位置并写入 Entity 变动状态
			int startDataPos = writer.Position;
			identity.WriteSyncState(writer);
			int endDataPos = writer.Position;
			// 计算实际 Payload 长度并原地回填到 lengthOffset
			ushort payloadSize = (ushort)(endDataPos - startDataPos);
			writer.PutUShortAt(lengthOffset, payloadSize);
		}

		ReadOnlySpan<IDType> span = subGroup.Span;
		for (int i = 0; i < span.Length; i++) {
			IDType subscriberId = span[i];
			MPSteamworks.Instance.SendToPeer(subscriberId, writer, SendType.Unreliable | SendType.NoNagle);
		}
	}

	/// <summary>
	/// 所有者广播敌人的全量状态包
	/// 接收函数: <see cref="HandleState"/>
	/// </summary>
	private void BroadcastFullStateBatch(List<IDType> subGroup, List<NetworkedGameEntity> batch) {
		if (!IsEnabled || batch == null || batch.Count == 0) return;
		var writer = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.EnemySync);
		writer.Put((byte)EnemySyncAction.StateBatch);
		writer.Put((byte)batch.Count);

		for (int i = 0; i < batch.Count; i++) {
			var identity = batch[i];
			writer.Put(identity.networkId);
			int lengthOffset = writer.Position;
			writer.Put((ushort)0); // 占位符
			int startDataPos = writer.Position;
			// 调用全量序列化方法，强制写入所有模块
			identity.WriteFullSyncState(writer);
			int endDataPos = writer.Position;
			ushort payloadSize = (ushort)(endDataPos - startDataPos);
			writer.PutUShortAt(lengthOffset, payloadSize);
		}

		foreach (var subscriberId in subGroup) {
			MPSteamworks.Instance.SendToPeer(subscriberId, writer, SendType.Reliable | SendType.NoNagle); // 全量包通常用 Reliable 更安全
		}
	}

	/// <summary>
	/// 广播实体死亡
	/// 接收函数: <see cref="HandleKill"/>
	/// </summary>
	private void BroadcastKill(NetworkedGameEntity identity, string type) {
		if (!MPCore.CanSync || !IsEnabled) return;
		var writer = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.EnemySync);
		writer.Put((byte)EnemySyncAction.Kill);
		writer.Put(identity.networkId);
		writer.Put(type);
		MPSteamworks.Instance.Broadcast(writer, SendType.Reliable);
	}

	/// <summary>
	/// 客机向主机请求生物快照 (生物死亡表 生物所有者表)
	/// 接收函数: <see cref="HandleSnapshotRequest"/>
	/// </summary>
	public void SendSnapshotRequest() {
		if (MPSteamworks.IsHost || !IsEnabled) return;
		var writer = GetWriter(MPSteamworks.UserSteamId, MPSteamworks.Instance.HostSteamId, PacketType.EnemySync);
		writer.Put((byte)EnemySyncAction.SnapshotRequest);

		MPSteamworks.Instance.SendToHost(writer, SendType.Reliable);
	}

	/// <summary>
	/// 客机向主机请求成为实体的新所有者
	/// 接收函数: <see cref="HandleOwnerRequest"/>
	/// </summary>
	public void SendOwnerRequest(IReadOnlyList<ulong> networkIds) {
		if (!MPCore.CanSync || !IsEnabled || MPSteamworks.IsHost) return;
		var ids = networkIds.Where(id => id != 0).Distinct().ToList();
		if (ids.Count == 0) return;
		var writer = GetWriter(MPSteamworks.UserSteamId, MPSteamworks.Instance.HostSteamId, PacketType.EnemySync);
		writer.Put((byte)EnemySyncAction.OwnerRequest);
		writer.Put(ids.Count);
		foreach (var id in ids) writer.Put(id);
		MPSteamworks.Instance.SendToHost(writer, SendType.Reliable);
	}

	/// <summary>
	/// 主机广播实体的新所有者
	/// 接收函数: <see cref="HandleOwnerResponse"/>
	/// </summary>
	public void BroadcastOwnerResponse(IReadOnlyList<ulong> networkIds, IDType ownerId) {
		if (!MPCore.CanSync || !IsEnabled || !MPSteamworks.IsHost || ownerId == 0 || networkIds == null) return;
		var ids = networkIds.Where(id => id != 0).Distinct().ToList();
		if (ids.Count == 0) return;
		var writer = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.EnemySync);
		writer.Put((byte)EnemySyncAction.OwnerResponse);
		writer.Put(ids.Count);
		foreach (ulong id in ids) { writer.Put(id); writer.Put(ownerId); }
		MPSteamworks.Instance.Broadcast(writer, SendType.Reliable);
	}

	/// <summary>
	/// 主机向目标单发实体的新所有者
	/// 接收函数: <see cref="HandleOwnerResponse"/>
	/// </summary>
	public void SendOwnerResponse(IReadOnlyList<ulong> networkIds, IDType ownerId, IDType targetId) {
		if (!MPCore.CanSync || !IsEnabled || !MPSteamworks.IsHost || ownerId == 0 || targetId == 0 || networkIds == null) return;
		var ids = networkIds.Where(id => id != 0).Distinct().ToList();
		if (ids.Count == 0) return;
		var writer = GetWriter(MPSteamworks.UserSteamId, targetId, PacketType.EnemySync);
		writer.Put((byte)EnemySyncAction.OwnerResponse);
		writer.Put(ids.Count);
		foreach (ulong id in ids) { writer.Put(id); writer.Put(ownerId); }
		MPSteamworks.Instance.SendToPeer(targetId, writer, SendType.Reliable);
	}

	/// <summary>
	/// 所有者广播 所有权放弃事件
	/// 接收函数: <see cref="HandleOwnerAbandon"/>
	/// </summary>
	public void BroadcastOwnerAbandon(IReadOnlyList<ulong> networkIds, bool abandonAll = false) {
		if (!MPCore.CanSync || !IsEnabled) return;
		var ids = (networkIds ?? Array.Empty<ulong>()).Where(id => id != 0).Distinct().ToList();
		var writer = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.EnemySync);
		writer.Put((byte)EnemySyncAction.OwnerAbandon);
		writer.Put(abandonAll);
		writer.Put(ids.Count);
		foreach (ulong id in ids) writer.Put(id);
		MPSteamworks.Instance.Broadcast(writer, SendType.Reliable);
	}

	/// <summary>
	/// 客机向所有者申请订阅
	/// 接收函数: <see cref="HandleSubscriptionRequest"/>
	/// </summary>
	public void SendSubscriptionRequest(IReadOnlyList<ulong> networkIds, IDType ownerId) {
		if (ownerId == 0 || networkIds == null) return;
		var ids = networkIds.Where(id => id != 0).Distinct().ToList();
		if (ids.Count == 0) return;
		var writer = GetWriter(MPSteamworks.UserSteamId, ownerId, PacketType.EnemySync);
		writer.Put((byte)EnemySyncAction.SubscriptionRequest);
		writer.Put(ids.Count);
		foreach (ulong id in ids) writer.Put(id);
		MPSteamworks.Instance.SendToPeer(ownerId, writer, SendType.Reliable);
	}

	/// <summary>
	/// 客机向所有者申请退订阅
	/// 接收函数: <see cref="HandleUnsubscriptionRequest"/>
	/// </summary>
	public void SendUnsubscriptionRequest(IReadOnlyList<ulong> networkIds, IDType ownerId) {
		if (ownerId == 0 || networkIds == null) return;
		var ids = networkIds.Where(id => id != 0).Distinct().ToList();
		if (ids.Count == 0) return;
		var writer = GetWriter(MPSteamworks.UserSteamId, ownerId, PacketType.EnemySync);
		writer.Put((byte)EnemySyncAction.UnsubscriptionRequest);
		writer.Put(ids.Count);
		foreach (ulong id in ids) writer.Put(id);
		MPSteamworks.Instance.SendToPeer(ownerId, writer, SendType.Reliable);
	}

	/// <summary>
	/// 单发自定义模块数据
	/// 接收函数: <see cref="HandleCustomFeature"/>
	/// </summary>
	public void SendCustomFeature(
		IDType targetId, ulong networkId, byte featureIndex,
		Action<DataWriter> action, bool needReply = false
	) {
		if (networkId == 0 || action == null) return;
		var writer = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.EnemySync);

		writer.Put((byte)EnemySyncAction.CustomFeature);
		writer.Put(networkId);
		writer.Put(featureIndex);
		writer.Put(needReply);

		action(writer);

		MPSteamworks.Instance.SendToPeer(targetId, writer, SendType.Reliable);
	}

	/// <summary>
	/// 广播自定义模块数据
	/// 接收函数: <see cref="HandleCustomFeature"/>
	/// </summary>
	public void BroadcastCustomFeature(
		ulong networkId, byte featureIndex,Action<DataWriter> action, bool needReply = false
	) {
		if (networkId == 0 || action == null) return;
		if (!_entitySubscribers.TryGetValue(networkId, out var subscribers) || subscribers.Count == 0)
			return;

		var writer = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.EnemySync);

		writer.Put((byte)EnemySyncAction.CustomFeature);
		writer.Put(networkId);
		writer.Put(featureIndex);
		writer.Put(needReply);

		action(writer);

		foreach (var targetId in subscribers)
			MPSteamworks.Instance.SendToPeer(targetId, writer, SendType.Reliable);
	}

	/// <summary>
	/// 回复 自定义模块数据失败 (对象不存在/模块不存在)
	/// 接收函数: <see cref="HandleCustomFeatureReply"/>
	/// </summary>
	private void SendCustomFeatureReply(IDType targetId, ulong networkId, byte featureIndex) {
		var writer = GetWriter(MPSteamworks.UserSteamId, targetId, PacketType.EnemySync);

		writer.Put((byte)EnemySyncAction.CustomFeatureReply);
		writer.Put(networkId);
		writer.Put(featureIndex);

		MPSteamworks.Instance.SendToPeer(targetId, writer, SendType.Reliable);
	}
	#endregion

	#region[网络数据处理]

	/// <summary>
	/// 收到伤害请求: 对指定敌人施加伤害, 更新状态并广播.
	/// 发送函数: <see cref="BroadcastEnemyDamage"/>
	/// </summary>
	private void HandleDamage(IDType senderId, DataReader reader) {
		ulong networkId = reader.GetULong();
		float amount = reader.GetFloat();
		string type = reader.GetString();
		List<string> tags = reader.GetStringList();
		Vector3 position = reader.GetVector3();
		Vector3 force = reader.GetVector3();
		bool isSourcePlayer = reader.GetBool();

		if (!enemies.TryGetValue(networkId, out var identity) || identity == null) return;

		var entity = identity.GetComponent<GameEntity>();
		if (entity == null) return;
		// 构建伤害信息并应用
		GameEntity? sourceEntity = isSourcePlayer
									&& RPManager.Instance.Players.TryGetValue(senderId, out var player)
									&& player.remotePlayer != null
									? player.remotePlayer : null;

		var info = Damageable.DamageInfo.CreateDamageInfo(amount, type, tags, sourceEntity);
		info.position = position;
		info.force = force;
		ApplyingRemoteState = true;
		try {
			entity.Damage(info);
		} finally {
			ApplyingRemoteState = false;
		}
	}

	/// <summary>
	/// 客机接收生物同步数据包
	/// 发送函数: <see cref="BroadcastStateBatch"/>
	/// 发送函数: <see cref="BroadcastFullStateBatch"/>
	/// </summary>
	private void HandleState(IDType senderId, DataReader reader) {
		byte count = reader.GetByte();
		// 退订阅请求列表
		var unsubList = RentList<ulong>();
		ApplyingRemoteState = true;
		try {
			for (int i = 0; i < count; i++) {
				ulong networkId = reader.GetULong();
				ushort payloadSize = reader.GetUShort(); // 读取当前实体的数据包 Payload 字节长度
				if (enemies.TryGetValue(networkId, out var identity) && identity != null) {
					int startPos = reader.Position;
					// 反序列化变动的 Feature 模块
					identity.ReadSyncState(reader);
					// 若 Feature 读写不一致,强制将游标修正到正确位置
					int readBytes = reader.Position - startPos;
					if (readBytes != payloadSize) reader.Position = startPos + payloadSize;
				} else {
					// 客机未找到实体时,跳过该实体的字节数,记录退订阅请求
					reader.Skip(payloadSize);
					unsubList.Add(networkId);
				}
			}
		} finally {
			ApplyingRemoteState = false;
			// 发送退订阅请求
			SendUnsubscriptionRequest(unsubList, senderId);
			ReturnList(unsubList);
		}
	}

	/// <summary>
	/// 客户端收到实体死亡消息: 
	/// 发送函数: <see cref="BroadcastKill"/>
	/// </summary>
	private void HandleKill(DataReader reader) {
		if (MPSteamworks.IsHost) return;

		ulong networkId = reader.GetULong();
		string killType = reader.GetString();
		if (string.IsNullOrEmpty(killType)) killType = "diedSync";

		if (!enemies.TryGetValue(networkId, out var identity) || identity == null) return;

		identity.GetComponent<GameEntity>()?.Kill(killType);
	}

	/// <summary>
	/// 主机收到客机快照请求
	/// 发送函数: <see cref="SendSnapshotRequest"/>
	/// </summary>
	public void HandleSnapshotRequest(IDType senderId) {
		if (!MPCore.CanSync || !IsEnabled || !MPSteamworks.IsHost) return;
		if (senderId == 0 || senderId == MPSteamworks.UserSteamId) return;
		// 停止旧协程
		if (_snapshotSyncRoutines.TryGetValue(senderId, out var existing) && existing != null)
			WorldSyncManager.Instance.StopCoroutine(existing);
		// 启动协程分帧发送
		_snapshotSyncRoutines[senderId] = WorldSyncManager.Instance.StartCoroutine(SendSnapshotRoutine(senderId));
	}

	/// <summary>
	/// 客机获取快照 (生物死亡表 生物所有者表)
	/// 发送函数: <see cref="SendSnapshotRoutine"/>
	/// </summary>
	private void HandleSnapshotChunk(DataReader reader) {
		if (MPSteamworks.IsHost) return;

		byte snapshotType = reader.GetByte(); // 0=所有权映射, 1=死亡记录

		if (snapshotType == 0) {
			int count = reader.GetInt();
			for (int i = 0; i < count; i++) {
				ulong networkId = reader.GetULong();
				IDType ownerId = reader.GetULong();
				_entityOwners[networkId] = ownerId;
				if (enemies.TryGetValue(networkId, out var identity) && identity != null) {
					identity.CurrentOwnerId = ownerId;
					RequestOwnerOrSubscription(identity);
				}
			}

			return;
		} else if (snapshotType == 1) {
			int count = reader.GetInt();
			for (int i = 0; i < count; i++) {
				ulong networkId = reader.GetULong();
				string diedType = reader.GetString();
				_diedEntities[networkId] = diedType;
				// 客机收到死亡记录后, 如果本地有该生物, 立即杀死
				if (enemies.TryGetValue(networkId, out var identity)
					&& identity != null && identity.TryGetComponent<GameEntity>(out var entity)) {
					if (string.IsNullOrEmpty(diedType)) entity.Kill("diedSync");
					else entity.Kill(diedType);
				}
			}
			return;
		}
	}

	/// <summary>
	/// 主机接受客机请求成为实体的新所有者
	/// 发送函数: <see cref="SendOwnerRequest"/>
	/// </summary>
	private void HandleOwnerRequest(IDType senderId, DataReader reader) {
		if (!MPCore.CanSync || !IsEnabled || !MPSteamworks.IsHost) return;
		// 获取申请包
		int count = reader.GetInt();
		if (count <= 0 || count > 4096) return;
		// 确定归该玩家所有 需要广播的队列
		var claimedIds = RentList<ulong>();
		// 不归该玩家所有 需要回信矫正的字典
		var responses = RentDictionary<IDType, List<ulong>>();
		ulong networkId = 0;
		IDType currentOwnerId = 0;
		for (int i = 0; i < count; i++) {
			networkId = reader.GetULong();
			if (networkId == 0) continue;
			if (!_entityOwners.TryGetValue(networkId, out currentOwnerId)
				|| currentOwnerId != MPSteamworks.UserSteamId && !MPSteamworks.ContainsMemberID(currentOwnerId)) {
				// 没有所有者 || 所有者不在成员列表中, 主机直接分配给请求者
				if (SetEntityOwner(networkId, senderId)) {
					claimedIds.Add(networkId);
				} else if (_entityOwners.TryGetValue(networkId, out currentOwnerId)) {
					// 所有者更新设置失败 加入矫正回信
					if (!responses.TryGetValue(currentOwnerId, out var ids))
						responses[currentOwnerId] = ids = RentList<ulong>();
					ids.Add(networkId);
				}
			} else {
				// 已有所有者 加入矫正回信
				if (!responses.TryGetValue(currentOwnerId, out var ids))
					responses[currentOwnerId] = ids = RentList<ulong>();
				ids.Add(networkId);
			}
		}
		// 广播新所有者
		if (claimedIds.Count > 0) BroadcastOwnerResponse(claimedIds, senderId);
		// 纠正错误所有者
		foreach (var pair in responses) SendOwnerResponse(pair.Value, pair.Key, senderId);
		// 归还内存
		foreach (var pair in responses) ReturnList(pair.Value);
		ReturnDictionary(responses);
		ReturnList(claimedIds);
	}

	/// <summary>
	/// 客机接受主机广播实体的新所有者
	/// 发送函数: <see cref="BroadcastOwnerResponse"/>
	/// 发送函数: <see cref="SendOwnerResponse"/>
	/// </summary>
	private void HandleOwnerResponse(DataReader reader) {
		if (!MPCore.CanSync || !IsEnabled) return;
		int count = reader.GetInt();
		if (count <= 0 || count > 4096) return;
		// 所有者失败收集
		var abandonGroups = RentList<ulong>();
		// 订阅收集
		var subscriptionGroups = RentDictionary<IDType, List<ulong>>();
		for (int i = 0; i < count; i++) {
			ulong networkId = reader.GetULong();
			IDType ownerId = reader.GetULong();
			if (networkId == 0 || ownerId == 0) continue;
			if (!SetEntityOwner(networkId, ownerId) && networkId != 0) {
				// 成为所有者失败 进行放弃记录
				MPMain.LogWarning($"[MP EnemySync] Failed to apply owner {ownerId} for entity {networkId}.");
				abandonGroups.Add(networkId);
				continue;
			}
			if (ownerId != MPSteamworks.UserSteamId && enemies.ContainsKey(networkId)) {
				// 不是所有者 && 存在实体 向所有者请求订阅
				if (!subscriptionGroups.TryGetValue(ownerId, out var ids))
					subscriptionGroups[ownerId] = ids = RentList<ulong>();
				ids.Add(networkId);
			}
		}
		// 广播所有者失败 进行放弃的id集合
		BroadcastOwnerAbandon(abandonGroups);
		// 进行批量订阅
		foreach (var pair in subscriptionGroups) SendSubscriptionRequest(pair.Value, pair.Key);
		// 归还内存
		foreach (var pair in subscriptionGroups) ReturnList(pair.Value);
		ReturnDictionary(subscriptionGroups);
		ReturnList(abandonGroups);
	}

	/// <summary>
	/// 客机接收实体所有者广播放弃所有权事件
	/// 发送函数: <see cref="BroadcastOwnerAbandon"/>
	/// </summary>
	private void HandleOwnerAbandon(IDType senderId, DataReader reader) {
		if (!MPCore.CanSync || !IsEnabled) return;
		bool abandonAll = reader.GetBool();
		int count = reader.GetInt();
		if (count < 0 || count > 4096) return;
		// 收集放弃所有权的对象
		var abandonedIds = RentList<ulong>();
		for (int i = 0; i < count; i++) {
			ulong id = reader.GetULong();
			if (id != 0) abandonedIds.Add(id);
		}
		if (abandonAll) {
			// 放弃全部所有权的对象
			foreach (var (networkID, ownerId) in _entityOwners)
				if (ownerId == senderId) abandonedIds.Add(networkID);
		} else {
			// 放弃特定所有权的对象
			foreach (var networkID in abandonedIds)
				if (_entityOwners.TryGetValue(networkID, out var ownerId) && ownerId == senderId)
					abandonedIds.Add(networkID);
		}
		// 批量放弃
		ClearEntityOwner(abandonedIds);
		// 归还内存
		ReturnList(abandonedIds);
	}

	/// <summary>
	/// 客机向实体所有者请求订阅实体更新数据
	/// 发送函数: <see cref="SendSubscriptionRequest"/>
	/// </summary>
	private void HandleSubscriptionRequest(IDType senderId, DataReader reader) {
		if (!MPCore.CanSync || !IsEnabled || senderId == 0) return;
		int count = reader.GetInt();
		if (count <= 0 || count > 4096) return;
		// 订阅处理收集
		var subscriberList = RentList<ulong>();
		for (int i = 0; i < count; i++) {
			ulong networkId = reader.GetULong();
			if (networkId == 0) continue;
			if (_entityOwners.TryGetValue(networkId, out var ownerId)) {
				// 确认是所有者, 直接处理订阅
				if (ownerId == MPSteamworks.UserSteamId) subscriberList.Add(networkId);
				// 存在其他所有者 忽略
				else continue;
			} else if (!_pendingSubscriptions.TryGetValue(networkId, out var pending)) {
				// 还不是所有者, 放入暂存缓冲区 
				_pendingSubscriptions[networkId] = pending = new List<IDType>();
				if (!pending.Contains(senderId)) pending.Add(senderId);
			}
		}
		AddSubscriber(subscriberList, senderId);
	}

	/// <summary>
	/// 客机向实体所有者请求退订阅实体更新数据
	/// 发送函数: <see cref="SendUnsubscriptionRequest"/>
	/// </summary>
	private void HandleUnsubscriptionRequest(IDType senderId, DataReader reader) {
		if (!MPCore.CanSync || !IsEnabled || senderId == 0) return;
		int count = reader.GetInt();
		if (count <= 0 || count > 4096) return;
		for (int i = 0; i < count; i++) {
			ulong networkId = reader.GetULong();
			// 订阅删除
			if (_entitySubscribers.TryGetValue(networkId, out var subscribers))
				subscribers.Remove(senderId);
			// 缓存订阅删除
			if (_pendingSubscriptions.TryGetValue(networkId, out var pending)) {
				pending.Remove(senderId);
				if (pending.Count == 0) _pendingSubscriptions.Remove(networkId);
			}
		}
	}

	/// <summary>
	/// 接收自定义模块的数据
	/// 发送函数: <see cref="SendCustomFeature"/>
	/// 发送函数: <see cref="BroadcastCustomFeature"/>
	/// </summary>
	private void HandleCustomFeature(IDType senderId, DataReader reader) {
		ulong networkId = reader.GetULong();
		byte featureIndex = reader.GetByte();
		bool needReply = reader.GetBool();

		if (!enemies.TryGetValue(networkId, out var identity) || identity == null) {
			if (needReply) SendCustomFeatureReply(senderId, networkId, featureIndex);
			return;
		}

		if (featureIndex >= identity.SyncFeatures.Count
			|| identity.SyncFeatures[featureIndex] is not ICustomSyncFeature feature) {
			if (needReply) SendCustomFeatureReply(senderId, networkId, featureIndex);
			return;
		}
		ApplyingRemoteState = true;
		try {
			feature.HandleCustomMessage(senderId, reader, needReply);
		} finally {
			ApplyingRemoteState = false;
		}
	}

	/// <summary>
	/// 接收自定义模块拒绝数据 (对象不存在/模块不存在)
	/// 发送函数: <see cref="SendCustomFeatureReply"/>
	/// </summary>
	private void HandleCustomFeatureReply(IDType senderId,DataReader reader) {
		ulong networkId = reader.GetULong();
		byte featureIndex = reader.GetByte();

		if (!enemies.TryGetValue(networkId, out var identity) || identity == null) return;
		if (featureIndex >= identity.SyncFeatures.Count) return;
		if (identity.SyncFeatures[featureIndex] is ICustomSyncFeature feature)
			feature.HandleCustomReply(senderId);
	}

	#endregion

	#region[黑名单]

	/// <summary>
	/// 检查 GameEntity 是否为可同步的敌人.
	/// 排除: 玩家, 远程实体, RP容器, 物品物体.
	/// 包含: 带有 "Creature" 标签的物体, 或名称以 "Denizen_"/"DEN_" 开头.
	/// </summary>
	private bool IsSyncableEnemy(GameEntity entity) {
		if (entity == null || entity.gameObject == null) return false;
		// 已经有网络标签
		if (entity.GetComponent<NetworkedGameEntity>() != null) return true;
		// 暂时排除物品(仅CL_Prop) 但有AI生物有CL_Prop
		if (entity.GetComponent<CL_Prop>() != null && entity.GetComponent<AIGameEntity>() == null) return false;
		// 排除道具物体
		if (entity.GetComponent<Item_Object>() != null) return false;
		// 排除玩家相关
		if (entity.GetComponent<ENT_Player>() != null) return false;
		// 排除远程实体 (由其他系统管理)
		if (entity.GetComponent<RemotePlayer>() != null) return false;
		// 排除 RP 容器
		if (entity.GetComponent<RPContainerRef>() != null) return false;
		// 暂时排除 MASS
		if (entity.GetComponent<DEN_DeathFloor>() != null) return false;
		// 暂时排除蟑螂
		if (entity.GetComponent<DEN_Roach>() != null) return false;
		// 居民生物进行同步
		if (entity.GetComponent<Denizen>() != null) return true;
		// 检查 "Creature" 标签
		var tagger = entity.GetComponent<ObjectTagger>();
		if (tagger != null && tagger.tags.Contains(MPKeys.CREATURE_TAGGER)) return true; // CREATURE_TAGGER (生物标签)

		//MPMain.LogTest("Checking syncable enemy: " + entity.name);
		// 检查命名约定
		var rootName = entity.name;
		//MPMain.LogTest("Sync root name: " + rootName);
		return rootName.StartsWith("Denizen_", StringComparison.OrdinalIgnoreCase)  // "Denizen_" 前缀
			|| rootName.StartsWith("DEN_", StringComparison.OrdinalIgnoreCase);     // "DEN_" 前缀
	}

	#endregion
}
