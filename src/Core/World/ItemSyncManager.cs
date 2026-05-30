using Steamworks.Data;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using WKMPMod.Component;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.NetWork;
using static WKMPMod.Data.MPWriterPool;
using Object = UnityEngine.Object;

namespace WKMPMod.World;

public enum ItemSyncAction : byte {
	SnapshotReset = 0,
	SnapshotFinalize = 1,
	Create = 2,
	PickupRequest = 3,
	DropRequest = 4,
	Remove = 5,
}

public static class ItemSyncManager {
	private const float CandidateMatchDistanceSqr = 0.5f;
	private const float LocalDropMatchDistanceSqr = 25f;
	private const float LocalDropMaxAge = 3f;
	private const float LocalDropPickupSuppressWindow = 0.2f;
	private const float VelocityEpsilonSqr = 0.0025f;
	private const float WorldReadyTimeout = 12f;
	private const float StableIdPositionPrecision = 20f;
	private const float StableIdRotationPrecision = 5f;

	private const int SnapshotItemsPerFrame = 10;
	private static readonly System.Reflection.FieldInfo _dropObjectField =
		typeof(Item).GetField("dropObject", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

	private static readonly Dictionary<string, NetworkedItem> _items = new();
	private static readonly List<Item_Object> _clientCandidates = new();
	private static readonly List<PendingLocalDrop> _pendingLocalDrops = new();
	private static readonly List<Item_Object> _snapshotCandidates = new();
	private static readonly Dictionary<ulong, Coroutine> _snapshotRoutines = new();
	private static readonly Dictionary<int, float> _suppressedPickupObjects = new();

	private static ulong _nextHostItemId = 1;
	private static Coroutine _prepareRoutine;
	private static Coroutine _hostDiscoveryRoutine;
	private static bool _hostSceneItemsRegistered;

	public static bool ApplyingRemoteState { get; private set; }

	public static void NotifyWorldInitialized() {
		ResetState();

		if (MPCore.Instance == null) {
			MPMain.LogWarning("[MP ItemSync] Prepare skipped: MPCore.Instance is null.");
			return;
		}

		_prepareRoutine = MPCore.Instance.StartCoroutine(PrepareWorldRoutine());
	}

	public static void ResetState() {
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
				if (routine != null) {
					MPCore.Instance.StopCoroutine(routine);
				}
			}
		}

		_snapshotRoutines.Clear();

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

	public static void SendSnapshotToClient(ulong clientId) {
		if (!MPCore.CanSync) return;
		if (!MPSteamworks.Instance.IsHost) return;
		if (MPCore.Instance == null) return;
		if (clientId == 0 || clientId == MPSteamworks.UserSteamId) return;

		if (_snapshotRoutines.TryGetValue(clientId, out var existingRoutine) && existingRoutine != null) {
			MPCore.Instance.StopCoroutine(existingRoutine);
		}

		_snapshotRoutines[clientId] = MPCore.Instance.StartCoroutine(SendSnapshotToClientRoutine(clientId));
	}

	public static void NotifyLocalPickup(Item_Object itemObject) {
		if (ApplyingRemoteState || itemObject == null || !MPCore.CanSync) return;
		if (IsPendingLocalDrop(itemObject)) {
			MPMain.LogInfo($"[MP ItemSync] Suppressed pickup for pending local drop. Item={itemObject.name}");
			return;
		}
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

		if (MPSteamworks.Instance.IsHost) {
			BroadcastRemove(identity.NetworkId);
			Forget(identity.NetworkId);
			return;
		}

		SendPickupRequest(identity.NetworkId);
	}

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

		RememberCandidate(itemObject, snapshotCandidate: false);
		RememberPendingLocalDrop(itemObject, prefabKey);
		RememberSuppressedPickup(itemObject);

		MPMain.LogInfo(
			$"[MP ItemSync] Client local drop request. " +
			$"Item={itemObject.name}, PrefabKey={prefabKey}"
		);

