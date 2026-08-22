using Steamworks.Data;
using System;
using System.Collections;
using System.Collections.Generic;
using Unity.VisualScripting;
using UnityEngine;
using WKMPMod.Component;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.NetWork;
using WKMPMod.Util;
using static WKMPMod.Data.MPWriterPool;

namespace WKMPMod.World;

#region[枚举 - 敌人同步操作类型]

/// <summary>
/// 敌人同步操作类型 (主机权威模型).
/// SnapshotReset=快照重置, State=状态更新, Remove=移除, DamageRequest=伤害请求.
/// </summary>
public enum EnemySyncAction : byte {
	StateBatch = 1,     // 状态更新包: 主机发送同步位置/旋转/生命值
	Damage = 2,         // 伤害广播: 客机广播对实体造成伤害
	Kill = 3,           // 实体死亡: 杀死实体
	KillChunkRequest = 5, // 生物死亡记录请求: 客机向主机请求所有的死亡生物
	KillChunk = 6,      // 生物死亡: 主机发送的生物死亡记录包
	Create = 7          // 生物创建: 暂时不实现
}

#endregion

/// <summary>
/// Host-authoritative denizen/enemy transform, health and death synchronization.
/// Existing scene enemies are matched by a stable hierarchy id, so clients do not
/// need to instantiate enemy prefabs to stay aligned with the host.
/// 敌人同步管理器 - 主机权威的敌人 (Denizen/GameEntity) 变换、生命值和死亡同步.
/// 现有场景敌人通过稳定的层级结构 ID 匹配, 因此客户端无需实例化敌人预制体即可与主机保持一致.
/// </summary>
public class EnemySyncModule : Singleton<EnemySyncModule>, ISyncModule{

	#region[ISyncModule接口实现]

	public string ModuleName => "EnemySync";

	/// <summary>
	/// 是否开启了生物同步
	/// </summary>
	public bool IsEnabled { get; set; }

	public void OnReset() {
		ResetState();
	}

	// 没有联机情况 清空死亡生物记录和死亡生物记录发送协程
	public void OnLeave() {
		// 停止残留的异步协程
		_diedEntities.Clear();
		if (WorldSyncManager.Instance != null)
			foreach (var coroutine in _diedEntityRoutines.Values)
				if (coroutine != null) WorldSyncManager.Instance.StopCoroutine(coroutine);

		_diedEntityRoutines.Clear();
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
	/// 每个客户端对应的快照发送协程. Key=客户端SteamId, Value=协程引用.
	/// </summary>
	private readonly Dictionary<IDType, Coroutine> _diedEntityRoutines = new();
	private const int ChunkItemsPerFrame = 10; // 每帧补发死亡记录数量上限

	#endregion

	#region[	同步时数据]

	/// <summary>
	/// 所有已注册的敌人字典. Key=NetworkId, Value=NetworkedEnemy 组件.
	/// </summary>
	private readonly Dictionary<ulong, NetworkedEnemy> _enemies = new();

	/// <summary>
	/// 按实例ID索引的敌人字典, 用于快速查找场景中已存在的敌人.
	/// </summary>
	private readonly Dictionary<int, NetworkedEnemy> _byInstanceId = new();

	/// <summary>
	/// 是否正在应用远程状态. 用于防止应用远程数据时再次触发本地广播造成循环.
	/// </summary>
	public bool ApplyingRemoteState { get; private set; }

	#endregion

	#region[	分帧发送状态机]
	private float _syncInterval = 0.20f; // 状态同步间隔 (秒, 约5Hz)
	private int _maxSyncPerFrame = 10; // 每次最多处理并广播的敌人数量
	private float _timer = 0f;
	private bool _isSweeping = false;
	private int _sweepIndex = 0;

	// 缓存本轮待发送的敌人队列, 避免产生 GC
	private readonly List<NetworkedEnemy> _sweepQueue = new();
	private readonly List<NetworkedEnemy> _batchBuffer;
	private readonly List<ulong> _localKeysCache = new List<ulong>();
	private void ResetSweepState() {
		_timer = 0f;
		_isSweeping = false;
		_sweepIndex = 0;
		_sweepQueue.Clear();
		_batchBuffer.Clear();
		_localKeysCache.Clear();
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
	}

	/// <summary>
	/// 场景重置
	/// </summary>
	public void ResetState() {
		// 重置分帧打包状态机
		ResetSweepState();
		// 清空已同步生物字典
		_enemies.Clear();
		_byInstanceId.Clear();
		ApplyingRemoteState = false;
	}

	#endregion

	#region[API Hook 事件接入]

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
		if (_byInstanceId.TryGetValue(instanceId, out var identity)) 
			RemoveEnemyRecord(identity);
	}

