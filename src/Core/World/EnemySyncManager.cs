using HarmonyLib;
using Steamworks.Data;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UIElements;
using WKMPMod.Component;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.NetWork;
using WKMPMod.Util;
using static WKMPMod.Data.MPWriterPool;
using Object = UnityEngine.Object;

namespace WKMPMod.World;

#region[枚举 - 敌人同步操作类型]

/// <summary>
/// 敌人同步操作类型 (主机权威模型).
/// SnapshotReset=快照重置, State=状态更新, Remove=移除, DamageRequest=伤害请求.
/// </summary>
public enum EnemySyncAction : byte {
	SnapshotReset = 0,	// 快照重置: 新玩家加入时清空客户端状态
	State = 1,			// 状态更新: 同步位置/旋转/生命值
	DamageRequest = 2,  // 伤害请求: 客户端请求主机对实体造成伤害
	Kill = 3,          // 实体死亡: 杀死实体
	Remove = 4,         // 移除: 禁用/销毁实体
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

	private const float SyncInterval = 0.10f; // 状态同步间隔 (秒, 约10Hz)
	private const float PositionEpsilonSqr = 0.0025f; // 位置变化阈值平方 (约0.05m)
	private const float RotationEpsilonDegrees = 1.0f; // 旋转变化阈值 (度)
	private const float HealthEpsilon = 0.001f; // 生命值变化阈值
	private const float StableIdPositionPrecision = 10f; // 稳定ID位置量化精度
	private const int SnapshotEnemiesPerFrame = 12; // 每帧快照发送敌人数量上限
	private const int MaxSyncPerFrame = 8; // 每帧最多处理并广播的敌人数量
	private static readonly AccessTools.FieldRef<GameEntity, float> _healthField =
		AccessTools.FieldRefAccess<GameEntity, float>("health");
	#endregion
	
	#region[静态字段]

	/// <summary>
	/// 所有已注册的敌人字典. Key=NetworkId, Value=NetworkedEnemy 组件.
	/// </summary>
	private static readonly Dictionary<string, NetworkedEnemy> _enemies = new();

	/// <summary>
	/// 按实例ID索引的敌人字典, 用于快速查找场景中已存在的敌人.
	/// </summary>
	private static readonly Dictionary<int, NetworkedEnemy> _byInstanceId = new();

	/// <summary>
	/// 存储通过 OnEnable Hook 新增但尚未注册身份的实体.
	/// </summary>
	private static readonly Queue<GameEntity> _pendingAdditions = new();

	/// <summary>
	/// 每个客户端对应的快照发送协程. Key=客户端SteamId, Value=协程引用.
	/// </summary>
	private static readonly Dictionary<ulong, Coroutine> _snapshotRoutines = new();

	/// <summary>
	/// 生命值成员缓存. Key=类型, Value=FieldInfo或PropertyInfo.
	/// 避免每次获取/设置生命值时都进行反射查找.
	/// </summary>
	private static readonly Dictionary<Type, MemberInfo> _healthMembers = new();

	private static Coroutine _syncRoutine; // 主同步协程引用
	private static bool _sceneEnemiesRegistered; // 场景敌人是否已注册完成

	/// <summary>
	/// 是否正在应用远程状态. 用于防止应用远程数据时再次触发本地广播造成循环.
	/// </summary>
	public static bool ApplyingRemoteState { get; private set; }

	#endregion

	#region[公共接口 - 初始化与重置]

	/// <summary>
	/// 世界初始化完成通知: 重置状态并启动主同步协程.
	/// </summary>
	public static void NotifyWorldInitialized() {
		ResetState();
		if (MPCore.Instance == null) return;
		_syncRoutine = MPCore.Instance.StartCoroutine(WorldRoutine());
	}

	/// <summary>
	/// 完全重置敌人同步状态: 停止所有协程, 清空字典, 重置标志.
	/// </summary>
	public static void ResetState() {
		if (_syncRoutine != null && MPCore.Instance != null) 
			MPCore.Instance.StopCoroutine(_syncRoutine);
		
		_syncRoutine = null;

		if (MPCore.Instance != null) 
			foreach (var routine in _snapshotRoutines.Values) 
				if (routine != null) MPCore.Instance.StopCoroutine(routine);
			
		_snapshotRoutines.Clear();
		_enemies.Clear();
		_byInstanceId.Clear();
		_pendingAdditions.Clear();
		_sceneEnemiesRegistered = false;
		ApplyingRemoteState = false;
	}

