using HarmonyLib;
using Steamworks.Data;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using WKMPMod.Component;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.NetWork;
using static WKMPMod.Data.MPWriterPool;
using Object = UnityEngine.Object;

namespace WKMPMod.World;

public enum EnemySyncAction : byte {
	SnapshotReset = 0,
	State = 1,
	Remove = 2,
	DamageRequest = 3,
}

/// <summary>
/// Host-authoritative denizen/enemy transform, health and death synchronization.
/// Existing scene enemies are matched by a stable hierarchy id, so clients do not
/// need to instantiate enemy prefabs to stay aligned with the host.
/// </summary>
public static class EnemySyncManager {
	private const float DiscoveryInterval = 2.0f;
	private const float SyncInterval = 0.10f;
	private const float PositionEpsilonSqr = 0.0025f;
	private const float RotationEpsilonDegrees = 1.0f;
	private const float HealthEpsilon = 0.001f;
	private const float StableIdPositionPrecision = 10f;
	private const int SnapshotEnemiesPerFrame = 12;

	private static readonly Dictionary<string, NetworkedEnemy> _enemies = new();
	private static readonly Dictionary<int, NetworkedEnemy> _byInstanceId = new();
	private static readonly Dictionary<ulong, Coroutine> _snapshotRoutines = new();
	private static readonly Dictionary<Type, MemberInfo> _healthMembers = new();

	private static Coroutine _syncRoutine;
	private static bool _sceneEnemiesRegistered;

	public static bool ApplyingRemoteState { get; private set; }

	public static void NotifyWorldInitialized() {
		ResetState();
		if (MPCore.Instance == null) return;
		_syncRoutine = MPCore.Instance.StartCoroutine(WorldRoutine());
	}

	public static void ResetState() {
		if (_syncRoutine != null && MPCore.Instance != null) {
			MPCore.Instance.StopCoroutine(_syncRoutine);
		}
		_syncRoutine = null;

		if (MPCore.Instance != null) {
			foreach (var routine in _snapshotRoutines.Values) {
				if (routine != null) MPCore.Instance.StopCoroutine(routine);
			}
		}
		_snapshotRoutines.Clear();
		_enemies.Clear();
		_byInstanceId.Clear();
		_sceneEnemiesRegistered = false;
		ApplyingRemoteState = false;
	}

	public static void SendSnapshotToClient(IDType clientId) {
		if (!MPCore.CanSync || !MPSteamworks.Instance.IsHost || MPCore.Instance == null) return;
		if (clientId == 0 || clientId == MPSteamworks.UserSteamId) return;

		if (_snapshotRoutines.TryGetValue(clientId, out var existing) && existing != null) {
			MPCore.Instance.StopCoroutine(existing);
		}

		_snapshotRoutines[clientId] = MPCore.Instance.StartCoroutine(SendSnapshotToClientRoutine(clientId));
	}

