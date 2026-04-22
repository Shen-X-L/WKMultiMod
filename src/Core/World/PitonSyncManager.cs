using Steamworks.Data;
using System;
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

public enum PitonSyncAction : byte {
	Create = 0,
	Update = 1,
	Remove = 2,
}

public static class PitonSyncManager {
	private const float PeriodicUpdateInterval = 0.15f;
	private const float PositionEpsilonSqr = 0.0004f;
	private const float RotationEpsilon = 0.5f;
	private const float SecureAmountEpsilon = 0.01f;

	private static readonly Dictionary<string, NetworkedPiton> _pitons = new();
	private static ulong _nextLocalId = 1;
	private static GameObject _pitonWorldPrefab;

	public static bool ApplyingRemoteState { get; private set; }

	public static HashSet<int> CaptureExistingHandholds() {
		var ids = new HashSet<int>();
		foreach (var handhold in Object.FindObjectsOfType<CL_Handhold>()) {
			if (handhold != null) {
				ids.Add(handhold.gameObject.GetInstanceID());
			}
		}
		return ids;
	}

	public static void RegisterNewLocalPiton(HandItem_Piton source, HashSet<int> knownHandholds) {
		if (!CanSync() || ApplyingRemoteState || source == null || knownHandholds == null) return;

		var handhold = FindNewPitonHandhold(source, knownHandholds);
		if (handhold == null) return;

		var identity = GetOrCreateIdentity(handhold.gameObject);
		if (string.IsNullOrEmpty(identity.NetworkId)) {
			identity.NetworkId = $"{MPSteamworks.Instance.UserSteamId}:{_nextLocalId++}";
			identity.OwnerId = MPSteamworks.Instance.UserSteamId;
			identity.IsRemote = false;
		}

		_pitons[identity.NetworkId] = identity;
		Broadcast(identity, PitonSyncAction.Create, force: true);
	}

	public static void BroadcastHammerUpdate(CL_Handhold handhold) {
		if (!CanSync() || ApplyingRemoteState || handhold == null) return;
		var identity = handhold.GetComponent<NetworkedPiton>();
		if (identity == null || string.IsNullOrEmpty(identity.NetworkId)) return;

		Broadcast(identity, PitonSyncAction.Update, force: true);
	}

	public static void BroadcastPeriodicUpdate(CL_Handhold handhold) {
		if (!CanSync() || ApplyingRemoteState || handhold == null) return;
		var identity = handhold.GetComponent<NetworkedPiton>();
		if (identity == null || string.IsNullOrEmpty(identity.NetworkId)) return;

		if (!handhold.gameObject.activeSelf) {
			Broadcast(identity, PitonSyncAction.Remove, force: true);
			return;
		}

		if (Time.time - identity.LastSentTime < PeriodicUpdateInterval) return;
		if (!HasMeaningfulStateChange(identity, handhold)) return;

		Broadcast(identity, PitonSyncAction.Update, force: false);
	}

	public static void HandlePitonState(ulong senderId, DataReader reader) {
		var action = (PitonSyncAction)reader.GetByte();
		var networkId = reader.GetString();
		var position = ReadVector3(reader);
		var rotation = ReadQuaternion(reader);
		var secureAmount = reader.GetFloat();
		var secure = reader.GetBool();
		var active = reader.GetBool();

		if (string.IsNullOrEmpty(networkId)) return;

		ApplyingRemoteState = true;
		try {
			switch (action) {
				case PitonSyncAction.Create:
					ApplyCreate(senderId, networkId, position, rotation, secureAmount, secure, active);
					break;
				case PitonSyncAction.Update:
					ApplyUpdate(networkId, position, rotation, secureAmount, secure, active);
					break;
				case PitonSyncAction.Remove:
					ApplyRemove(networkId);
					break;
			}
		} catch (Exception e) {
			MPMain.LogError($"[MP PitonSync] Failed to apply {action} for {networkId}: {e.Message}");
		} finally {
			ApplyingRemoteState = false;
		}
	}