	#endregion

	#region[增量 Hook 事件接入]

	/// <summary>
	/// 实体启用时的增量回调 (由 Harmony Patch 调用)
	/// </summary>
	public static void OnEntityEnabled(GameEntity entity) {
		if (entity == null || !MPCore.CanSync || !MPSteamworks.Instance.IsHost) return;
		_pendingAdditions.Enqueue(entity);
	}

	/// <summary>
	/// 实体禁用/销毁时的增量回调 (由 Harmony Patch 调用)
	/// </summary>
	public static void OnEntityDisabled(GameEntity entity) {
		if (entity == null || ApplyingRemoteState) return;

		var syncRoot = entity;
		if (syncRoot == null) return;

		int instanceId = syncRoot.GetInstanceID();
		if (_byInstanceId.TryGetValue(instanceId, out var identity)) {
			// 如果是主机，广播移除并从自身字典移除记录
			if (MPSteamworks.Instance.IsHost) BroadcastRemove(identity);
			RemoveEnemyRecord(identity);
		}
	}

	#endregion

	#region[公共接口 - 快照与伤害]

	/// <summary>
	/// 向指定客户端发送敌人快照. 仅主机可调用.
	/// </summary>
	public static void SendSnapshotToClient(IDType clientId) {
		if (!MPCore.CanSync || !MPSteamworks.Instance.IsHost || MPCore.Instance == null) return;
		if (clientId == 0 || clientId == MPSteamworks.UserSteamId) return;

		if (_snapshotRoutines.TryGetValue(clientId, out var existing) && existing != null) 
			MPCore.Instance.StopCoroutine(existing);
		
		_snapshotRoutines[clientId] = MPCore.Instance.StartCoroutine(SendSnapshotToClientRoutine(clientId));
	}

	/// <summary>
	/// 本地敌人受伤通知: 客户端向主机发送伤害请求.
	/// </summary>
	public static void NotifyLocalEnemyDamage(GameEntity entity, Damageable.DamageInfo info) {
		if (ApplyingRemoteState || entity == null || info == null || !MPCore.CanSync) return;
		if (MPSteamworks.Instance.IsHost) return;
		if (!TryGetEnemyIdentity(entity, out var identity)) return;

		// 构建并发送伤害请求数据包
		var writer = GetWriter(MPSteamworks.UserSteamId, MPSteamworks.Instance.HostSteamId, PacketType.EnemyStateSync);
		writer.Put((byte)EnemySyncAction.DamageRequest);
		writer.Put(identity.NetworkId);
		writer.Put(info.amount);
		writer.Put(info.type);
		writer.Put(info.tags);
		writer.Put(info.position);
		MPSteamworks.Instance.SendToHost(writer, SendType.Reliable);
	}

	#endregion

	#region[协程与分帧同步]

	/// <summary>
	/// 主世界协程: 1次全量扫描 + 增量更新 + 分帧状态广播.
	/// </summary>
	private static IEnumerator WorldRoutine() {
		yield return new WaitUntil(() => WorldLoader.isLoaded && WorldLoader.initialized);
		yield return null;
		yield return null;

		// 世界加载完毕：执行仅此 1 次的全量扫描
		RegisterInitialSceneEnemies();

		// 复用缓存列表，避免每轮循环产生 GC 垃圾
		var activeSnapshot = new List<NetworkedEnemy>();

		while (MPCore.IsInLobby && MPCore.IsInitialized) {
			if (MPCore.CanSync && MPSteamworks.Instance.IsHost) {
				// 增量扫描处理新增实体
				ProcessIncrementalEntities();

				// 获取当前存活敌人快照
				activeSnapshot.Clear();
				activeSnapshot.AddRange(_enemies.Values);

				int processedCount = 0;

				for (int i = 0; i < activeSnapshot.Count; i++) {
					var identity = activeSnapshot[i];
					if (identity == null || identity.gameObject == null) continue;

					// 生物死亡/移除检测
					if (IsRemoved(identity)) {
						BroadcastRemove(identity);
						RemoveEnemyRecord(identity);
						continue;
					}

					// 状态变化检查与广播
					if (HasMeaningfulChange(identity)) {
						BroadcastState(identity);
						RememberState(identity);
					}

					processedCount++;

					// 分帧更新控制 处理满 MaxSyncPerFrame 个敌人则让出当帧
					if (processedCount % MaxSyncPerFrame == 0) {
						yield return null;
						// 让出帧期间可能有新生成实体，及时补充消费
						ProcessIncrementalEntities();
					}
				}
			}

			yield return new WaitForSeconds(SyncInterval);
		}
	}