	/// <summary>
	/// 生物死亡时主机额外记录
	/// </summary>
	public void OnEntityKill(GameEntity entity, string type) {
		if (entity == null) return;

		if (entity.TryGetComponent<NetworkedEnemy>(out var identity)
			&& !_diedEntities.ContainsKey(identity.networkId)
			&& MPSteamworks.IsHost) {

			// 进行额外记录并广播
			_diedEntities[identity.networkId] = type;
			BroadcastKill(identity, type);
		}

		// 消除生物记录
		int instanceId = entity.GetInstanceID();
		if (_byInstanceId.TryGetValue(instanceId, out var existingIdentity)) RemoveEnemyRecord(existingIdentity);
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
				case EnemySyncAction.StateBatch:
					HandleState(reader);
					break;
				case EnemySyncAction.Damage:
					HandleDamage(reader);
					break;
				case EnemySyncAction.Kill:
					HandleKill(reader);
					break;
				case EnemySyncAction.KillChunkRequest:
					HandleChunkRequest(senderId);
					break;
				case EnemySyncAction.KillChunk:
					HandleChunk(reader);
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
		if (!MPSteamworks.IsHost || !MPCore.CanSync || !IsEnabled) {
			ResetSweepState();
			return;
		}

		// 处于空闲状态:累加定时器, 等待下一个同步周期到来
		if (!_isSweeping) {
			_timer += deltaTime;
			if (_timer >= _syncInterval) {
				_timer = Mathf.Max(0f, _timer - _syncInterval); // 保留余数, 保证计时精准
				StartNewSweep();          // 开启新一轮的全局扫描
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

		foreach (var key in _enemies.Keys) {
			_localKeysCache.Add(key);
		}

		for (int i = 0; i < _localKeysCache.Count; i++) {
			ulong networkId = _localKeysCache[i];
			// 容错：防止因其他逻辑提前从字典中删除了 key
			if (!_enemies.TryGetValue(networkId, out var identity)) continue;
			// 移除检测
			if (identity.IsRemoved()) RemoveEnemyRecord(identity);
			// 变化检测
			else if (identity.HasMeaningfulChange()) _sweepQueue.Add(identity);
		}

		// 如果本轮有需要更新的生物, 开启冲刷标志
		if (_sweepQueue.Count > 0) _isSweeping = true;
	}

	/// <summary>
	/// 连续分帧打包 单帧最多打包并发送 _maxSyncPerFrame 个敌人
	/// </summary>
	private void FlushNextBatch() {
		MPMain.LogTest("FlushNextBatch");
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
		if (_batchBuffer.Count > 0) BroadcastStateBatch(_batchBuffer);

		// 如果队列已经全部发完, 关闭冲刷, 等待下一个 _syncInterval 触发
		if (_sweepIndex >= _sweepQueue.Count) {
			_isSweeping = false;
			_sweepQueue.Clear();
		}
	}

	/// <summary>
	/// 分帧向客户端补发死亡生物表
	/// 接收函数: <see cref="HandleChunk"/>
	/// </summary>
	private IEnumerator SendDiedEnemiesChunksRoutine(IDType clientId) {
		var list = new List<KeyValuePair<ulong, string>>(_diedEntities);
		int total = list.Count;
		int currentIndex = 0;

		while (currentIndex < total) {
			int countToSend = Mathf.Min(ChunkItemsPerFrame, total - currentIndex);

			var writer = GetWriter(MPSteamworks.UserSteamId, clientId, PacketType.EnemyStateSync);
			writer.Put((byte)EnemySyncAction.KillChunk);
			writer.Put(countToSend);

			for (int i = 0; i < countToSend; i++) {
				var item = list[currentIndex + i];
				writer.Put(item.Key);
				writer.Put(item.Value ?? string.Empty);
			}

			MPSteamworks.Instance.SendToPeer(clientId, writer, SendType.Reliable);
			currentIndex += countToSend;
			yield return null;
		}

		_diedEntityRoutines.Remove(clientId);
	}

	#endregion

	#region[扫描与身份注册]

	/// <summary>
	/// 确保敌人有 NetworkedEnemy 组件: 从缓存查找或创建, 分配稳定 NetworkId.
	/// </summary>
	private NetworkedEnemy EnsureIdentity(GameEntity entity) {
		var syncRoot = entity.transform;
		if (syncRoot == null) return null;

		// 按实例ID缓存查找
		int instanceId = syncRoot.GetInstanceID();
		if (_byInstanceId.TryGetValue(instanceId, out var existing) && existing != null) return existing;

		// 获取或添加 NetworkedEnemy 组件
		var identity = syncRoot.GetComponent<NetworkedEnemy>() ?? syncRoot.AddComponent<NetworkedEnemy>();
		if (identity.networkId == 0) identity.networkId = BuildStableNetworkId(syncRoot);

		// 该生物已经被记录 杀死该生物
		if (_diedEntities.TryGetValue(identity.networkId, out var diedType)) {
			if (string.IsNullOrEmpty(diedType)) entity.Kill("diedSync");
			else entity.Kill(diedType);
			entity.health = 0f;
			return identity;
		}

		// 注册到字典
		_enemies[identity.networkId] = identity;
		_byInstanceId[instanceId] = identity;
		return identity;
	}

	/// <summary>
	/// 从本地所有字典中注销并移除敌人记录.
	/// </summary>
	private void RemoveEnemyRecord(NetworkedEnemy identity) {
		if (identity == null) return;

		if (identity.networkId != 0) _enemies.Remove(identity.networkId);

		int instanceId = identity.transform.GetInstanceID();
		_byInstanceId.Remove(instanceId);
	}

	/// <summary>
	/// 构建稳定的 Hash NetworkId: "{层级路径}".
	/// </summary>
	private ulong BuildStableNetworkId(Transform transform) {
		return MPUtil.Hash64(MPUtil.BuildTransformPath(transform));
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

		// 构建并发送伤害请求数据包
		var writer = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.EnemyStateSync);
		writer.Put((byte)EnemySyncAction.Damage);
		writer.Put(identity.networkId);
		writer.Put(info.amount);
		writer.Put(info.type);
		writer.Put(info.tags);
		writer.Put(info.position);
		MPSteamworks.Instance.Broadcast(writer, SendType.Reliable);
	}

	/// <summary>
	/// 主机广播敌人状态包
	/// 接收函数: <see cref="HandleState"/>
	/// </summary>
	private void BroadcastStateBatch(List<NetworkedEnemy> batch) {
		if (!IsEnabled || batch == null || batch.Count == 0) return;
		MPMain.LogTest("BroadcastStateBatch");

		var writer = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.EnemyStateSync);
		writer.Put((byte)EnemySyncAction.StateBatch);
		writer.Put((byte)batch.Count);

		for (int i = 0; i < batch.Count; i++) {
			var identity = batch[i];
			writer.Put(identity.networkId);
			writer.Put(identity.transform.position);
			writer.Put(identity.transform.rotation);
			writer.Put(identity.currentHealth);
			identity.RememberState();
		}

		MPSteamworks.Instance.Broadcast(writer, SendType.Unreliable | SendType.NoNagle);
	}

