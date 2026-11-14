using Steamworks.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Unity.Entities;
using UnityEngine;
using WKMPMod.Asset;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.NetWork;
using WKMPMod.Patch;
using static Patch_Projectile;
using static WKMPMod.Data.MPWriterPool;

namespace WKMPMod.World;

/// <summary>
/// 投射物同步操作类型
/// </summary>
public enum ProjectileSyncAction : byte {
	Initialize,
	Ricochet,
	CollisionHit
}

public class ProjectileSyncModule : Singleton<ProjectileSyncModule>, ISyncModule {

	#region[数据类]

	public struct ProjectileData : INetworkSerializable {
		public int uniqueId;
		public string prefabName;

		public Vector3 position;
		public Quaternion rotation;
		public Vector3 velocity;

		public float gravity;
		public float airResistance;
		public float velocityMultiplier;
		public float lifetime;

		public void Serialize(DataWriter writer) {
			writer.Put(uniqueId);
			writer.Put(prefabName);

			writer.Put(position);
			writer.Put(rotation);
			writer.Put(velocity);

			writer.Put(gravity);
			writer.Put(airResistance);
			writer.Put(velocityMultiplier);
			writer.Put(lifetime);
		}

		public void Deserialize(DataReader reader) {
			uniqueId = reader.GetInt();
			prefabName = reader.GetString();

			position = reader.GetVector3();
			rotation = reader.GetQuaternion();
			velocity = reader.GetVector3();

			gravity = reader.GetFloat();
			airResistance = reader.GetFloat();
			velocityMultiplier = reader.GetFloat();
			lifetime = reader.GetFloat();
		}
	}


	#endregion

	#region[ISyncModule接口实现]

	public string ModuleName => "ProiectileSync";

	public bool IsEnabled { get; set; } = false;
	public void OnResetMap() {
		// 远程投射物由本模块创建 需要主动销毁
		foreach (var projectile in _remoteProjectileDic.Values) {
			if (projectile != null)
				UnityEngine.Object.Destroy(projectile.gameObject);
		}

		_remoteProjectileDic.Clear();
		_remoteProjectile.Clear();
		_cleanupKeys.Clear();

		_cleanupCountdown = 0;
	}
	public void OnLeave() {
		OnResetMap();
	}
	public void OnEnd() {
		OnResetMap();
	}

	#endregion

	#region[字段和属性]

	/// <summary>
	/// 正在应用远程投射物状态。
	/// 用于避免远程实例初始化时触发本地广播
	/// </summary>
	public bool ApplyingRemoteState { get; private set; }

	/// <summary>
	/// 远程投射物.
	/// Key = (OwnerId, Owner 进程内的 InstanceID)
	/// </summary>
	private readonly Dictionary<(IDType ownerId, int uniqueId), Projectile> _remoteProjectileDic = new();

	/// <summary>
	/// 远程投射物.
	///
	/// 这是判断投射物是否属于本地/远程的唯一集合:
	/// - 存在：本地投射物,允许碰撞检测及广播事件.
	/// - 不存在：远程投射物,不执行本地碰撞检测.
	/// </summary>
	private readonly HashSet<Projectile> _remoteProjectile = new();

	/// <summary>
	/// 定期清理时复用的 Key 列表.
	/// </summary>
	private readonly List<(IDType ownerId, int uniqueId)> _cleanupKeys = new();

	/// <summary>
	/// 清理计数, 为0时触发清理, 清理后重置为10, 以避免频繁清理
	/// </summary>
	private byte _cleanupCountdown; 

	#endregion

	#region[更新]

	public void OnSyncUpdate(float deltaTime) {
		if (_remoteProjectileDic.Count == 0 && _remoteProjectile.Count == 0) return;
		if(_cleanupCountdown > 0) {
			_cleanupCountdown--;
			return;
		}
		// 清理已销毁的远程投射物
		_cleanupKeys.Clear();
		foreach (var pair in _remoteProjectileDic) if (pair.Value == null) _cleanupKeys.Add(pair.Key);
		foreach (var key in _cleanupKeys) _remoteProjectileDic.Remove(key);
		// 清理本地投射物
		_remoteProjectile.RemoveWhere(p => p == null);
		// 重设计数
		_cleanupCountdown = 10;
	}

	#endregion

	#region[API]

