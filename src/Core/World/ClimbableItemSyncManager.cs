using Newtonsoft.Json.Linq;
using Steamworks.Data;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using UnityEngine;
using WKMPMod.Component;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.NetWork;
using WKMPMod.Patch;
using static WKMPMod.Data.MPWriterPool;
using Object = UnityEngine.Object;

namespace WKMPMod.World;

/// <summary>
/// Piton同步操作类型
/// Piton sync action type
/// </summary>
public enum PitonSyncAction : byte {
	Create = 0,
	Update = 1,
	Remove = 2,
}

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
public static class ClimbableItemSyncManager {
	private const float PeriodicUpdateInterval = 0.15f;
	private const float PositionEpsilonSqr = 0.0004f;
	private const float RotationEpsilon = 0.5f;
	private const float SecureAmountEpsilon = 0.01f;
	private const string CLONE_SUFFIX = "(Clone)";

	// MP Debug
	public static Stopwatch sw = new Stopwatch();

	// 关键: 用来接收捕获到的对象
	private static List<GameObject> CapturedPitons = new List<GameObject>();

	// List.Add 方法的包装, 供 IL 调用,
	// 不需要再传入CapturedPitons到栈顶, 直接在方法内访问静态字段即可
	public static void SaveCapturedPiton(GameObject go) {
		if (go != null)
			if (go.GetComponentInChildren<CL_Handhold>(true) != null) {
				CapturedPitons.Add(go);
			}
	}

	// 已同步的Piton对象表, key为NetworkId
	// Synced piton lookup, keyed by NetworkId
	private static readonly Dictionary<string, NetworkedClimableItem> _pitons = new();

	// 预制体查找表, 用于远程创建时根据名称找到对应Prefab
	// Prefab lookup used to resolve prefabs by name when creating remote objects
	private static readonly Dictionary<string, GameObject> _prefabLookup = new(StringComparer.OrdinalIgnoreCase);

