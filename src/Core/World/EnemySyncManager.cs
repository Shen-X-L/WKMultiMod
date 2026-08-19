using HarmonyLib;
using Steamworks.Data;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using WKMPMod.Component;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.NetWork;
using WKMPMod.Util;
using static UT_Damage;
using static WKMPMod.Data.MPWriterPool;
using Object = UnityEngine.Object;

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
public static class EnemySyncManager {
	#region[常量]

	private const int ChunkItemsPerFrame = 10; // 每帧补发死亡记录数量上限
	private static float _syncInterval = 0.20f; // 状态同步间隔 (秒, 约5Hz)
	private static int _maxSyncPerFrame = 10; // 每次最多处理并广播的敌人数量
	private const float PositionEpsilonSqr = 0.01f; // 位置变化阈值平方 (0.1m)
	private const float RotationEpsilonDegrees = 1.0f; // 旋转变化阈值 (度)
	private const float HealthEpsilon = 0.01f; // 生命值变化阈值
	private static readonly AccessTools.FieldRef<GameEntity, float> _healthField =
		AccessTools.FieldRefAccess<GameEntity, float>("health");
	private static WaitForSecondsRealtime _waitSyncInterval;

	#endregion

	#region[静态字段]

	/// <summary>
	/// 所有已注册的敌人字典. Key=NetworkId, Value=NetworkedEnemy 组件.
	/// </summary>
	private static readonly Dictionary<ulong, NetworkedEnemy> _enemies = new();

	/// <summary>
	/// 已经死亡或移除的生物,用于同步给新加入的玩家 string储存死亡方式
	/// </summary>
	private static readonly Dictionary<ulong, string> _diedEntities = new();

	/// <summary>
	/// 每个客户端对应的快照发送协程. Key=客户端SteamId, Value=协程引用.
	/// </summary>
	private static readonly Dictionary<IDType, Coroutine> _diedEntityRoutines = new();

	/// <summary>
	/// 按实例ID索引的敌人字典, 用于快速查找场景中已存在的敌人.
	/// </summary>
	private static readonly Dictionary<int, NetworkedEnemy> _byInstanceId = new();

	private static Coroutine _syncRoutine = null; // 主同步协程引用

	/// <summary>
	/// 是否正在应用远程状态. 用于防止应用远程数据时再次触发本地广播造成循环.
	/// </summary>
	public static bool ApplyingRemoteState { get; private set; }

	/// <summary>
	/// 是否开启了生物同步
	/// </summary>
	public static bool IsEnemySync;

	#endregion

	#region[生命周期函数]

	static EnemySyncManager() {
		// 订阅场景切换
		SceneManager.sceneLoaded += OnSceneLoaded;
	}

	#endregion

	#region[初始化与重置]