	/// <summary>
	/// 接收投射物同步数据包。
	/// </summary>
	[MPPacketHandler(PacketType.ProjectileSync)]
	public static void HandleProjectileState(IDType senderId, DataReader reader) {
		var module = Instance;
		if (module == null || !module.IsEnabled)
			return;

		var action = (ProjectileSyncAction)reader.GetByte();

		try {
			switch (action) {
				case ProjectileSyncAction.Initialize:
					module.HandleInitialize(senderId, reader);
					break;

				case ProjectileSyncAction.Ricochet:
					module.HandleRichochet(senderId, reader);
					break;

				case ProjectileSyncAction.CollisionHit:
					module.HandleCreateHitEffect(senderId, reader);
					break;

				default:
					MPMain.LogWarning($"[MP ProjectileSync] Unknown action: {action}");
					break;
			}
		} catch (Exception ex) {
			MPMain.LogError($"Failed to apply {action}: {ex}");
		}
	}

	#endregion

	#region[Hook 事件接入 接口]


	/// <summary>
	/// 本地投射物 Initialize 完成后调用
	/// </summary>
	public void OnInitialize(Projectile projectile) {
		// 空对象 || 对远程投射物操作中 || 未启用模块 || 不需要同步 || 已经是远程投射物
		if (projectile == null || ApplyingRemoteState || !IsEnabled 
			|| !MPCore.CanSync || _remoteProjectile.Contains(projectile)) return;
		BroadcastInitialize(projectile);
	}

	/// <summary>
	/// 判断是否应该执行原版 CollisionCheck
	/// </summary>
	public bool CollisionCheck(Projectile projectile) {
		// 本地投射物
		if (!_remoteProjectile.Contains(projectile)) return true;
		// 非联机情况的远程投射物依然去除碰撞(突然断线)
		return false;
	}

	// 反弹回调 本地反弹成功后进行PRC通知
	public void OnRichochet(Projectile projectile) {
		if (projectile == null || ApplyingRemoteState || !IsEnabled 
			|| !MPCore.CanSync ||  _remoteProjectile.Contains(projectile)) return;
		BroadcastRichochet(projectile);
	}

	// 碰撞命中回调 本地命中成功后进行PRC通知
	public void OnCreateHitEffect(Projectile projectile,ref RaycastHit hit) {
		if (projectile == null || ApplyingRemoteState || !IsEnabled
			|| !MPCore.CanSync || _remoteProjectile.Contains(projectile)) return;
		BroadcastCreateHitEffect(projectile,ref hit);
	}

	#endregion

	#region[网络数据发送]

	/// <summary>
	/// 获取投射物初始化数据。
	/// </summary>
	private ProjectileData CaptureProjectileData(Projectile projectile) {
		return new ProjectileData {
			uniqueId = projectile.gameObject.GetInstanceID(),
			prefabName = projectile.gameObject.name.Replace("(Clone)", string.Empty).Trim(),

			position = projectile.transform.position,
			rotation = projectile.transform.rotation,
			velocity = velocityField(projectile),

			gravity = projectile.gravity,
			airResistance = projectile.airResistance,
			velocityMultiplier = velocityMultiplierField(projectile),
			lifetime = projectile.lifetime
		};
	}

	/// <summary>
	/// 广播投射物初始化
	/// 接收函数: <see cref="HandleInitialize"/>
	/// </summary>
	private void BroadcastInitialize(Projectile projectile) {
		var data = CaptureProjectileData(projectile);
		var writer = GetWriter(MPSteamworks.UserSteamId,MPProtocol.BroadcastId,PacketType.ProjectileSync);
		writer.Put((byte)ProjectileSyncAction.Initialize);
		writer.Put(data);
		MPSteamworks.Instance.Broadcast(writer, SendType.Reliable);
	}