	public static void NotifyLocalEnemyDamage(GameEntity entity, Damageable.DamageInfo info) {
		if (ApplyingRemoteState || entity == null || info == null || !MPCore.CanSync) return;
		if (MPSteamworks.Instance.IsHost) return;
		if (!TryGetEnemyIdentity(entity, out var identity)) return;

		var writer = GetWriter(MPSteamworks.UserSteamId, MPSteamworks.Instance.HostSteamId, PacketType.EnemyStateSync);
		writer.Put((byte)EnemySyncAction.DamageRequest);
		writer.Put(identity.NetworkId);
		writer.Put(info.amount);
		writer.Put(info.type);
		writer.Put(info.tags);
		writer.Put(info.position);
		MPSteamworks.Instance.SendToHost(writer, SendType.Reliable);
	}

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
				case EnemySyncAction.Remove:
					HandleRemove(reader);
					break;
				case EnemySyncAction.DamageRequest:
					HandleDamageRequest(senderId, reader);
					break;
				default:
					MPMain.LogWarning($"[MP EnemySync] Unknown action: {action}");
					break;
			}
		} catch (Exception ex) {
			MPMain.LogError($"[MP EnemySync] Failed to apply {action}: {ex.Message}");
		}
	}

	private static IEnumerator WorldRoutine() {
		yield return new WaitUntil(() => WorldLoader.isLoaded);
		yield return null;

		RegisterSceneEnemies();

		while (MPCore.IsInLobby && MPCore.IsInitialized) {
			if (MPCore.CanSync && MPSteamworks.Instance.IsHost) {
				RegisterSceneEnemies();
				BroadcastChangedEnemies();
			}
			yield return new WaitForSeconds(SyncInterval);
		}
	}

	private static IEnumerator SendSnapshotToClientRoutine(IDType clientId) {
		while (!_sceneEnemiesRegistered && MPCore.CanSync) {
			RegisterSceneEnemies();
			yield return null;
		}

		var reset = GetWriter(MPSteamworks.UserSteamId, clientId, PacketType.EnemyStateSync);
		reset.Put((byte)EnemySyncAction.SnapshotReset);
		MPSteamworks.Instance.SendToPeer(clientId, reset, SendType.Reliable);

		int sent = 0;
		foreach (var identity in _enemies.Values) {
			if (identity == null || identity.gameObject == null) continue;
			SendStateToClient(clientId, identity, reliable: true);
			if (++sent % SnapshotEnemiesPerFrame == 0) yield return null;
		}

		_snapshotRoutines.Remove(clientId);
	}

	private static void RegisterSceneEnemies() {
		var entities = Object.FindObjectsOfType<GameEntity>();
		foreach (var entity in entities) {
			if (!IsSyncableEnemy(entity)) continue;
			EnsureIdentity(entity);
		}
		_sceneEnemiesRegistered = true;
	}

	private static void BroadcastChangedEnemies() {
		foreach (var identity in _enemies.Values) {
			if (identity == null || identity.gameObject == null) continue;

			bool removed = IsRemoved(identity);
			if (removed) {
				if (!identity.LastRemoved) {
					identity.LastRemoved = true;
					BroadcastRemove(identity);
				}
				continue;
			}

			if (HasMeaningfulChange(identity)) {
				BroadcastState(identity);
				RememberState(identity);
			}
		}
	}

	private static bool TryGetEnemyIdentity(GameEntity entity, out NetworkedEnemy identity) {
		identity = null;
		if (!IsSyncableEnemy(entity)) return false;

		identity = EnsureIdentity(entity);
		return identity != null && !string.IsNullOrEmpty(identity.NetworkId);
	}

	private static NetworkedEnemy EnsureIdentity(GameEntity entity) {
		var syncRoot = GetSyncRoot(entity);
		if (syncRoot == null) return null;

		int instanceId = syncRoot.GetInstanceID();
		if (_byInstanceId.TryGetValue(instanceId, out var existing) && existing != null) {
			return existing;
		}

		var identity = syncRoot.GetComponent<NetworkedEnemy>() ?? syncRoot.gameObject.AddComponent<NetworkedEnemy>();
		if (string.IsNullOrEmpty(identity.NetworkId)) {
			identity.NetworkId = BuildStableNetworkId(syncRoot);
		}

		_enemies[identity.NetworkId] = identity;
		_byInstanceId[instanceId] = identity;
		RememberState(identity);
		return identity;
	}

	private static bool IsSyncableEnemy(GameEntity entity) {
		if (entity == null || entity.gameObject == null) return false;
		if (entity.GetComponentInParent<ENT_Player>() != null) return false;
		if (entity.GetComponentInParent<RemoteEntity>() != null) return false;
		if (entity.GetComponentInParent<RPContainerRef>() != null) return false;
		if (entity.GetComponentInParent<Item_Object>() != null) return false;

		var taggers = entity.GetComponentsInParent<ObjectTagger>(true);
		foreach (var tagger in taggers) {
			if (tagger?.tags == null) continue;
			if (tagger.tags.Contains(MPKeys.CREATURE_TAGGER)) return true;
		}

		var rootName = GetSyncRoot(entity)?.name ?? entity.name;
		return rootName.StartsWith("Denizen_", StringComparison.OrdinalIgnoreCase)
			|| rootName.StartsWith("DEN_", StringComparison.OrdinalIgnoreCase);
	}

	private static Transform GetSyncRoot(GameEntity entity) {
		var rigidbody = entity.GetComponentInParent<Rigidbody>();
		if (rigidbody != null && rigidbody.transform.root != rigidbody.transform) {
			return rigidbody.transform;
		}

		var prop = entity.GetComponentInParent<CL_Prop>();
		if (prop != null) return prop.transform;

		return entity.transform;
	}

	private static bool HasMeaningfulChange(NetworkedEnemy identity) {
		var transform = identity.transform;
		if ((transform.position - identity.LastPosition).sqrMagnitude > PositionEpsilonSqr) return true;
		if (Quaternion.Angle(transform.rotation, identity.LastRotation) > RotationEpsilonDegrees) return true;

		float health = GetHealth(identity);
		if (float.IsNaN(health) != float.IsNaN(identity.LastHealth)) return true;
		if (!float.IsNaN(health) && Mathf.Abs(health - identity.LastHealth) > HealthEpsilon) return true;

		return false;
	}

	private static void RememberState(NetworkedEnemy identity) {
		identity.LastPosition = identity.transform.position;
		identity.LastRotation = identity.transform.rotation;
		identity.LastHealth = GetHealth(identity);
		identity.LastRemoved = IsRemoved(identity);
	}

	private static bool IsRemoved(NetworkedEnemy identity) {
		if (identity == null || identity.gameObject == null) return true;
		if (!identity.gameObject.activeInHierarchy) return true;

		float health = GetHealth(identity);
		return !float.IsNaN(health) && health <= 0f;
	}

	private static void BroadcastState(NetworkedEnemy identity) {
		var writer = BuildStateWriter(MPProtocol.BroadcastId, identity);
		MPSteamworks.Instance.Broadcast(writer, SendType.Unreliable | SendType.NoNagle);
	}

	private static void SendStateToClient(IDType clientId, NetworkedEnemy identity, bool reliable) {
		var writer = BuildStateWriter(clientId, identity);
		MPSteamworks.Instance.SendToPeer(clientId, writer, reliable ? SendType.Reliable : SendType.Unreliable);
	}

	private static DataWriter BuildStateWriter(IDType targetId, NetworkedEnemy identity) {
		var writer = GetWriter(MPSteamworks.UserSteamId, targetId, PacketType.EnemyStateSync);
		writer.Put((byte)EnemySyncAction.State);
		writer.Put(identity.NetworkId);
		writer.Put(identity.transform.position);
		writer.Put(identity.transform.rotation);
		writer.Put(GetHealth(identity));
		return writer;
	}

	private static void BroadcastRemove(NetworkedEnemy identity) {
		var writer = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.EnemyStateSync);
		writer.Put((byte)EnemySyncAction.Remove);
		writer.Put(identity.NetworkId);
		MPSteamworks.Instance.Broadcast(writer, SendType.Reliable);
	}

	private static void HandleSnapshotReset() {
		if (MPSteamworks.Instance.IsHost) return;
		_enemies.Clear();
		_byInstanceId.Clear();
		RegisterSceneEnemies();
	}

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
			SetHealth(identity, health);
			if (!identity.gameObject.activeSelf) identity.gameObject.SetActive(true);
			RememberState(identity);
		} finally {
			ApplyingRemoteState = false;
		}
	}

	private static void HandleRemove(DataReader reader) {
		if (MPSteamworks.Instance.IsHost) return;

		string networkId = reader.GetString();
		if (!TryResolveIdentity(networkId, out var identity)) return;

		ApplyingRemoteState = true;
		try {
			identity.LastRemoved = true;
			identity.gameObject.SetActive(false);
		} finally {
			ApplyingRemoteState = false;
		}
	}

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

		var info = Damageable.DamageInfo.CreateDamageInfo(amount, type, tags);
		info.position = position;
		entity.Damage(info);
		RememberState(identity);
		BroadcastState(identity);
	}

	private static bool TryResolveIdentity(string networkId, out NetworkedEnemy identity) {
		if (_enemies.TryGetValue(networkId, out identity) && identity != null) return true;

		RegisterSceneEnemies();
		return _enemies.TryGetValue(networkId, out identity) && identity != null;
	}

	private static float GetHealth(NetworkedEnemy identity) {
		var entity = identity.GetComponentInChildren<GameEntity>();
		if (entity == null) return float.NaN;

		var member = GetHealthMember(entity.GetType());
		if (member is FieldInfo field && field.GetValue(entity) is float fieldValue) return fieldValue;
		if (member is PropertyInfo property && property.GetValue(entity) is float propertyValue) return propertyValue;
		return float.NaN;
	}

	private static void SetHealth(NetworkedEnemy identity, float health) {
		if (float.IsNaN(health)) return;

		var entity = identity.GetComponentInChildren<GameEntity>();
		if (entity == null) return;

		var member = GetHealthMember(entity.GetType());
		if (member is FieldInfo field) field.SetValue(entity, health);
		else if (member is PropertyInfo property && property.CanWrite) property.SetValue(entity, health);
	}

	private static MemberInfo GetHealthMember(Type type) {
		if (_healthMembers.TryGetValue(type, out var member)) return member;

		string[] names = { "health", "curHealth", "currentHealth", "Health", "hp", "HP" };
		foreach (var name in names) {
			var field = AccessTools.Field(type, name);
			if (field != null && field.FieldType == typeof(float)) {
				_healthMembers[type] = field;
				return field;
			}

			var property = AccessTools.Property(type, name);
			if (property != null && property.PropertyType == typeof(float)) {
				_healthMembers[type] = property;
				return property;
			}
		}

		_healthMembers[type] = null;
		return null;
	}

	private static string BuildStableNetworkId(Transform transform) {
		var position = transform.position;
		return "enemy:" + BuildTransformPath(transform)
			+ $":{Quantize(position.x)}:{Quantize(position.y)}:{Quantize(position.z)}";
	}

	private static string BuildTransformPath(Transform transform) {
		var stack = new Stack<string>();
		var current = transform;
		while (current != null) {
			stack.Push($"{CleanName(current.name)}[{current.GetSiblingIndex()}]");
			current = current.parent;
		}
		return string.Join("/", stack);
	}

	private static string CleanName(string name) {
		return string.IsNullOrEmpty(name) ? "unknown" : name.Replace("(Clone)", "").Trim();
	}

	private static int Quantize(float value) {
		return Mathf.RoundToInt(value * StableIdPositionPrecision);
	}
}
