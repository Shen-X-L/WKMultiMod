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

/// <summary>
/// 岩钉同步动作类型枚举
/// </summary>
public enum PitonSyncAction : byte {
	Create = 0,   // 创建岩钉
	Update = 1,   // 更新岩钉状态
	Remove = 2,   // 移除岩钉
}

/// <summary>
/// 岩钉同步管理器, 负责多人游戏中岩钉 (Piton/Handhold) 的网络同步
/// </summary>
public static class PitonSyncManager {
	private const float PeriodicUpdateInterval = 0.15f;      // 周期性更新间隔 (秒) 
	private const float PositionEpsilonSqr = 0.0004f;        // 位置变化阈值平方 (0.02^2) 
	private const float RotationEpsilon = 0.5f;              // 旋转变化阈值 (角度) 
	private const float SecureAmountEpsilon = 0.01f;         // 固定量变化阈值

	private static readonly Dictionary<string, NetworkedPiton> _pitons = new();  // 网络ID到岩钉组件的映射
	private static ulong _nextLocalId = 1;                   // 下一个本地岩钉ID
	private static GameObject _pitonWorldPrefab;             // 岩钉世界预制体

	/// <summary>
	/// 是否正在应用远程状态 (防止循环同步) 
	/// </summary>
	public static bool ApplyingRemoteState { get; private set; }

	/// <summary>
	/// 捕获场景中所有现有的手部抓握点 (Handhold) 的实例ID
	/// </summary>
	/// <returns>手部抓握点实例ID的哈希集合</returns>
	public static HashSet<int> CaptureExistingHandholds() {
		var ids = new HashSet<int>();
		foreach (var handhold in Object.FindObjectsOfType<CL_Handhold>()) {
			if (handhold != null) {
				ids.Add(handhold.gameObject.GetInstanceID());
			}
		}
		return ids;
	}

	/// <summary>
	/// 注册新的本地岩钉, 将其同步到网络
	/// </summary>
	/// <param name="source">岩钉物品源</param>
	/// <param name="knownHandholds">已知的手部抓握点ID集合, 用于排除已存在的抓握点</param>
	public static void RegisterNewLocalPiton(HandItem_Piton source, HashSet<int> knownHandholds) {
		if (!MPCore.CanSync() || ApplyingRemoteState || source == null || knownHandholds == null) return;

		// 查找新生成的岩钉对应的手部抓握点
		var handhold = FindNewPitonHandhold(source, knownHandholds);
		if (handhold == null) return;

		// 获取或创建网络标识组件
		var identity = GetOrCreateIdentity(handhold.gameObject);
		if (string.IsNullOrEmpty(identity.NetworkId)) {
			identity.NetworkId = $"{MPSteamworks.Instance.UserSteamId}:{_nextLocalId++}";
			identity.OwnerId = MPSteamworks.Instance.UserSteamId;
			identity.IsRemote = false;
		}

		_pitons[identity.NetworkId] = identity;
		Broadcast(identity, PitonSyncAction.Create, force: true);
	}

	/// <summary>
	/// 广播岩钉被锤子敲击的更新
	/// </summary>
	/// <param name="handhold">被更新的手部抓握点</param>
	public static void BroadcastHammerUpdate(CL_Handhold handhold) {
		if (!MPCore.CanSync() || ApplyingRemoteState || handhold == null) return;
		var identity = handhold.GetComponent<NetworkedPiton>();
		if (identity == null || string.IsNullOrEmpty(identity.NetworkId)) return;

		Broadcast(identity, PitonSyncAction.Update, force: true);
	}