		SendDropRequest(prefabKey, itemObject.transform.position, itemObject.transform.rotation, GetVelocity(itemObject));
	}

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
			yield return RegisterHostSceneItemsRoutine();
			if (MPCore.Instance != null && _hostDiscoveryRoutine == null) {
				_hostDiscoveryRoutine = MPCore.Instance.StartCoroutine(HostDiscoveryRoutine());
			}
		} else if (MPCore.IsInLobby) {
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

	private static IEnumerator SendSnapshotToClientRoutine(ulong clientId) {
		yield return WaitForWorldReady();

		if (!_hostSceneItemsRegistered) {
			yield return RegisterHostSceneItemsRoutine();
		}

		SendSnapshotReset(clientId);

		var snapshot = new List<NetworkedItem>(EnumerateKnownHostWorldItems());

		int sentThisFrame = 0;

		foreach (var identity in snapshot) {
			if (identity == null || identity.gameObject == null) continue;

			var itemObject = identity.GetComponent<Item_Object>();
			if (!IsSyncableWorldItem(itemObject)) continue;

			SendCreate(clientId, identity, itemObject, GetVelocity(itemObject), isDropSpawn: false);

			sentThisFrame++;
			if (sentThisFrame >= SnapshotItemsPerFrame) {
				sentThisFrame = 0;
				yield return null;
			}
		}

		SendSnapshotFinalize(clientId);
		_snapshotRoutines.Remove(clientId);

		MPMain.LogInfo($"[MP ItemSync] Sent item snapshot to {clientId}. Items={snapshot.Count}");
	}

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

	private static IEnumerable<NetworkedItem> EnumerateKnownHostWorldItems() {
		foreach (var identity in _items.Values) {
			if (identity == null || identity.gameObject == null) continue;

			var itemObject = identity.GetComponent<Item_Object>();
			if (!IsSyncableWorldItem(itemObject)) continue;

			yield return identity;
		}
	}

	private static NetworkedItem RegisterHostItem(
		Item_Object itemObject,
		string prefabKey,
		bool preferStableSceneIdentity = false
	) {
		var identity = GetOrCreateIdentity(itemObject.gameObject);

		if (preferStableSceneIdentity) {
			TryAssignStableSceneIdentity(itemObject, identity);
		}

		if (string.IsNullOrEmpty(identity.NetworkId)) {
			identity.NetworkId = $"{MPSteamworks.UserSteamId}:item:{_nextHostItemId++}";
		}

		identity.PrefabKey = prefabKey;
		identity.OwnerId = MPSteamworks.UserSteamId;
		identity.IsRemote = false;
		identity.WasInstantiatedBySync = false;

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

	private static void HandleSnapshotReset() {
		if (MPSteamworks.Instance.IsHost) return;

		MPMain.LogInfo("[MP ItemSync] Received snapshot reset.");

		ResetState();
		CaptureSceneCandidates(snapshotCandidate: true);
	}

	private static void HandleSnapshotFinalize() {
		if (MPSteamworks.Instance.IsHost) return;

		int hidden = 0;

		for (int i = _snapshotCandidates.Count - 1; i >= 0; i--) {
			var candidate = _snapshotCandidates[i];
			if (candidate == null || candidate.gameObject == null) continue;

			var identity = candidate.GetComponent<NetworkedItem>();
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

	private static void HandleCreate(ulong senderId, DataReader reader) {
		if (MPSteamworks.Instance.IsHost) return;

		var networkId = reader.GetString();
		var prefabKey = reader.GetString();
		var position = reader.GetVector3();
		var rotation = reader.GetQuaternion();
		var velocity = reader.GetVector3();
		var isDropSpawn = reader.GetBool();

		if (string.IsNullOrEmpty(networkId) || string.IsNullOrEmpty(prefabKey)) return;

		if (_items.TryGetValue(networkId, out var existing) && existing != null) {
			ApplyCreate(existing, position, rotation, velocity, isDropSpawn, skipDropCallbacks: true);
			return;
		}

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

		if (itemObject == null) {
			itemObject = InstantiateWorldItem(prefabKey, position, rotation);
			instantiatedBySync = itemObject != null;
		}

		if (itemObject == null) {
			MPMain.LogWarning($"[MP ItemSync] Could not create item '{prefabKey}' for {networkId}.");
			return;
		}

		var identity = GetOrCreateIdentity(itemObject.gameObject);

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

		bool skipDropCallbacks = !wasSnapshotCandidate && candidate != null && isDropSpawn;
		ApplyCreate(identity, position, rotation, velocity, isDropSpawn, skipDropCallbacks);
	}

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

	private static void HandleRemove(DataReader reader) {
		var networkId = reader.GetString();

		MPMain.LogInfo($"[MP ItemSync] Received remove. NetworkId={networkId}");

		if (string.IsNullOrEmpty(networkId)) return;

		Forget(networkId);
	}

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

			if (isDropSpawn && itemObject != null && !skipDropCallbacks) {
				itemObject.OnDrop();
			}

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

	private static void Forget(string networkId) {
		if (!_items.TryGetValue(networkId, out var identity) || identity == null) {
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

			if (identity.WasInstantiatedBySync) {
				Object.Destroy(identity.gameObject);
			}
		}

		_items.Remove(networkId);
	}

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

	private static void CaptureSceneCandidates(bool snapshotCandidate) {
		foreach (var itemObject in EnumerateSceneItems()) {
			RememberCandidate(itemObject, snapshotCandidate);
		}
	}

	private static IEnumerable<Item_Object> EnumerateSceneItems() {
		foreach (var itemObject in Object.FindObjectsOfType<Item_Object>()) {
			if (!IsSyncableWorldItem(itemObject)) continue;
			yield return itemObject;
		}
	}

	private static void RememberCandidate(Item_Object itemObject, bool snapshotCandidate) {
		if (!IsSyncableWorldItem(itemObject)) return;

		var identity = GetOrCreateIdentity(itemObject.gameObject);

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

		if (!_clientCandidates.Contains(itemObject)) {
			_clientCandidates.Add(itemObject);
		}

		if (snapshotCandidate && !_snapshotCandidates.Contains(itemObject)) {
			_snapshotCandidates.Add(itemObject);
		}
	}

	private static void RemoveCandidate(Item_Object itemObject) {
		_clientCandidates.Remove(itemObject);
		_snapshotCandidates.Remove(itemObject);
		RemovePendingLocalDrop(itemObject);
	}

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

	private static void RememberPendingLocalDrop(Item_Object itemObject, string prefabKey) {
		if (!IsSyncableWorldItem(itemObject) || string.IsNullOrEmpty(prefabKey)) return;

		RemoveDestroyedPendingLocalDrops();
		RemovePendingLocalDrop(itemObject);

		_pendingLocalDrops.Add(new PendingLocalDrop(itemObject, prefabKey, Time.time));
	}

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

	private static void RemovePendingLocalDrop(Item_Object itemObject) {
		if (itemObject == null) return;

		for (int i = _pendingLocalDrops.Count - 1; i >= 0; i--) {
			if (_pendingLocalDrops[i].ItemObject == itemObject) {
				_pendingLocalDrops.RemoveAt(i);
			}
		}
	}

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

	private static void RemoveDestroyedPendingLocalDrops() {
		float minCreatedAt = Time.time - LocalDropMaxAge;

		for (int i = _pendingLocalDrops.Count - 1; i >= 0; i--) {
			var pending = _pendingLocalDrops[i];
			if (!IsSyncableWorldItem(pending.ItemObject) || pending.CreatedAt < minCreatedAt) {
				_pendingLocalDrops.RemoveAt(i);
			}
		}
	}

	private static void RememberSuppressedPickup(Item_Object itemObject) {
		if (itemObject == null || itemObject.gameObject == null) return;

		RemoveExpiredSuppressedPickups();
		_suppressedPickupObjects[itemObject.gameObject.GetInstanceID()] = Time.time + LocalDropPickupSuppressWindow;
	}

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

	private static void RemoveExpiredSuppressedPickups() {
		if (_suppressedPickupObjects.Count == 0) return;

		foreach (var instanceId in new List<int>(_suppressedPickupObjects.Keys)) {
			if (_suppressedPickupObjects.TryGetValue(instanceId, out float untilTime) && Time.time > untilTime) {
				_suppressedPickupObjects.Remove(instanceId);
			}
		}
	}

	private static bool IsSyncableWorldItem(Item_Object itemObject) {
		if (itemObject == null || itemObject.gameObject == null) return false;
		if (!itemObject.gameObject.activeInHierarchy) return false;
		if (string.IsNullOrEmpty(itemObject.gameObject.scene.name)) return false;
		if (itemObject.itemData == null) return false;

		return !itemObject.itemData.inBag;
	}

	private static Item_Object InstantiateWorldItem(string prefabKey, Vector3 position, Quaternion rotation) {
		GameObject prefab = CL_AssetManager.GetAssetGameObject(prefabKey);
		if (prefab == null) return null;

		ApplyingRemoteState = true;

		try {
			var itemObject = Object.Instantiate(prefab, position, rotation);
			var levelRoot = WorldLoader.GetCurrentLevelParentRoot();

			if (levelRoot != null) {
				itemObject.transform.SetParent(levelRoot);
			}

			var itemComponent = itemObject.GetComponent<Item_Object>() ??
			                    itemObject.GetComponentInChildren<Item_Object>(true);

			if (itemComponent != null && itemComponent.itemData != null) {
				itemComponent.itemData.InitializeItemData(itemComponent);
			}

			return itemComponent;
		} finally {
			ApplyingRemoteState = false;
		}
	}

	private static Vector3 GetVelocity(Item_Object itemObject) {
		var rb = GetRigidbody(itemObject.gameObject);
		if (rb == null) return Vector3.zero;

		return rb.velocity.sqrMagnitude > VelocityEpsilonSqr ? rb.velocity : Vector3.zero;
	}

	private static Rigidbody GetRigidbody(GameObject gameObject) {
		if (gameObject == null) return null;

		return gameObject.GetComponent<Rigidbody>() ?? gameObject.GetComponentInChildren<Rigidbody>();
	}

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

	private static void AssignDropObject(Item item, Item_Object itemObject) {
		if (item == null || itemObject == null) return;

		if (_dropObjectField == null) return;

		try {
			_dropObjectField.SetValue(item, itemObject);
		} catch (System.Exception ex) {
			MPMain.LogWarning($"[MP ItemSync] Failed to assign dropObject via reflection: {ex.Message}");
		}
	}

	private static NetworkedItem GetOrCreateIdentity(GameObject gameObject) {
		var identity = gameObject.GetComponent<NetworkedItem>();

		if (identity == null) {
			identity = gameObject.AddComponent<NetworkedItem>();
		}

		return identity;
	}

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

	private static bool TryAssignStableSceneIdentity(Item_Object itemObject, NetworkedItem identity) {
		if (itemObject == null || identity == null) return false;

		string stableId = GetStableSceneItemId(itemObject);
		if (string.IsNullOrEmpty(stableId)) return false;

		identity.StableSceneId = stableId;
		identity.NetworkId = stableId;

		return true;
	}

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

	private static int Quantize(float value, float precision) {
		return Mathf.RoundToInt(value * precision);
	}

	private static float NormalizeAngle(float value) {
		value %= 360f;

		if (value < 0f) {
			value += 360f;
		}

		return value;
	}

	private static string GetPrefabKey(Item_Object itemObject) {
		if (itemObject == null) return string.Empty;

		if (itemObject.itemData != null && !string.IsNullOrEmpty(itemObject.itemData.prefabName)) {
			return itemObject.itemData.prefabName;
		}

		return CleanCloneName(itemObject.gameObject.name);
	}

	private static bool PrefabKeysMatch(string a, string b) {
		if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;

		return string.Equals(CleanCloneName(a), CleanCloneName(b), System.StringComparison.OrdinalIgnoreCase);
	}

	private static string CleanCloneName(string value) {
		if (string.IsNullOrEmpty(value)) return string.Empty;

		return value.Replace("(Clone)", string.Empty).Trim();
	}

	// sealed何意味
	private sealed class PendingLocalDrop {
		public PendingLocalDrop(Item_Object itemObject, string prefabKey, float createdAt) {
			ItemObject = itemObject;
			PrefabKey = prefabKey;
			CreatedAt = createdAt;
		}

		public Item_Object ItemObject { get; }
		public string PrefabKey { get; }
		public float CreatedAt { get; }
	}
}