	/// <summary>
	/// 广播实体死亡
	/// 接收函数: <see cref="HandleKill"/>
	/// </summary>
	private void BroadcastKill(NetworkedEnemy identity, string type) {
		if (!MPCore.CanSync || !IsEnabled) return;
		var writer = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.EnemyStateSync);
		writer.Put((byte)EnemySyncAction.Kill);
		writer.Put(identity.networkId);
		writer.Put(type);
		MPSteamworks.Instance.Broadcast(writer, SendType.Reliable);
	}

	/// <summary>
	/// 客机向主机请求生物死亡表 (NeedRemoveChunk)
	/// 接收函数: <see cref="HandleChunkRequest"/>
	/// </summary>
	private void SendKillChunkRequest() {
		if (MPSteamworks.IsHost || !IsEnabled) return;
		var writer = GetWriter(MPSteamworks.UserSteamId, MPSteamworks.Instance.HostSteamId, PacketType.EnemyStateSync);
		writer.Put((byte)EnemySyncAction.KillChunkRequest);

		MPSteamworks.Instance.SendToHost(writer, SendType.Reliable);
	}

	#endregion

	#region[网络数据处理]

	/// <summary>
	/// 收到伤害请求: 对指定敌人施加伤害, 更新状态并广播.
	/// 发送函数: <see cref="BroadcastEnemyDamage"/>
	/// </summary>
	private void HandleDamage(DataReader reader) {
		ulong networkId = reader.GetULong();
		float amount = reader.GetFloat();
		string type = reader.GetString();
		List<string> tags = reader.GetStringList();
		Vector3 position = reader.GetVector3();

		if (!_enemies.TryGetValue(networkId, out var identity) || identity == null) return;

		var entity = identity.GetComponent<GameEntity>();
		if (entity == null) return;

		// 构建伤害信息并应用
		var info = Damageable.DamageInfo.CreateDamageInfo(amount, type, tags);
		info.position = position;

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
	/// </summary>
	private void HandleState(DataReader reader) {
		if (MPSteamworks.IsHost) return;

		byte count = reader.GetByte();
		ApplyingRemoteState = true;
		try {
			for (int i = 0; i < count; i++) {
				ulong networkId = reader.GetULong();
				Vector3 position = reader.GetVector3();
				Quaternion rotation = reader.GetQuaternion();
				float health = reader.GetFloat();

				if (_enemies.TryGetValue(networkId, out var identity) && identity != null)
					identity.ApplyRemoteState(position, rotation, health);
			}
		} finally {
			ApplyingRemoteState = false;
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

		if (!_enemies.TryGetValue(networkId, out var identity) || identity == null) return;

		identity.GetComponent<GameEntity>()?.Kill(killType);
	}

	/// <summary>
	/// 主机收到客机生物死亡表请求
	/// 发送函数: <see cref="SendKillChunkRequest"/>
	/// </summary>
	public void HandleChunkRequest(IDType senderId) {
		if (!MPCore.CanSync || !MPSteamworks.IsHost || WorldSyncManager.Instance == null || !IsEnabled) return;
		if (senderId == 0 || senderId == MPSteamworks.UserSteamId) return;
		// 停止旧协程
		if (_diedEntityRoutines.TryGetValue(senderId, out var existing) && existing != null)
			WorldSyncManager.Instance.StopCoroutine(existing);
		// 启动协程分帧发送
		_diedEntityRoutines[senderId] = WorldSyncManager.Instance.StartCoroutine(SendDiedEnemiesChunksRoutine(senderId));
	}

	/// <summary>
	/// 客机获取生物死亡记录表
	/// 发送函数: <see cref="SendDiedEnemiesChunksRoutine"/>
	/// </summary>
	private void HandleChunk(DataReader reader) {
		if (MPSteamworks.IsHost) return;

		int count = reader.GetInt();

		for (int i = 0; i < count; i++) {
			ulong networkId = reader.GetULong();
			string diedType = reader.GetString();

			_diedEntities[networkId] = diedType;

			if (_enemies.TryGetValue(networkId, out var identity)
				&& identity != null
				&& identity.TryGetComponent<GameEntity>(out var entity)) {

				if (string.IsNullOrEmpty(diedType)) entity.Kill("diedSync");
				else entity.Kill(diedType);
			}
		}
	}

	#endregion

	#region[黑名单]

	/// <summary>
	/// 检查 GameEntity 是否为可同步的敌人.
	/// 排除: 玩家、远程实体、RP容器、物品物体.
	/// 包含: 带有 "Creature" 标签的物体, 或名称以 "Denizen_"/"DEN_" 开头.
	/// </summary>
	private bool IsSyncableEnemy(GameEntity entity) {
		if (entity == null || entity.gameObject == null) return false;

		// 暂时排除物品(仅CL_Prop) 但有AI生物有CL_Prop
		if (entity.GetComponent<CL_Prop>() != null && entity.GetComponent<AIGameEntity>() == null) return false;
		// 排除道具物体
		if (entity.GetComponent<Item_Object>() != null) return false;
		// 排除玩家相关
		if (entity.GetComponent<ENT_Player>() != null) return false;
		// 排除远程实体 (由其他系统管理)
		if (entity.GetComponent<RemoteEntity>() != null) return false;
		// 排除 RP 容器
		if (entity.GetComponent<RPContainerRef>() != null) return false;
		// 暂时排除 MASS
		if (entity.GetComponent<DEN_DeathFloor>() != null) return false;
		// 暂时排除蟑螂
		if (entity.GetComponent<DEN_Roach>() != null) return false;

		// 检查 "Creature" 标签
		var tagger = entity.GetComponent<ObjectTagger>();
		if (tagger != null && tagger.tags.Contains(MPKeys.CREATURE_TAGGER)) return true; // CREATURE_TAGGER (生物标签)

		MPMain.LogTest("Checking syncable enemy: " + entity.name);
		// 检查命名约定
		var rootName = entity.name;
		MPMain.LogTest("Sync root name: " + rootName);
		return rootName.StartsWith("Denizen_", StringComparison.OrdinalIgnoreCase)  // "Denizen_" 前缀
			|| rootName.StartsWith("DEN_", StringComparison.OrdinalIgnoreCase);     // "DEN_" 前缀
	}

	#endregion
}