	/// <summary>
	/// 向客户端发送快照协程: 发送 SnapshotReset, 逐帧发送所有敌人状态.
	/// </summary>
	private static IEnumerator SendSnapshotToClientRoutine(IDType clientId) {
		// 确保场景敌人已注册
		while (!_sceneEnemiesRegistered && MPCore.CanSync) {
			yield return null;
		}

		// 发送快照重置
		var reset = GetWriter(MPSteamworks.UserSteamId, clientId, PacketType.EnemyStateSync);
		reset.Put((byte)EnemySyncAction.SnapshotReset);
		MPSteamworks.Instance.SendToPeer(clientId, reset, SendType.Reliable);

		// 逐帧发送所有敌人状态
		int sent = 0;
		foreach (var identity in _enemies.Values) {
			if (identity == null || identity.gameObject == null || IsRemoved(identity)) continue;
			SendStateToClient(clientId, identity, reliable: true);
			if (++sent % SnapshotEnemiesPerFrame == 0) yield return null;
		}

		_snapshotRoutines.Remove(clientId);
	}

	#endregion

	#region[扫描与身份注册]

	/// <summary>
	/// 全量扫描场景中的所有已有敌人.
	/// </summary>
	private static void RegisterInitialSceneEnemies() {
		var levelRoot = WorldLoader.instance?.transform;
		if (levelRoot == null) return;

		var entities = levelRoot.GetComponentsInChildren<AIGameEntity>(includeInactive: false);
		foreach (var entity in entities) {
			if (!IsSyncableEnemy(entity)) continue;
			EnsureIdentity(entity);
		}
		_sceneEnemiesRegistered = true;
	}

	/// <summary>
	/// 增量扫描快速消费并注册新生成的实体.
	/// </summary>
	private static void ProcessIncrementalEntities() {
		while (_pendingAdditions.Count > 0) {
			var entity = _pendingAdditions.Dequeue();
			if (entity == null || !entity.gameObject.activeInHierarchy) continue;
			if (!IsSyncableEnemy(entity)) continue;

			EnsureIdentity(entity);
		}
	}

	/// <summary>
	/// 尝试获取敌人的 NetworkedEnemy 身份组件.
	/// </summary>
	private static bool TryGetEnemyIdentity(GameEntity entity, out NetworkedEnemy identity) {
		identity = null;
		if (!IsSyncableEnemy(entity)) return false;

		identity = EnsureIdentity(entity);
		return identity != null && !string.IsNullOrEmpty(identity.NetworkId);
	}

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
		var identity = syncRoot.GetComponent<NetworkedEnemy>() ?? syncRoot.gameObject.AddComponent<NetworkedEnemy>();
		if (string.IsNullOrEmpty(identity.NetworkId)) identity.NetworkId = BuildStableNetworkId(syncRoot);
		