	/// <summary>
	/// 广播反弹后的状态
	/// 接收函数: <see cref="HandleRichochet"/>
	/// </summary>
	private void BroadcastRichochet(Projectile projectile) {
		var writer = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.ProjectileSync);
		writer.Put((byte)ProjectileSyncAction.Ricochet);
		writer.Put(projectile.gameObject.GetInstanceID());
		writer.Put(projectile.transform.position);
		writer.Put(projectile.transform.rotation);
		writer.Put(velocityField(projectile));
		writer.Put(bouncesField(projectile));
		MPSteamworks.Instance.Broadcast(writer, SendType.Reliable);
	}

	/// <summary>
	/// 广播终止碰撞
	/// 接收函数: <see cref="HandleCreateHitEffect"/>
	/// </summary>
	private void BroadcastCreateHitEffect(Projectile projectile, ref RaycastHit hit) {
		var writer = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.ProjectileSync);
		writer.Put((byte)ProjectileSyncAction.CollisionHit);
		writer.Put(projectile.gameObject.GetInstanceID());
		writer.Put(projectile.transform.position);
		writer.Put(projectile.transform.rotation);
		writer.Put(projectile.destroyOnHit);
		writer.Put(hit.point);
		MPSteamworks.Instance.Broadcast(writer, SendType.Reliable);
	}

	#endregion

	#region[网络数据处理]

	/// <summary>
	/// 接收初始化远程投射物
	/// 发送函数: <see cref="BroadcastInitialize"/>
	/// </summary>
	private void HandleInitialize(IDType senderId, DataReader reader) {
		reader.GetOut<ProjectileData>(out var data);
		var key = (senderId, data.uniqueId);
		// 防止重复初始化
		if (_remoteProjectileDic.TryGetValue(key, out var existing)) {
			if (existing != null) return;
			_remoteProjectileDic.Remove(key);
		}
		// 获取预制体
		var prefab = MPAssetManager.GetProjectilePrefab(data.prefabName);
		if (prefab == null || !prefab.TryGetComponent<Projectile>(out var template)) {
			MPMain.LogWarning($"[MP ProjectileSync] Projectile prefab not found: {data.prefabName}");
			return;
		}
		// 设置标志位
		bool previousApplyingState = ApplyingRemoteState;
		ApplyingRemoteState = true;
		try {
			var projectile = UnityEngine.Object.Instantiate(template,data.position,data.rotation);
			// 恢复原版物理参数
			projectile.gravity = data.gravity;
			projectile.airResistance = data.airResistance;
			projectile.SetVelocityMultiplier(data.velocityMultiplier);
			projectile.lifetime = data.lifetime;
			// direction 使用零向量,随后直接恢复实际 velocity
			projectile.Initialize(Vector3.zero);
			velocityField(projectile) = data.velocity;
			// SphereCast 的上一帧位置必须与初始位置一致。
			projectile.lastPos = data.position;
			_remoteProjectile.Add(projectile);
			_remoteProjectileDic[key] = projectile;
		} finally {
			ApplyingRemoteState = previousApplyingState;
		}
	}

	/// <summary>
	/// 接收投射物方向变换, 重设 位置 角度 速度
	/// 发送函数: <see cref="BroadcastRichochet"/>
	/// </summary>
	private void HandleRichochet(IDType senderId, DataReader reader) {
		int uniqueId = reader.GetInt();
		Vector3 position = reader.GetVector3();
		Quaternion rotation = reader.GetQuaternion();
		Vector3 velocity = reader.GetVector3();
		int bounces = reader.GetInt();
		// 查找对应投射物
		var key = (senderId, uniqueId);
		if (!_remoteProjectileDic.TryGetValue(key, out var projectile) ||projectile == null) return;
		// 重设数据
		projectile.transform.SetPositionAndRotation(position, rotation);
		velocityField(projectile) = velocity;
		bouncesField(projectile) = bounces;
		// 避免使用反弹前的位置进行下一次碰撞检测
		projectile.lastPos = position;
	}

	/// <summary>
	/// 接收投射物命中 按照类型来生成命中效果
	/// 发送函数: <see cref="BroadcastCreateHitEffect"/>
	/// </summary>
	private void HandleCreateHitEffect(IDType senderId, DataReader reader) {
		int uniqueId = reader.GetInt();
		Vector3 position = reader.GetVector3();
		Quaternion rotation = reader.GetQuaternion();
		bool destroyOnHit = reader.GetBool();
		Vector3 hitPositon = reader.GetVector3();
		// 查找对应投射物
		var key = (senderId, uniqueId);
		if (!_remoteProjectileDic.TryGetValue(key, out var projectile) ||projectile == null)return;
		// 重设数据
		projectile.transform.SetPositionAndRotation(position, rotation);
		projectile.lastPos = position;
		// 让远程实例停止后续物理模拟
		Vector3 tracerVelocity = velocityField(projectile);
		velocityField(projectile) = Vector3.zero;
		hasHitSurfaceField(projectile) = true;
		if (destroyOnHit) {
			_remoteProjectile.Remove(projectile);
			_remoteProjectileDic.Remove(key);
			UnityEngine.Object.Destroy(projectile.gameObject);
		}
		// 播放曳光弹命中
		if (projectile.useTracer) {
			if (!hasSpawnedTracerField(projectile)) {
				hasSpawnedTracerField(projectile) = true;
				FXManager.PlayTracer(projectile.particleTracerInfo.tracerID, startPosField(projectile), 
						hitPositon, tracerVelocity.magnitude, projectile.particleTracerInfo.hitID);
			} else FXManager.PlayTracer("", startPosField(projectile), hitPositon, 
						tracerVelocity.magnitude, projectile.particleTracerInfo.hitID);
		}
	}

	#endregion
}