	private static void ApplyCreate(ulong senderId, string networkId, Vector3 position, Quaternion rotation,
									float secureAmount, bool secure, bool active) {
		if (_pitons.TryGetValue(networkId, out var existing) && existing != null) {
			ApplyState(existing, position, rotation, secureAmount, secure, active);
			return;
		}

		var prefab = GetPitonWorldPrefab();
		if (prefab == null) {
			MPMain.LogError("[MP PitonSync] Could not find a piton world prefab.");
			return;
		}

		var pitonObject = Object.Instantiate(prefab, position, rotation);
		var levelRoot = WorldLoader.GetCurrentLevelParentRoot();
		if (levelRoot != null) {
			pitonObject.transform.SetParent(levelRoot);
		}

		TryAddPlacedObjectToLevel(pitonObject);

		var handhold = pitonObject.GetComponent<CL_Handhold>() ?? pitonObject.GetComponentInChildren<CL_Handhold>(true);
		var identity = GetOrCreateIdentity(handhold != null ? handhold.gameObject : pitonObject);
		identity.NetworkId = networkId;
		identity.OwnerId = senderId;
		identity.IsRemote = true;
		_pitons[networkId] = identity;

		ApplyState(identity, position, rotation, secureAmount, secure, active);
	}

	private static void ApplyUpdate(string networkId, Vector3 position, Quaternion rotation,
									float secureAmount, bool secure, bool active) {
		if (!_pitons.TryGetValue(networkId, out var identity) || identity == null) return;
		ApplyState(identity, position, rotation, secureAmount, secure, active);
	}

	private static void ApplyRemove(string networkId) {
		if (!_pitons.TryGetValue(networkId, out var identity) || identity == null) return;
		if (identity.gameObject != null) {
			identity.gameObject.SetActive(false);
		}
		_pitons.Remove(networkId);
	}

	private static void ApplyState(NetworkedPiton identity, Vector3 position, Quaternion rotation,
								   float secureAmount, bool secure, bool active) {
		var transform = identity.transform;
		transform.position = position;
		transform.rotation = rotation;

		var handhold = identity.GetComponent<CL_Handhold>();
		if (handhold != null) {
			handhold.Initialize();
			handhold.secureAmount = secureAmount;
			handhold.secure = secure;
		}

		identity.gameObject.SetActive(active);
		RecordState(identity, handhold);
	}

	private static CL_Handhold FindNewPitonHandhold(HandItem_Piton source, HashSet<int> knownHandholds) {
		var hitPoint = source.GetAimCircleHit().point;
		CL_Handhold best = null;
		var bestDistance = float.MaxValue;

		foreach (var handhold in Object.FindObjectsOfType<CL_Handhold>()) {
			if (handhold == null || knownHandholds.Contains(handhold.gameObject.GetInstanceID())) continue;
			if (!LooksLikePiton(source, handhold.gameObject)) continue;

			var distance = (handhold.transform.position - hitPoint).sqrMagnitude;
			if (distance < bestDistance) {
				best = handhold;
				bestDistance = distance;
			}
		}

		return best;
	}

	private static bool LooksLikePiton(HandItem_Piton source, GameObject obj) {
		if (obj == null) return false;
		if (source != null && source.pitonWorldObject != null) {
			var prefabName = source.pitonWorldObject.name;
			if (!string.IsNullOrEmpty(prefabName) &&
				obj.name.StartsWith(prefabName, StringComparison.OrdinalIgnoreCase)) {
				return true;
			}
		}

		return obj.name.IndexOf("piton", StringComparison.OrdinalIgnoreCase) >= 0;
	}