		// 注册到字典
		_enemies[identity.NetworkId] = identity;
		_byInstanceId[instanceId] = identity;
		RememberState(identity);
		return identity;
	}

	/// <summary>
	/// 检查 GameEntity 是否为可同步的敌人.
	/// 排除: 玩家、远程实体、RP容器、物品物体.
	/// 包含: 带有 "Creature" 标签的物体, 或名称以 "Denizen_"/"DEN_" 开头.
	/// </summary>
	private static bool IsSyncableEnemy(GameEntity entity) {
		if (entity == null || entity.gameObject == null) return false;

		// 排除玩家相关
		if (entity.GetComponent<ENT_Player>() != null) return false;
		// 排除物品物体
		if (entity.GetComponent<Item_Object>() != null) return false;
		// 排除远程实体 (由其他系统管理)
		if (entity.GetComponent<RemoteEntity>() != null) return false;
		// 排除 RP 容器
		if (entity.GetComponent<RPContainerRef>() != null) return false;

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

	/// <summary>
	/// 从本地所有字典中注销并移除敌人记录.
	/// </summary>
	private static void RemoveEnemyRecord(NetworkedEnemy identity) {
		if (identity == null) return;

		if (!string.IsNullOrEmpty(identity.NetworkId)) _enemies.Remove(identity.NetworkId);

		int instanceId = identity.transform.GetInstanceID();
		_byInstanceId.Remove(instanceId);
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
	/// 检查敌人是否已移除: GameObject为null、未激活、或生命值<=0.
	/// </summary>
	private static bool IsRemoved(NetworkedEnemy identity) {
		if (identity == null || identity.gameObject == null) return true;
		if (!identity.gameObject.activeInHierarchy) return true;

		float health = identity.TryGetComponent<GameEntity>(out GameEntity entity) ? _healthField(entity) : float.NaN;
		return !float.IsNaN(health) && health <= 0f;
	}

	#endregion

	#region[网络发送]

	/// <summary>
	/// 广播敌人状态 (不可靠传输, 高频更新).
	/// </summary>
	private static void BroadcastState(NetworkedEnemy identity) {
		var writer = BuildStateWriter(MPProtocol.BroadcastId, identity);
		MPSteamworks.Instance.Broadcast(writer, SendType.Unreliable | SendType.NoNagle);
	}

	/// <summary>
	/// 向指定客户端发送敌人状态.
	/// </summary>
	private static void SendStateToClient(IDType clientId, NetworkedEnemy identity, bool reliable) {
		var writer = BuildStateWriter(clientId, identity);
		MPSteamworks.Instance.SendToPeer(clientId, writer, reliable ? SendType.Reliable : SendType.Unreliable);
	}

	/// <summary>
	/// 构建状态数据包: NetworkId + 位置 + 旋转 + 生命值.
	/// </summary>
	private static DataWriter BuildStateWriter(IDType targetId, NetworkedEnemy identity) {
		var writer = GetWriter(MPSteamworks.UserSteamId, targetId, PacketType.EnemyStateSync);
		writer.Put((byte)EnemySyncAction.State);
		writer.Put(identity.NetworkId);
		writer.Put(identity.transform.position);
		writer.Put(identity.transform.rotation);
		writer.Put(identity.TryGetComponent<GameEntity>(out GameEntity entity) ? _healthField(entity) : float.NaN);
		return writer;
	}

	/// <summary>
	/// 广播敌人移除 (可靠传输).
	/// </summary>
	private static void BroadcastRemove(NetworkedEnemy identity) {
		var writer = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.EnemyStateSync);
		writer.Put((byte)EnemySyncAction.Remove);
		writer.Put(identity.NetworkId);
		MPSteamworks.Instance.Broadcast(writer, SendType.Reliable);
	}

	/// <summary>
	/// 广播实体死亡 (可靠传输).
	/// </summary>
	private static void BroadcastKill(NetworkedEnemy identity) {
		var writer = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.EnemyStateSync);
		writer.Put((byte)EnemySyncAction.Kill);
		writer.Put(identity.NetworkId);
		MPSteamworks.Instance.Broadcast(writer, SendType.Reliable);
	}

	#endregion

	#region[消息处理 - 客户端/主机]

	/// <summary>
	/// 处理接收到的敌人同步数据包: 根据操作类型分发到对应处理方法.
	/// </summary>
	public static void HandleEnemyState(IDType senderId, DataReader reader) {
		var action = (EnemySyncAction)reader.GetByte();
		try {
			switch (action) {
				case EnemySyncAction.SnapshotReset:
					HandleSnapshotReset();
					break;
				case EnemySyncAction.State:
					HandleState(reader);
					break;
				case EnemySyncAction.DamageRequest:
					HandleDamageRequest(senderId, reader);
					break;
				case EnemySyncAction.Remove:
					HandleRemove(reader);
					break;
				case EnemySyncAction.Kill:
					HandleKill(reader);
					break;
				default:
					MPMain.LogWarning($"[MP EnemySync] Unknown action: {action}");
					break;
			}
		} catch (Exception ex) {
			MPMain.LogError($"[MP EnemySync] Failed to apply {action}: {ex.Message}");
		}
	}

	/// <summary>
	/// 客户端收到快照重置: 清空所有敌人数据, 重新注册场景敌人.
	/// </summary>
	private static void HandleSnapshotReset() {
		if (MPSteamworks.Instance.IsHost) return;
		_enemies.Clear();
		_byInstanceId.Clear();
		_pendingAdditions.Clear();
		RegisterInitialSceneEnemies();
	}

	/// <summary>
	/// 客户端收到状态更新: 解析并应用位置、旋转和生命值.
	/// </summary>
	private static void HandleState(DataReader reader) {
		if (MPSteamworks.Instance.IsHost) return;

		string networkId = reader.GetString();
		Vector3 position = reader.GetVector3();
		Quaternion rotation = reader.GetQuaternion();
		float health = reader.GetFloat();

		if (!TryResolveIdentity(networkId, out var identity)) return;

		ApplyingRemoteState = true;
		try {
			identity.transform.SetPositionAndRotation(position, rotation);
			if (identity.TryGetComponent<GameEntity>(out GameEntity entity)) 
				_healthField(entity) = health;
			if (!identity.gameObject.activeSelf) identity.gameObject.SetActive(true);
			RememberState(identity);
		} finally {
			ApplyingRemoteState = false;
		}
	}

	/// <summary>
	/// 客户端收到移除消息: 标记并禁用敌人.
	/// </summary>
	private static void HandleRemove(DataReader reader) {
		if (MPSteamworks.Instance.IsHost) return;

		string networkId = reader.GetString();
		if (!TryResolveIdentity(networkId, out var identity)) return;

		ApplyingRemoteState = true;
		try {
			identity.LastRemoved = true;
			identity.gameObject?.SetActive(false);
		} finally {
			ApplyingRemoteState = false;
		}

		// 移除本地记录
		RemoveEnemyRecord(identity);
	}

	/// <summary>
	/// 客户端收到实体死亡消息: 
	/// </summary>
	private static void HandleKill(DataReader reader) {
		if (MPSteamworks.Instance.IsHost) return;

		string networkId = reader.GetString();
		if (!TryResolveIdentity(networkId, out var identity)) return;

		ApplyingRemoteState = true;
		try {
			identity.GetComponent<GameEntity>()?.Kill("otherPlayer");
		} finally {
			ApplyingRemoteState = false;
		}
	}

	/// <summary>
	/// 主机收到伤害请求: 对指定敌人施加伤害, 更新状态并广播.
	/// </summary>
	private static void HandleDamageRequest(IDType senderId, DataReader reader) {
		if (!MPSteamworks.Instance.IsHost) return;

		string networkId = reader.GetString();
		float amount = reader.GetFloat();
		string type = reader.GetString();
		List<string> tags = reader.GetStringList();
		Vector3 position = reader.GetVector3();

		if (!_enemies.TryGetValue(networkId, out var identity) || identity == null) return;

		var entity = identity.GetComponentInChildren<GameEntity>();
		if (entity == null) return;

		// 构建伤害信息并应用
		var info = Damageable.DamageInfo.CreateDamageInfo(amount, type, tags);
		info.position = position;
		entity.Damage(info);

		// 更新状态并广播
		if (entity.dead) {
			BroadcastKill(identity);
		} else if (IsRemoved(identity)) {
			BroadcastRemove(identity);
			RemoveEnemyRecord(identity);
		} else {
			RememberState(identity);
			BroadcastState(identity);
		}
	}

	#endregion

	#region[身份解析与生命值反射]

	/// <summary>
	/// 尝试解析 NetworkId 对应的 NetworkedEnemy: 先在字典查找, 失败则重新扫描场景.
	/// </summary>
	private static bool TryResolveIdentity(string networkId, out NetworkedEnemy identity) {
		if (_enemies.TryGetValue(networkId, out identity) && identity != null) return true;

		// 缓存未命中: 重新扫描场景
		ProcessIncrementalEntities();
		return _enemies.TryGetValue(networkId, out identity) && identity != null;
	}

	#endregion

	#region[稳定 ID 生成]

	/// <summary>
	/// 构建稳定的 NetworkId: "enemy:{层级路径}:{量化X}:{量化Y}:{量化Z}".
	/// </summary>
	private static string BuildStableNetworkId(Transform transform) {
		var position = transform.position;
		return "enemy:" + MPUtil.BuildTransformPath(transform)
			+ $":{Quantize(position.x)}:{Quantize(position.y)}:{Quantize(position.z)}";
	}

	/// <summary>
	/// 量化坐标值以提高匹配稳定性.
	/// </summary>
	private static int Quantize(float value) {
		return Mathf.RoundToInt(value * StableIdPositionPrecision);
	}

	#endregion
}