	/// <summary>
	/// 广播周期性状态更新 (如果状态有显著变化) 
	/// </summary>
	/// <param name="handhold">要检查的手部抓握点</param>
	public static void BroadcastPeriodicUpdate(CL_Handhold handhold) {
		if (!MPCore.CanSync() || ApplyingRemoteState || handhold == null) return;
		var identity = handhold.GetComponent<NetworkedPiton>();
		if (identity == null || string.IsNullOrEmpty(identity.NetworkId)) return;

		// 如果物体已禁用, 发送移除消息
		if (!handhold.gameObject.activeSelf) {
			Broadcast(identity, PitonSyncAction.Remove, force: true);
			return;
		}

		// 检查更新间隔
		if (Time.time - identity.LastSentTime < PeriodicUpdateInterval) return;
		if (!HasMeaningfulStateChange(identity, handhold)) return;

		Broadcast(identity, PitonSyncAction.Update, force: false);
	}

	/// <summary>
	/// 处理接收到的岩钉状态同步数据包
	/// </summary>
	/// <param name="senderId">发送者Steam ID</param>
	/// <param name="reader">数据读取器</param>
	public static void HandlePitonState(ulong senderId, DataReader reader) {
		var action = (PitonSyncAction)reader.GetByte();
		var networkId = reader.GetString();
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

	/// <summary>
	/// 应用创建操作：在本地创建远程玩家的岩钉
	/// </summary>
	private static void ApplyCreate(ulong senderId, string networkId, Vector3 position, Quaternion rotation,
									float secureAmount, bool secure, bool active) {
		// 如果岩钉已存在, 仅更新状态
		if (_pitons.TryGetValue(networkId, out var existing) && existing != null) {
			ApplyState(existing, position, rotation, secureAmount, secure, active);
			return;
		}

		// 获取岩钉预制体
		var prefab = GetPitonWorldPrefab();
		if (prefab == null) {
			MPMain.LogError("[MP PitonSync] Could not find a piton world prefab.");
			return;
		}

		// 实例化岩钉对象
		var pitonObject = Object.Instantiate(prefab, position, rotation);
		var levelRoot = WorldLoader.GetCurrentLevelParentRoot();
		if (levelRoot != null) {
			pitonObject.transform.SetParent(levelRoot);
		}

		// 尝试将岩钉注册到当前关卡
		TryAddPlacedObjectToLevel(pitonObject);

		// 获取手部抓握点组件并设置网络标识
		var handhold = pitonObject.GetComponent<CL_Handhold>() ?? pitonObject.GetComponentInChildren<CL_Handhold>(true);
		var identity = GetOrCreateIdentity(handhold != null ? handhold.gameObject : pitonObject);
		identity.NetworkId = networkId;
		identity.OwnerId = senderId;
		identity.IsRemote = true;
		_pitons[networkId] = identity;

		ApplyState(identity, position, rotation, secureAmount, secure, active);
	}

	/// <summary>
	/// 应用更新操作：更新本地岩钉的状态
	/// </summary>
	private static void ApplyUpdate(string networkId, Vector3 position, Quaternion rotation,
									float secureAmount, bool secure, bool active) {
		if (!_pitons.TryGetValue(networkId, out var identity) || identity == null) return;
		ApplyState(identity, position, rotation, secureAmount, secure, active);
	}

	/// <summary>
	/// 应用移除操作：禁用并移除本地岩钉
	/// </summary>
	private static void ApplyRemove(string networkId) {
		if (!_pitons.TryGetValue(networkId, out var identity) || identity == null) return;
		if (identity.gameObject != null) {
			identity.gameObject.SetActive(false);
		}
		_pitons.Remove(networkId);
	}

	/// <summary>
	/// 应用岩钉状态到本地对象
	/// </summary>
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

	/// <summary>
	/// 查找新创建的岩钉对应的手部抓握点
	/// </summary>
	/// <param name="source">岩钉物品源</param>
	/// <param name="knownHandholds">已知抓握点ID集合</param>
	/// <returns>找到的手部抓握点, 未找到则返回null</returns>
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

	/// <summary>
	/// 检查游戏对象是否看起来像是岩钉
	/// </summary>
	/// <param name="source">岩钉物品源</param>
	/// <param name="obj">要检查的游戏对象</param>
	/// <returns>如果是岩钉则返回true</returns>
	private static bool LooksLikePiton(HandItem_Piton source, GameObject obj) {
		if (obj == null) return false;

		// 检查是否匹配源物品的预制体名称
		if (source != null && source.pitonWorldObject != null) {
			var prefabName = source.pitonWorldObject.name;
			if (!string.IsNullOrEmpty(prefabName) &&
				obj.name.StartsWith(prefabName, StringComparison.OrdinalIgnoreCase)) {
				return true;
			}
		}

		// 回退检查：名称中包含 "piton"
		return obj.name.IndexOf("piton", StringComparison.OrdinalIgnoreCase) >= 0;
	}

	/// <summary>
	/// 获取岩钉世界预制体
	/// </summary>
	/// <returns>岩钉预制体, 未找到则返回null</returns>
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

	/// <summary>
	/// 尝试将岩钉对象添加到当前关卡的放置对象列表中
	/// </summary>
	/// <param name="pitonObject">岩钉游戏对象</param>
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

	/// <summary>
	/// 获取或创建游戏对象的网络标识组件
	/// </summary>
	/// <param name="obj">目标游戏对象</param>
	/// <returns>网络标识组件</returns>
	private static NetworkedPiton GetOrCreateIdentity(GameObject obj) {
		var identity = obj.GetComponent<NetworkedPiton>();
		if (identity == null) {
			identity = obj.AddComponent<NetworkedPiton>();
		}
		return identity;
	}

	/// <summary>
	/// 广播岩钉状态到所有客户端
	/// </summary>
	/// <param name="identity">岩钉网络标识</param>
	/// <param name="action">同步动作类型</param>
	/// <param name="force">是否强制广播 (记录日志) </param>
	private static void Broadcast(NetworkedPiton identity, PitonSyncAction action, bool force) {
		if (identity == null || string.IsNullOrEmpty(identity.NetworkId)) return;

		var handhold = identity.GetComponent<CL_Handhold>();
		var writer = GetWriter(MPSteamworks.Instance.UserSteamId, MPProtocol.BroadcastId, PacketType.PitonStateSync);
		writer.Put((byte)action);
		writer.Put(identity.NetworkId);
		writer.Put(identity.transform.position);
		writer.Put(identity.transform.rotation);
		writer.Put(handhold != null ? handhold.secureAmount : 0f);
		writer.Put(handhold != null && handhold.secure);
		writer.Put(identity.gameObject.activeSelf);

		MPSteamworks.Instance.Broadcast(writer, SendType.Reliable);
		RecordState(identity, handhold);

		if (force) {
			MPMain.LogInfo($"[MP PitonSync] Sent {action} for {identity.NetworkId}");
		}
	}

	/// <summary>
	/// 检查岩钉状态是否有显著变化 (需要同步) 
	/// </summary>
	/// <param name="identity">岩钉网络标识</param>
	/// <param name="handhold">手部抓握点</param>
	/// <returns>如果有显著变化则返回true</returns>
	private static bool HasMeaningfulStateChange(NetworkedPiton identity, CL_Handhold handhold) {
		if (identity.LastActive != identity.gameObject.activeSelf) return true;
		if ((identity.LastPosition - identity.transform.position).sqrMagnitude > PositionEpsilonSqr) return true;
		if (Quaternion.Angle(identity.LastRotation, identity.transform.rotation) > RotationEpsilon) return true;
		if (handhold == null) return false;
		if (Mathf.Abs(identity.LastSecureAmount - handhold.secureAmount) > SecureAmountEpsilon) return true;
		return identity.LastSecure != handhold.secure;
	}

	/// <summary>
	/// 记录岩钉当前状态, 用于下次变化检测
	/// </summary>
	/// <param name="identity">岩钉网络标识</param>
	/// <param name="handhold">手部抓握点</param>
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

}