	private static GameObject GetPitonWorldPrefab() {
		if (_pitonWorldPrefab != null) return _pitonWorldPrefab;

		foreach (var piton in Resources.FindObjectsOfTypeAll<HandItem_Piton>()) {
			if (piton != null && piton.pitonWorldObject != null) {
				_pitonWorldPrefab = piton.pitonWorldObject;
				return _pitonWorldPrefab;
			}
		}

		return null;
	}

	private static void TryAddPlacedObjectToLevel(GameObject pitonObject) {
		if (!WorldLoader.initialized || pitonObject == null) return;

		try {
			var level = WorldLoader.instance.GetCurrentLevel().GetLevel();
			var addPlacedObject = typeof(M_Level).GetMethod(
				"AddPlacedObject",
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			addPlacedObject?.Invoke(level, new object[] { pitonObject });
		} catch (Exception e) {
			MPMain.LogWarning($"[MP PitonSync] Could not register remote piton as placed object: {e.Message}");
		}
	}

	private static NetworkedPiton GetOrCreateIdentity(GameObject obj) {
		var identity = obj.GetComponent<NetworkedPiton>();
		if (identity == null) {
			identity = obj.AddComponent<NetworkedPiton>();
		}
		return identity;
	}

	private static void Broadcast(NetworkedPiton identity, PitonSyncAction action, bool force) {
		if (identity == null || string.IsNullOrEmpty(identity.NetworkId)) return;

		var handhold = identity.GetComponent<CL_Handhold>();
		var writer = GetWriter(MPSteamworks.Instance.UserSteamId, MPProtocol.BroadcastId, PacketType.PitonStateSync);
		writer.Put((byte)action);
		writer.Put(identity.NetworkId);
		WriteVector3(writer, identity.transform.position);
		WriteQuaternion(writer, identity.transform.rotation);
		writer.Put(handhold != null ? handhold.secureAmount : 0f);
		writer.Put(handhold != null && handhold.secure);
		writer.Put(identity.gameObject.activeSelf);

		MPSteamworks.Instance.Broadcast(writer, SendType.Reliable);
		RecordState(identity, handhold);

		if (force) {
			MPMain.LogInfo($"[MP PitonSync] Sent {action} for {identity.NetworkId}");
		}
	}

	private static bool HasMeaningfulStateChange(NetworkedPiton identity, CL_Handhold handhold) {
		if (identity.LastActive != identity.gameObject.activeSelf) return true;
		if ((identity.LastPosition - identity.transform.position).sqrMagnitude > PositionEpsilonSqr) return true;
		if (Quaternion.Angle(identity.LastRotation, identity.transform.rotation) > RotationEpsilon) return true;
		if (handhold == null) return false;
		if (Mathf.Abs(identity.LastSecureAmount - handhold.secureAmount) > SecureAmountEpsilon) return true;
		return identity.LastSecure != handhold.secure;
	}

	private static void RecordState(NetworkedPiton identity, CL_Handhold handhold) {
		identity.LastSentTime = Time.time;
		identity.LastPosition = identity.transform.position;
		identity.LastRotation = identity.transform.rotation;
		identity.LastActive = identity.gameObject.activeSelf;
		if (handhold != null) {
			identity.LastSecureAmount = handhold.secureAmount;
			identity.LastSecure = handhold.secure;
		}
	}

	private static bool CanSync() {
		return MPCore.IsInLobby && MPCore.IsInitialized && MPSteamworks.Instance.HasConnections;
	}

	private static void WriteVector3(DataWriter writer, Vector3 value) {
		writer.Put(value.x);
		writer.Put(value.y);
		writer.Put(value.z);
	}

	private static Vector3 ReadVector3(DataReader reader) {
		return new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
	}

	private static void WriteQuaternion(DataWriter writer, Quaternion value) {
		writer.Put(value.x);
		writer.Put(value.y);
		writer.Put(value.z);
		writer.Put(value.w);
	}

	private static Quaternion ReadQuaternion(DataReader reader) {
		return new Quaternion(reader.GetFloat(), reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
	}
}