	// Projectile.sourceEntity字段缓存, 用于判断投射物是否属于本地玩家
	// Cached Projectile.sourceEntity field, used to check if a projectile belongs to the local player
	private static readonly FieldInfo _projectileSourceEntityField =
		typeof(Projectile).GetField("sourceEntity", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

	private static ulong _nextLocalId = 1;

	/// <summary>
	/// 是否正在应用远程状态
	/// 用于防止应用远程数据时再次触发本地广播, 造成循环同步
	///
	/// Whether remote state is currently being applied
	/// Used to prevent broadcasting again while applying remote data
	/// </summary>
	public static bool ApplyingRemoteState { get; private set; }

	/// <summary>
	/// 捕获当前场景中已经存在的Handhold根对象ID
	/// 用于之后判断哪个Handhold是新生成的
	///
	/// Captures currently existing handhold root IDs
	/// Used later to detect which handhold was newly spawned
	/// </summary>
	public static HashSet<int> CaptureExistingHandholds() {
		var ids = new HashSet<int>();
		foreach (var handhold in Object.FindObjectsOfType<CL_Handhold>()) {
			var root = GetClimbableRoot(handhold != null ? handhold.gameObject : null);
			if (root != null) {
				ids.Add(root.GetInstanceID());
			}
		}
		return ids;
	}

	/// <summary>
	/// 注册本地新放置的Piton
	/// 根据已知Handhold列表查找新生成的可攀爬物体, 并广播Create消息<br/>
	///
	/// Registers a newly placed local piton
	/// Finds the newly spawned climbable using the known handhold list and broadcasts a Create message
	/// </summary>
	public static void RegisterNewLocalPiton(HandItem_Piton source, HashSet<int> knownHandholds) {
		if (!MPCore.CanSync || ApplyingRemoteState || source == null || knownHandholds == null || source.pitonWorldObject == null) return;

		var prefabKey = CleanCloneName(source.pitonWorldObject.name);
		var root = FindBestNewClimbable(source.GetAimCircleHit().point, knownHandholds, prefabKey, fallback: null);
		RegisterLocalClimbable(root, prefabKey);
	}

	/// <summary>
	/// 注册本地新放置的Piton
	/// 通过修改IL代码, 直接返回HandItem_Piton组件, 并广播Create消息<br/>
	///
	/// Registers a newly placed local piton
	/// Finds the newly spawned climbable using the known handhold list and broadcasts a Create message
	/// </summary>
	public static void RegisterNewLocalPiton(HandItem_Piton source) {
		if (!MPCore.CanSync || ApplyingRemoteState || source == null || CapturedPitons.Count == 0) {
			CapturedPitons.Clear();
			return;
		}

		foreach (var piton in CapturedPitons) {
			var root = GetClimbableRoot(piton);
			if (root == null) continue;
			RegisterLocalClimbable(root, source.pitonWorldObject.name);
		}

		// 清理捕获列表以备下次使用
		CapturedPitons.Clear();
	}

	/// <summary>
	/// 注册本地投射物生成的可攀爬物体
	/// 例如由射击类物品产生的钩点/可攀爬对象
	///
	/// Registers a climbable spawned by a local projectile
	/// For example climbable points created by shoot-type items
	/// </summary>
	public static void RegisterNewLocalProjectileClimbable(Projectile source, RaycastHit hit, HashSet<int> knownHandholds) {
		if (!MPCore.CanSync || ApplyingRemoteState || source == null || knownHandholds == null) return;
		if (!IsLocalProjectile(source)) return;

		var projectileRoot = GetClimbableRoot(source.gameObject);
		var fallback = IsNewClimbableRoot(projectileRoot, knownHandholds) ? projectileRoot : null;
		var root = FindBestNewClimbable(hit.point, knownHandholds, preferredName: null, fallback);
		RegisterLocalClimbable(root, root != null ? root.name : source.gameObject.name);
	}

	/// <summary>
	/// 注册本地投射物生成的可攀爬物体
	/// 例如由射击类物品产生的钩点/可攀爬对象
	///
	/// Registers a climbable spawned by a local projectile
	/// For example climbable points created by shoot-type items
	/// </summary>
	public static void RegisterNewLocalProjectileClimbable(Projectile source, RaycastHit hit) {
		if (!MPCore.CanSync || ApplyingRemoteState || source == null || !IsLocalProjectile(source)) {
			CapturedPitons.Clear();
			return; 
		}

		foreach (var piton in CapturedPitons) {
			var root = GetClimbableRoot(piton);
			if (root == null) continue;
			RegisterLocalClimbable(root, root != null ? root.name : source.gameObject.name);
		}

		CapturedPitons.Clear();
	}

	/// <summary>
	/// 广播锤击/加固后的Piton状态
	///
	/// Broadcasts piton state after hammering/securing
	/// </summary>
	public static void BroadcastHammerUpdate(CL_Handhold handhold) {
		if (!MPCore.CanSync || ApplyingRemoteState || handhold == null) return;

		var identity = FindIdentity(handhold);
		if (identity == null || string.IsNullOrEmpty(identity.NetworkId)) return;

		Broadcast(identity, PitonSyncAction.Update, force: true);
	}

	/// <summary>
	/// 周期性广播Piton状态变化
	/// 只有位置, 旋转, 激活状态或secure状态有明显变化时才发送
	///
	/// Periodically broadcasts piton state changes
	/// Only sends when position, rotation, active state or secure state changed meaningfully
	/// </summary>
	public static void BroadcastPeriodicUpdate(CL_Handhold handhold) {
		if (!MPCore.CanSync || ApplyingRemoteState || handhold == null) return;

		var identity = FindIdentity(handhold);
		if (identity == null || string.IsNullOrEmpty(identity.NetworkId)) return;

		if (!identity.gameObject.activeSelf) {
			Broadcast(identity, PitonSyncAction.Remove, force: true);
			return;
		}

		if (Time.time - identity.LastSentTime < PeriodicUpdateInterval) return;

		var trackedHandhold = GetTrackedHandhold(identity.gameObject);
		if (!HasMeaningfulStateChange(identity, trackedHandhold)) return;

		Broadcast(identity, PitonSyncAction.Update, force: false);
	}

	/// <summary>
	/// 处理收到的Piton同步数据包
	/// 根据Action类型执行创建, 更新或删除
	///
	/// Handles an incoming piton sync packet
	/// Applies create, update or remove depending on the action type
	/// </summary>
	public static void HandlePitonState(ulong senderId, DataReader reader) {
		var action = (PitonSyncAction)reader.GetByte();
		var networkId = reader.GetString();
		var prefabKey = reader.GetString();
		var position = reader.GetVector3();
		var rotation = reader.GetQuaternion();
		var secureAmount = reader.GetFloat();
		var secure = reader.GetBool();
		var active = reader.GetBool();

		if (string.IsNullOrEmpty(networkId)) return;

		ApplyingRemoteState = true;
		try {
			switch (action) {
				case PitonSyncAction.Create:
					ApplyCreate(senderId, networkId, prefabKey, position, rotation, secureAmount, secure, active);
					break;
				case PitonSyncAction.Update:
					ApplyUpdate(networkId, position, rotation, secureAmount, secure, active);
					break;
				case PitonSyncAction.Remove:
					ApplyRemove(networkId);
					break;
			}
		} catch (Exception e) {
			MPMain.LogError($"[MP ClimbableSync] Failed to apply {action} for {networkId}: {e.Message}");
		} finally {
			ApplyingRemoteState = false;
		}
	}

	/// <summary>
	/// 注册本地可攀爬对象并广播Create
	///
	/// Registers a local climbable object and broadcasts Create
	/// </summary>
	private static void RegisterLocalClimbable(GameObject root, string prefabKey) {
		if (root == null) return;

		var normalizedPrefabKey = CleanCloneName(prefabKey);
		if (string.IsNullOrEmpty(normalizedPrefabKey)) return;

		var identity = GetOrCreateIdentity(root);
		if (string.IsNullOrEmpty(identity.NetworkId)) {
			identity.NetworkId = $"{MPSteamworks.UserSteamId}:{_nextLocalId++}";
			identity.OwnerId = MPSteamworks.UserSteamId;
			identity.IsRemote = false;
		}

		identity.PrefabKey = normalizedPrefabKey;
		_pitons[identity.NetworkId] = identity;
		Broadcast(identity, PitonSyncAction.Create, force: true);
	}

	/// <summary>
	/// 应用远程Create消息
	/// 如果对象已存在则更新状态, 否则实例化对应Prefab
	///
	/// Applies a remote Create message
	/// Updates state if the object already exists, otherwise instantiates the matching prefab
	/// </summary>
	private static void ApplyCreate(ulong senderId, string networkId, string prefabKey, Vector3 position, Quaternion rotation,
									float secureAmount, bool secure, bool active) {
		if (_pitons.TryGetValue(networkId, out var existing) && existing != null) {
			existing.PrefabKey = CleanCloneName(prefabKey);
			ApplyState(existing, position, rotation, secureAmount, secure, active);
			return;
		}

		var prefab = ResolvePrefab(prefabKey);
		if (prefab == null) {
			MPMain.LogError($"[MP ClimbableSync] Could not resolve prefab '{prefabKey}' for {networkId}.");
			return;
		}

		var climbableObject = Object.Instantiate(prefab, position, rotation);
		var levelRoot = WorldLoader.GetCurrentLevelParentRoot();
		if (levelRoot != null) {
			climbableObject.transform.SetParent(levelRoot);
		}

		TryAddPlacedObjectToLevel(climbableObject);

		var identity = GetOrCreateIdentity(climbableObject);
		identity.NetworkId = networkId;
		identity.PrefabKey = CleanCloneName(prefabKey);
		identity.OwnerId = senderId;
		identity.IsRemote = true;
		_pitons[networkId] = identity;

		ApplyState(identity, position, rotation, secureAmount, secure, active);
	}

	/// <summary>
	/// 应用远程Update消息
	///
	/// Applies a remote Update message
	/// </summary>
	private static void ApplyUpdate(string networkId, Vector3 position, Quaternion rotation,
									float secureAmount, bool secure, bool active) {
		if (!_pitons.TryGetValue(networkId, out var identity) || identity == null) return;
		ApplyState(identity, position, rotation, secureAmount, secure, active);
	}

	/// <summary>
	/// 应用远程Remove消息
	/// 不直接销毁对象, 而是禁用对象并移除同步记录
	///
	/// Applies a remote Remove message
	/// Does not destroy the object directly; disables it and removes the sync record
	/// </summary>
	private static void ApplyRemove(string networkId) {
		if (!_pitons.TryGetValue(networkId, out var identity) || identity == null) return;
		if (identity.gameObject != null) {
			identity.gameObject.SetActive(false);
		}
		_pitons.Remove(networkId);
	}

	/// <summary>
	/// 将同步状态应用到NetworkedPiton对象
	///
	/// Applies synced state to a NetworkedPiton object
	/// </summary>
	private static void ApplyState(NetworkedClimableItem identity, Vector3 position, Quaternion rotation,
								   float secureAmount, bool secure, bool active) {
		var transform = identity.transform;
		transform.position = position;
		transform.rotation = rotation;

		var handhold = GetTrackedHandhold(identity.gameObject);
		if (handhold != null) {
			handhold.Initialize();
			handhold.secureAmount = secureAmount;
			handhold.secure = secure;
		}

		identity.gameObject.SetActive(active);
		RecordState(identity, handhold);
	}

	/// <summary>
	/// 查找最适合的新生成可攀爬对象
	/// 根据距离, 名称匹配和已知Handhold列表进行筛选<br/>
	///
	/// Finds the best newly spawned climbable object
	/// Filters by distance, name match and known handhold list
	/// </summary>
	private static GameObject FindBestNewClimbable(Vector3 anchor, HashSet<int> knownHandholds, string preferredName, GameObject fallback) {
		GameObject best = null;
		var bestDistance = float.MaxValue;
		var seenRoots = new HashSet<int>();

		if (IsNewClimbableRoot(fallback, knownHandholds) && NameMatches(fallback.name, preferredName)) {
			best = fallback;
			bestDistance = GetAnchorPosition(fallback, anchor);
			seenRoots.Add(fallback.GetInstanceID());
		}

		foreach (var handhold in Object.FindObjectsOfType<CL_Handhold>()) {
			var root = GetClimbableRoot(handhold != null ? handhold.gameObject : null);
			if (!IsNewClimbableRoot(root, knownHandholds)) continue;
			if (!seenRoots.Add(root.GetInstanceID())) continue;
			if (!NameMatches(root.name, preferredName)) continue;

			var distance = GetAnchorPosition(root, anchor);
			if (distance < bestDistance) {
				best = root;
				bestDistance = distance;
			}
		}

		return best;
	}

	/// <summary>
	/// 判断是否为新生成的可攀爬根对象
	/// 根对象必须不在knownHandholds列表中, 并且包含CL_Handhold组件<br/>
	/// Checks whether this is a newly spawned climbable root object
	/// </summary>
	private static bool IsNewClimbableRoot(GameObject root, HashSet<int> knownHandholds) {
		return root != null &&
			   knownHandholds != null &&
			   !knownHandholds.Contains(root.GetInstanceID()) &&
			   ContainsTrackedHandhold(root);
	}

	/// <summary>
	/// 获取可攀爬对象的根节点
	/// 根节点通常是当前Level Root下的直接子对象
	///
	/// Gets the root object for a climbable
	/// The root is usually the direct child below the current level root
	/// </summary>
	private static GameObject GetClimbableRoot(GameObject obj) {
		if (obj == null) return null;

		var levelRoot = WorldLoader.initialized ? WorldLoader.GetCurrentLevelParentRoot() : null;
		var current = obj.transform;
		while (current.parent != null && current.parent != levelRoot) {
			current = current.parent;
		}

		return current.gameObject;
	}

	/// <summary>
	/// 判断根对象中是否包含可追踪的Handhold<br/>
	///
	/// Checks whether the root object contains a tracked handhold
	/// </summary>
	private static bool ContainsTrackedHandhold(GameObject root) {
		return root != null && GetTrackedHandhold(root) != null;
	}

	/// <summary>
	/// 获取根对象上的CL_Handhold组件<br/>
	///
	/// Gets the CL_Handhold component from the root object
	/// </summary>
	private static CL_Handhold GetTrackedHandhold(GameObject root) {
		if (root == null) return null;
		return root.GetComponent<CL_Handhold>() ?? root.GetComponentInChildren<CL_Handhold>(true);
	}

	/// <summary>
	/// 从Handhold查找对应的NetworkedPiton身份组件
	///
	/// Finds the matching NetworkedPiton identity component from a handhold
	/// </summary>
	private static NetworkedClimableItem FindIdentity(CL_Handhold handhold) {
		return handhold.GetComponent<NetworkedClimableItem>() ?? handhold.GetComponentInParent<NetworkedClimableItem>();
	}

	/// <summary>
	/// 获取根对象到锚点的平方距离
	///
	/// Gets the squared distance from the root object to the anchor point
	/// </summary>
	private static float GetAnchorPosition(GameObject root, Vector3 anchor) {
		var trackedHandhold = GetTrackedHandhold(root);
		var target = trackedHandhold != null ? trackedHandhold.transform.position : root.transform.position;
		return (target - anchor).sqrMagnitude;
	}

	/// <summary>
	/// 判断对象名称是否匹配预期Prefab名称<br/>
	///
	/// Checks whether the object name matches the expected prefab name
	/// </summary>
	private static bool NameMatches(string name, string preferredName) {
		if (string.IsNullOrEmpty(preferredName)) return true;

		var normalizedName = CleanCloneName(name);
		var normalizedPreferredName = CleanCloneName(preferredName);
		if (string.IsNullOrEmpty(normalizedName) || string.IsNullOrEmpty(normalizedPreferredName)) return false;

		// 完全匹配或包含关系都算匹配, 忽略大小写
		return normalizedName.Equals(normalizedPreferredName, StringComparison.OrdinalIgnoreCase) ||
			   normalizedName.IndexOf(normalizedPreferredName, StringComparison.OrdinalIgnoreCase) >= 0 ||
			   normalizedPreferredName.IndexOf(normalizedName, StringComparison.OrdinalIgnoreCase) >= 0;
	}

	/// <summary>
	/// 根据PrefabKey解析对应Prefab
	///
	/// Resolves a prefab by prefab key
	/// </summary>
	private static GameObject ResolvePrefab(string prefabKey) {
		var normalizedPrefabKey = CleanCloneName(prefabKey);
		if (string.IsNullOrEmpty(normalizedPrefabKey)) return null;

		if (_prefabLookup.TryGetValue(normalizedPrefabKey, out var prefab) && prefab != null) {
			return prefab;
		}

		RebuildPrefabLookup();
		_prefabLookup.TryGetValue(normalizedPrefabKey, out prefab);
		return prefab;
	}

	/// <summary>
	/// 重建Prefab查找表
	/// 从Piton物品和射击物品中收集可生成的Prefab
	///
	/// Rebuilds the prefab lookup table
	/// Collects spawnable prefabs from piton items and shoot items
	/// </summary>
	private static void RebuildPrefabLookup() {
		_prefabLookup.Clear();

		foreach (var piton in Resources.FindObjectsOfTypeAll<HandItem_Piton>()) {
			if (piton != null) {
				TryRegisterPrefab(piton.pitonWorldObject);
			}
		}

		foreach (var shooter in Resources.FindObjectsOfTypeAll<HandItem_Shoot>()) {
			if (shooter == null) continue;

			TryRegisterPrefab(shooter.projectile);
			RegisterProjectileHitPrefabs(shooter.projectile);
		}
	}

	/// <summary>
	/// 注册投射物命中后可能生成的Prefab
	///
	/// Registers prefabs that may be spawned by projectile hit effects
	/// </summary>
	private static void RegisterProjectileHitPrefabs(GameObject projectilePrefab) {
		if (projectilePrefab == null) return;

		var projectile = projectilePrefab.GetComponent<Projectile>();
		if (projectile == null) return;

		TryRegisterPrefab(projectile.hitEffect);

		if (projectile.customHitEffects == null) return;
		foreach (var hitEffect in projectile.customHitEffects) {
			if (hitEffect == null) continue;
			TryRegisterPrefab(hitEffect.hitEffect);
		}
	}

	/// <summary>
	/// 尝试注册Prefab到查找表<br/>
	///
	/// Tries to register a prefab into the lookup table
	/// </summary>
	private static void TryRegisterPrefab(GameObject prefab) {
		if (prefab == null) return;

		var key = CleanCloneName(prefab.name);
		if (string.IsNullOrEmpty(key) || _prefabLookup.ContainsKey(key)) return;

		_prefabLookup[key] = prefab;
	}

	/// <summary>
	/// 标准化PrefabKey
	/// 去除空格和Unity实例化后附加的(Clone)后缀<br/>
	///
	/// Normalizes a prefab key
	/// Removes whitespace and Unity's instantiated "(Clone)" suffix
	/// </summary>
	private static string CleanCloneName(string prefabKey) {
		if (string.IsNullOrEmpty(prefabKey)) return string.Empty;

		return prefabKey.Replace("(Clone)", string.Empty).Trim();
	}

	/// <summary>
	/// 判断投射物是否由本地玩家发射
	///
	/// Checks whether the projectile was fired by the local player
	/// </summary>
	private static bool IsLocalProjectile(Projectile projectile) {
		var localPlayer = ENT_Player.GetPlayer();
		if (projectile == null || localPlayer == null || _projectileSourceEntityField == null) return false;

		var sourceEntity = _projectileSourceEntityField.GetValue(projectile) as GameEntity;
		return sourceEntity == localPlayer;
	}

	/// <summary>
	/// 将远程生成的可攀爬对象注册到当前关卡的PlacedObject列表
	/// 这样游戏内部系统可以正常识别它
	///
	/// Adds a remotely spawned climbable object to the current level's placed object list
	/// This allows internal game systems to recognize it properly
	/// </summary>
	private static void TryAddPlacedObjectToLevel(GameObject climbableObject) {
		if (!WorldLoader.initialized || climbableObject == null) return;

		try {
			var level = WorldLoader.instance.GetCurrentLevel().GetLevel();
			var addPlacedObject = typeof(M_Level).GetMethod(
				"AddPlacedObject",
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			addPlacedObject?.Invoke(level, new object[] { climbableObject });
		} catch (Exception e) {
			MPMain.LogWarning($"[MP ClimbableSync] Could not register remote climbable as placed object: {e.Message}");
		}
	}

	/// <summary>
	/// 获取或创建NetworkedPiton组件
	///
	/// Gets or creates the NetworkedPiton component
	/// </summary>
	private static NetworkedClimableItem GetOrCreateIdentity(GameObject obj) {
		var identity = obj.GetComponent<NetworkedClimableItem>();
		if (identity == null) {
			identity = obj.AddComponent<NetworkedClimableItem>();
		}
		return identity;
	}

	/// <summary>
	/// 广播Piton同步消息
	/// 消息包含NetworkId, PrefabKey, Transform, secure状态和active状态
	///
	/// Broadcasts a piton sync message
	/// The message includes NetworkId, PrefabKey, transform, secure state and active state
	/// </summary>
	private static void Broadcast(NetworkedClimableItem identity, PitonSyncAction action, bool force) {
		if (identity == null || string.IsNullOrEmpty(identity.NetworkId)) return;

		var handhold = GetTrackedHandhold(identity.gameObject);
		var writer = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.PitonStateSync);
		writer.Put((byte)action);
		writer.Put(identity.NetworkId);
		writer.Put(identity.PrefabKey ?? string.Empty);
		writer.Put(identity.transform.position);
		writer.Put(identity.transform.rotation);
		writer.Put(handhold != null ? handhold.secureAmount : 0f);
		writer.Put(handhold != null && handhold.secure);
		writer.Put(identity.gameObject.activeSelf);

		MPSteamworks.Instance.Broadcast(writer, SendType.Reliable);
		RecordState(identity, handhold);

		if (force) {
			MPMain.LogInfo($"[MP ClimbableSync] Sent {action} for {identity.NetworkId} ({identity.PrefabKey})");
		}
	}

	/// <summary>
	/// 判断状态是否有足够明显的变化, 需要发送同步
	///
	/// Checks whether the state changed enough to require syncing
	/// </summary>
	private static bool HasMeaningfulStateChange(NetworkedClimableItem identity, CL_Handhold handhold) {
		if (identity.LastActive != identity.gameObject.activeSelf) return true;
		if ((identity.LastPosition - identity.transform.position).sqrMagnitude > PositionEpsilonSqr) return true;
		if (Quaternion.Angle(identity.LastRotation, identity.transform.rotation) > RotationEpsilon) return true;
		if (handhold == null) return false;
		if (Mathf.Abs(identity.LastSecureAmount - handhold.secureAmount) > SecureAmountEpsilon) return true;
		return identity.LastSecure != handhold.secure;
	}

	/// <summary>
	/// 记录当前状态为上次同步状态
	///
	/// Records the current state as the last synced state
	/// </summary>
	private static void RecordState(NetworkedClimableItem identity, CL_Handhold handhold) {
		identity.LastSentTime = Time.time;
		identity.LastPosition = identity.transform.position;
		identity.LastRotation = identity.transform.rotation;
		identity.LastActive = identity.gameObject.activeSelf;
		if (handhold != null) {
			identity.LastSecureAmount = handhold.secureAmount;
			identity.LastSecure = handhold.secure;
		}
	}
}