	public static void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
		ResetState();
		if (MPCore.Instance == null) return;
		if (MPCore.IsInLobby && MPCore.IsInitialized)
			_syncRoutine = MPCore.Instance.StartCoroutine(WorldRoutine());
	}

	/// <summary>
	/// 完全重置敌人同步状态: 停止所有协程, 清空字典, 重置标志.
	/// </summary>
	public static void ResetState() {
		_syncInterval = 1 / MPConfig.EnemySendFrequency;
		_maxSyncPerFrame = MPConfig.MaxEnemySendCount;
		_waitSyncInterval = new WaitForSecondsRealtime(_syncInterval);

		// 没有联机情况 清空死亡生物记录和死亡生物记录发送协程
		if (!MPCore.IsInLobby && !MPCore.IsInitialized) {
			_diedEntities.Clear();
			if (MPCore.Instance != null)
				foreach (var coroutine in _diedEntityRoutines.Values)
					if (coroutine != null) MPCore.Instance.StopCoroutine(coroutine);

			_diedEntityRoutines.Clear();
		}

		if (_syncRoutine != null && MPCore.Instance != null)
			MPCore.Instance.StopCoroutine(_syncRoutine);

		_syncRoutine = null;
		_enemies.Clear();
		_byInstanceId.Clear();
		ApplyingRemoteState = false;
	}

	#endregion

	#region[GameEntity Hook 事件接入]

	/// <summary>
	/// 实体启用时的增量回调 (由 Harmony Patch 调用)
	/// </summary>
	public static void OnEntityEnabled(GameEntity entity) {
		if (entity == null || !MPCore.CanSync) return;
		if (!IsSyncableEnemy(entity)) return;

		// 主机与客机均在实体启用时直接建立身份绑定
		EnsureIdentity(entity);
	}

	/// <summary>
	/// 实体禁用/销毁时的增量回调 (由 Harmony Patch 调用)
	/// </summary>
	public static void OnEntityDisabled(GameEntity entity) {
		if (entity == null || ApplyingRemoteState) return;

		int instanceId = entity.GetInstanceID();
		if (_byInstanceId.TryGetValue(instanceId, out var identity)) {
			RemoveEnemyRecord(identity);
		}
	}

	public static void OnEntityKill(GameEntity entity, string type) {
		if (entity == null) return;

		if (entity.TryGetComponent<NetworkedEnemy>(out var identity)
			&& !_diedEntities.ContainsKey(identity.NetworkId)
			&& MPSteamworks.Instance.IsHost) {

			// 进行额外记录并广播
			_diedEntities[identity.NetworkId] = type;
			BroadcastKill(identity, type);
		}

		// 消除生物记录
		int instanceId = entity.GetInstanceID();
		if (_byInstanceId.TryGetValue(instanceId, out var existingIdentity)) RemoveEnemyRecord(existingIdentity);
	}
	#endregion

	#region[API]

	/// <summary>
	/// 向指定客户端发送敌人死亡表. 仅主机可调用.
	/// </summary>
	public static void SendDiedEnemiesToClient(IDType clientId) {
		if (!MPCore.CanSync || !MPSteamworks.Instance.IsHost || MPCore.Instance == null) return;
		if (clientId == 0 || clientId == MPSteamworks.UserSteamId) return;
		// 停止旧协程
		if (_diedEntityRoutines.TryGetValue(clientId, out var existing) && existing != null)
			MPCore.Instance.StopCoroutine(existing);
		// 启动协程分帧发送
		_diedEntityRoutines[clientId] = MPCore.Instance.StartCoroutine(SendDiedEnemiesChunksRoutine(clientId));
	}

	/// <summary>
	/// 处理接收到的敌人同步数据包: 根据操作类型分发到对应处理方法.
	/// </summary>
	public static void HandleEnemyState(IDType senderId, DataReader reader) {
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
					HandleChunkRequest(senderId, reader);
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

	#region[协程与分帧同步]

	/// <summary>
	/// 分帧生物状态广播.
	/// </summary>
	private static IEnumerator WorldRoutine() {
		yield return new WaitUntil(() => WorldLoader.isLoaded && WorldLoader.initialized);
		yield return null;
		yield return null;

		// 复用缓存列表，避免每轮循环产生 GC 垃圾
		var activeSnapshot = new List<NetworkedEnemy>();
		// 数据打包发送
		var pendingStateBatch = new List<NetworkedEnemy>(_maxSyncPerFrame);

		while (MPCore.IsInLobby && MPCore.IsInitialized) {
			if (!MPSteamworks.Instance.IsHost || !MPCore.CanSync) {
				yield return _waitSyncInterval;
				continue;
			}
			// 获取当前存活敌人快照
			activeSnapshot.Clear();
			activeSnapshot.AddRange(_enemies.Values);
			pendingStateBatch.Clear();

			foreach (var identity in activeSnapshot) {
				if (identity == null || identity.gameObject == null) continue;

				// 生物移除检测
				// 是否有变化记录
				if (IsRemoved(identity)) {
					RemoveEnemyRecord(identity);
				} else if (HasMeaningfulChange(identity)) {
					pendingStateBatch.Add(identity);
				}

				// 分帧更新控制 处理满 MaxSyncPerFrame 个敌人则让出当帧
				// 数据打包发送
				if (pendingStateBatch.Count >= _maxSyncPerFrame) {
					BroadcastStateBatch(pendingStateBatch);
					pendingStateBatch.Clear();
					yield return null;
				}
			}

			// 最后的残留打包发送
			if (pendingStateBatch.Count > 0) {
				BroadcastStateBatch(pendingStateBatch);
				pendingStateBatch.Clear();
			}
			yield return _waitSyncInterval;
		}
	}

	/// <summary>
	/// 分帧向客户端补发死亡生物表
	/// 接收函数: <see cref="HandleChunk"/>
	/// </summary>
	private static IEnumerator SendDiedEnemiesChunksRoutine(IDType clientId) {
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
	private static NetworkedEnemy EnsureIdentity(GameEntity entity) {
		var syncRoot = entity.transform;
		if (syncRoot == null) return null;

		// 按实例ID缓存查找
		int instanceId = syncRoot.GetInstanceID();
		if (_byInstanceId.TryGetValue(instanceId, out var existing) && existing != null) return existing;

		// 获取或添加 NetworkedEnemy 组件
		var identity = syncRoot.GetComponent<NetworkedEnemy>() ?? syncRoot.AddComponent<NetworkedEnemy>();
		if (identity.NetworkId == 0) identity.NetworkId = BuildStableNetworkId(syncRoot);

		// 该生物已经被记录 杀死该生物
		if (_diedEntities.TryGetValue(identity.NetworkId, out var diedType)) {
			if (string.IsNullOrEmpty(diedType)) entity.Kill("diedSync");
			else entity.Kill(diedType);
			entity.health = 0f;
			return identity;
		}

		// 注册到字典
		_enemies[identity.NetworkId] = identity;
		_byInstanceId[instanceId] = identity;
		RememberState(identity);
		return identity;
	}

	/// <summary>
	/// 从本地所有字典中注销并移除敌人记录.
	/// </summary>
	private static void RemoveEnemyRecord(NetworkedEnemy identity) {
		if (identity == null) return;

		if (identity.NetworkId != 0) _enemies.Remove(identity.NetworkId);

		int instanceId = identity.transform.GetInstanceID();
		_byInstanceId.Remove(instanceId);
	}

	/// <summary>
	/// 构建稳定的 Hash NetworkId: "{层级路径}".
	/// </summary>
	private static ulong BuildStableNetworkId(Transform transform) {
		return MPUtil.Hash64(MPUtil.BuildTransformPath(transform));
	}

	#endregion

	#region[状态变化检测]

	/// <summary>
	/// 检查敌人状态是否有足够明显的变化需要同步.
	/// 比较: 位置距离、旋转角度、生命值差异.
	/// </summary>
	private static bool HasMeaningfulChange(NetworkedEnemy identity) {
		var transform = identity.transform;

		// 位置变化检查
		if ((transform.position - identity.LastPosition).sqrMagnitude > PositionEpsilonSqr) return true;

		// 旋转变化检查
		if (Quaternion.Angle(transform.rotation, identity.LastRotation) > RotationEpsilonDegrees) return true;

		// 生命值变化检查
		float health = identity.TryGetComponent<GameEntity>(out GameEntity entity) ? _healthField(entity) : float.NaN;

		if (float.IsNaN(health) != float.IsNaN(identity.LastHealth)) return true;
		if (!float.IsNaN(health) && Mathf.Abs(health - identity.LastHealth) > HealthEpsilon) return true;

		return false;
	}

	/// <summary>
	/// 记录当前状态为上次同步状态: 保存位置、旋转、生命值和移除状态.
	/// </summary>
	private static void RememberState(NetworkedEnemy identity) {
		identity.LastPosition = identity.transform.position;
		identity.LastRotation = identity.transform.rotation;
		identity.LastHealth = identity.TryGetComponent<GameEntity>(out GameEntity entity) ? _healthField(entity) : float.NaN;
		identity.LastRemoved = IsRemoved(identity);
	}

	/// <summary>
	/// 检查敌人是否已移除: GameObject为null、未激活
	/// </summary>
	private static bool IsRemoved(NetworkedEnemy identity) {
		if (identity == null || identity.gameObject == null) return true;
		if (!identity.gameObject.activeInHierarchy) return true;

		float health = identity.TryGetComponent<GameEntity>(out GameEntity entity) ? _healthField(entity) : float.NaN;
		return !float.IsNaN(health) && health <= 0f;
	}

	#endregion

	#region[网络数据发送]

	/// <summary>
	/// 广播本地敌人受伤通知
	/// 接收函数: <see cref="HandleDamage"/>
	/// </summary>
	public static void BroadcastEnemyDamage(GameEntity entity, Damageable.DamageInfo info) {
		if (ApplyingRemoteState || entity == null || info == null || !MPCore.CanSync) return;
		if (!IsSyncableEnemy(entity)) return;
		var identity = EnsureIdentity(entity);
		if (identity == null || identity.NetworkId == 0) return;

		// 构建并发送伤害请求数据包
		var writer = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.EnemyStateSync);
		writer.Put((byte)EnemySyncAction.Damage);
		writer.Put(identity.NetworkId);
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
	private static void BroadcastStateBatch(List<NetworkedEnemy> batch) {
		if (batch == null || batch.Count == 0) return;

		var writer = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.EnemyStateSync);
		writer.Put((byte)EnemySyncAction.StateBatch);
		writer.Put((byte)batch.Count);

		for (int i = 0; i < batch.Count; i++) {
			var identity = batch[i];
			writer.Put(identity.NetworkId);
			writer.Put(identity.transform.position);
			writer.Put(identity.transform.rotation);
			writer.Put(identity.TryGetComponent<GameEntity>(out var entity) ? _healthField(entity) : float.NaN);
			RememberState(identity);
		}

		MPSteamworks.Instance.Broadcast(writer, SendType.Unreliable | SendType.NoNagle);
	}

	/// <summary>
	/// 广播实体死亡
	/// 接收函数: <see cref="HandleKill"/>
	/// </summary>
	private static void BroadcastKill(NetworkedEnemy identity, string type) {
		var writer = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.EnemyStateSync);
		writer.Put((byte)EnemySyncAction.Kill);
		writer.Put(identity.NetworkId);
		writer.Put(type);
		MPSteamworks.Instance.Broadcast(writer, SendType.Reliable);
	}

	/// <summary>
	/// 客机向主机请求生物死亡表 (NeedRemoveChunk)
	/// 接收函数: <see cref="HandleChunkRequest"/>
	/// </summary>
	private static void SendKillChunkRequest() {
		if (MPSteamworks.Instance.IsHost) return;
		var writer = GetWriter(MPSteamworks.UserSteamId, MPSteamworks.Instance.HostSteamId, PacketType.ItemStateSync);
		writer.Put((byte)EnemySyncAction.KillChunkRequest);

		MPSteamworks.Instance.SendToHost(writer, SendType.Reliable);
	}

	#endregion

	#region[网络数据处理]

	/// <summary>
	/// 收到伤害请求: 对指定敌人施加伤害, 更新状态并广播.
	/// 发送函数: <see cref="BroadcastEnemyDamage"/>
	/// </summary>
	private static void HandleDamage(DataReader reader) {
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
	private static void HandleState(DataReader reader) {
		if (MPSteamworks.Instance.IsHost) return;

		byte count = reader.GetByte();
		ApplyingRemoteState = true;
		try {
			for (int i = 0; i < count; i++) {
				ulong networkId = reader.GetULong();
				Vector3 position = reader.GetVector3();
				Quaternion rotation = reader.GetQuaternion();
				float health = reader.GetFloat();

				if (_enemies.TryGetValue(networkId, out var identity) && identity != null) {
					identity.transform.SetPositionAndRotation(position, rotation);
					if (identity.TryGetComponent<GameEntity>(out var entity) && !float.IsNaN(health)) {
						_healthField(entity) = health;
					}
					if (!identity.gameObject.activeSelf) identity.gameObject.SetActive(true);
					RememberState(identity);
				}
			}
		} finally {
			ApplyingRemoteState = false;
		}
	}

	/// <summary>
	/// 客户端收到实体死亡消息: 
	/// 发送函数: <see cref="BroadcastKill"/>
	/// </summary>
	private static void HandleKill(DataReader reader) {
		if (MPSteamworks.Instance.IsHost) return;

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
	private static void HandleChunkRequest(IDType senderId, DataReader reader) {
		if (!MPSteamworks.Instance.IsHost) return;
		SendDiedEnemiesToClient(senderId);
	}

	/// <summary>
	/// 客机获取生物死亡记录表
	/// 发送函数: <see cref="SendDiedEnemiesChunksRoutine"/>
	/// </summary>
	private static void HandleChunk(DataReader reader) {
		if (MPSteamworks.Instance.IsHost) return;

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
	private static bool IsSyncableEnemy(GameEntity entity) {
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
