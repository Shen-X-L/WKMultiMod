using Steamworks.Data;
using System;
using System.Collections.Generic;
using UnityEngine;
using WKMPMod.Asset;
using WKMPMod.Components;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.NetWork;
using static AIGameEntity;
using static Patch_Projectile;
using static WKMPMod.Data.MPWriterPool;

namespace WKMPMod.World;

/// <summary>
/// 投射物同步操作类型
/// </summary>
public enum ProjectileSyncAction : byte {
	Initialize,			// 投射物创建: 仅普通投射物被HOOK
	Ricochet,			// 投射物弹射
	CollisionHit,		// 投射物命中
	OnDestroy,          // 投射物销毁: 仅非普通投射物有销毁信号
	GrappleInitialize,  // 抓钩创建: 仅抓钩被HOOK
	GrappleState,       // 抓钩状态转移
	GrappleLatched,     // 抓钩锁定: 抓钩钻头和手柄合并, 并转为Climable接管
	RebarState,         // 矛状态转移
	RebarSnapshot,      // 矛位置状态: 矛每帧更新的位置数据
}

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

public class ProjectileSyncModule : Singleton<ProjectileSyncModule>, ISyncModule {

	#region[ISyncModule接口实现]

	public string ModuleName => "ProjectileSync";

	public bool IsEnabled { get; set; } = true;
	public void OnResetMap() {
		// 远程投射物由本模块创建 需要主动销毁
		var remoteProjectiles = new List<Projectile>(_remoteProjectileDic.Values);

		_remoteProjectileDic.Clear();
		_remoteProjectile.Clear();
		_snapshotProjectile.Clear();
		_cleanupKeys.Clear();

		_cleanupCountdown = 0;

		foreach (var projectile in remoteProjectiles) if (projectile != null)
			UnityEngine.Object.Destroy(projectile.gameObject);
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
	/// 正在应用远程投射物状态
	/// 用于避免远程实例初始化时触发本地广播
	/// </summary>
	public bool ApplyingRemoteState { get; private set; }

	/// <summary>
	/// 远程投射物.
	/// Key = (OwnerId, Owner 进程内的 InstanceID)
	/// </summary>
	private readonly Dictionary<NetworkObjectId, Projectile> _remoteProjectileDic = new();

	/// <summary>
	/// 远程投射物.
	///
	/// 这是判断投射物是否属于本地/远程的唯一集合:
	/// - 存在: 本地投射物,允许碰撞检测及广播事件.
	/// - 不存在: 远程投射物,不执行本地碰撞检测.
	/// </summary>
	private readonly HashSet<Projectile> _remoteProjectile = new();

	/// <summary>
	/// 本地中无法靠简单路径模拟可同步的投射物, 需要进行帧更新广播
	/// </summary>
	private readonly HashSet<NetworkedProjectile> _snapshotProjectile = new();

	/// <summary>
	/// 定期清理时复用的 Key 列表.
	/// </summary>
	private readonly List<NetworkObjectId> _cleanupKeys = new();

	/// <summary>
	/// 清理计数, 为0时触发清理, 清理后重置为10, 以避免频繁清理
	/// </summary>
	private byte _cleanupCountdown;

	// 矛更新时间
	private const float REBAR_SNAPSHOT_INTERVAL = 0.05f;
	private float _rebarSnapshotTimer;


	#endregion

	#region[更新]

	public void SyncUpdate(float deltaTime) {
		if (MPCore.CanSync && IsEnabled) {
			// 更新计时判断
			_rebarSnapshotTimer += deltaTime;
			if (_rebarSnapshotTimer >= REBAR_SNAPSHOT_INTERVAL) {
				_rebarSnapshotTimer = 0f;
				// 发送同步数据
				foreach (var identity in _snapshotProjectile) {
					if (identity == null || identity.projectile == null) continue;
					if (identity.CurrentRebarState != RebarSyncState.Flying) {
						BroadcastRebarSync(identity, false);
					}
				}
			}
		}

		if (_remoteProjectileDic.Count == 0 && _remoteProjectile.Count == 0) return;
		if (_cleanupCountdown > 0) {
			_cleanupCountdown--;
			return;
		}
		// 清理已销毁的远程投射物
		_cleanupKeys.Clear();
		foreach (var pair in _remoteProjectileDic) if (pair.Value == null) _cleanupKeys.Add(pair.Key);
		foreach (var key in _cleanupKeys) _remoteProjectileDic.Remove(key);
		// 清理远程投射物
		_remoteProjectile.RemoveWhere(p => p == null);
		// 重设计数
		_cleanupCountdown = 10;
	}

	#endregion

	#region[API]

	/// <summary>
	/// 接收投射物同步数据包
	/// </summary>
	[MPPacketHandler(PacketType.ProjectileSync)]
	public static void HandleProjectileState(IDType senderId, DataReader reader) {
		var module = Instance;
		if (module == null || !module.IsEnabled) return;

		var action = (ProjectileSyncAction)reader.GetByte();
		MPMain.LogDebug(action.ToString());
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
				case ProjectileSyncAction.GrappleInitialize:
					module.HandleGrappleCreate(senderId, reader);
					break;
				case ProjectileSyncAction.OnDestroy:
					module.HandleOnDestroy(senderId, reader);
					break;
				case ProjectileSyncAction.GrappleState:
					module.HandleGrappleState(senderId, reader);
					break;
				case ProjectileSyncAction.GrappleLatched:
					module.HandleGrappleLatched(senderId, reader);
					break;
				case ProjectileSyncAction.RebarState:
				case ProjectileSyncAction.RebarSnapshot:
					module.HandleRebarSync(senderId, reader);
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

	/// 状态机(实际不存在):
	///		Fly: 飞行, 本地进行物理模拟, 通过信号矫正关键坐标
	///			每帧通过 ProjectilePhysics 物理更新, 关闭 CollisionCheck 碰撞检测
	///		Hit: 命中后停止, 并在一定时间后销毁
	/// 信号:
	///		Initialize: 初始化创建投射物
	///		Richochet: 投射物弹射, 矫正坐标
	///		CreateHitEffect: 投射物命中, 触发效果, 矫正坐标, 速度设0, 切换为Hit

	#region[Projectile 一般投射物]

	#region[Hook 事件接入 接口]

	/// <summary>
	/// 本地投射物 Initialize 完成后调用
	/// </summary>
	public void OnInitialize(Projectile projectile) {
		// 空对象 || 对远程投射物操作中 || 未启用模块 || 不需要同步 || 已经是远程投射物
		if (projectile == null || ApplyingRemoteState || !IsEnabled
			|| !MPCore.CanSync || _remoteProjectile.Contains(projectile)) return;

		// 类型派发
		switch (projectile.GetType().Name) {
			case nameof(Projectile_Homing_Rocket):
			case nameof(Projectile_Homing):
				break;
			case nameof(Projectile_Grapple):
				break;
			case nameof(Projectile_ReturnRebar):
				if (!projectile.TryGetComponent<NetworkedProjectile>(out var identity))
					identity = projectile.gameObject.AddComponent<NetworkedProjectile>();
				identity.InitializeLocal();
				_snapshotProjectile.Add(identity);
				BroadcastInitialize(projectile);
				break;
			case nameof(Projectile):
			default:
				BroadcastInitialize(projectile);
				break;
		}
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

	/// <summary>
	/// 反弹回调 本地反弹成功后进行通知
	/// </summary>
	public void OnRichochet(Projectile projectile) {
		if (projectile == null || ApplyingRemoteState || !IsEnabled
			|| !MPCore.CanSync || _remoteProjectile.Contains(projectile)) return;
		BroadcastRichochet(projectile);
	}

	/// <summary>
	/// 碰撞命中回调 本地命中成功后进行通知
	/// </summary>
	public void OnCreateHitEffect(Projectile projectile, ref RaycastHit hit) {
		if (projectile == null || ApplyingRemoteState || !IsEnabled
			|| !MPCore.CanSync || _remoteProjectile.Contains(projectile)) return;
		BroadcastCreateHitEffect(projectile, ref hit);
		if (projectile is Projectile_ReturnRebar rebar 
			&& rebar.TryGetComponent<NetworkedProjectile>(out var identity)) {
			identity.SetRebarState(RebarSyncState.HasHit);
		}
	}

	/// <summary>
	/// 本地投射物销毁
	/// </summary>
	public void OnDestroy(NetworkedProjectile identity) {
		if (identity == null) return;
		_snapshotProjectile.Remove(identity);
		// 远程对象销毁: 只清理本地记录,不广播
		if (identity.IsRemote) {
			_remoteProjectileDic.Remove(identity.NetworkId);
			if (identity.projectile != null) _remoteProjectile.Remove(identity.projectile);
			return;
		}

		if (identity.IsRemote || ApplyingRemoteState || !IsEnabled
			|| !MPCore.CanSync || _remoteProjectile.Contains(identity.projectile)) return;
		BroadcastOnDestroy(identity);
	}

	#endregion

	#region[网络数据发送]

	/// <summary>
	/// 获取投射物初始化数据
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
		var writer = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.ProjectileSync);
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

	/// <summary>
	/// 广播物品销毁
	/// 接收函数: <see cref="HandleOnDestroy"/>
	/// </summary>
	private void BroadcastOnDestroy(NetworkedProjectile identity) {
		var writer = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.ProjectileSync);
		writer.Put((byte)ProjectileSyncAction.OnDestroy);
		writer.Put(identity.InstanceId);
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
		var key = new NetworkObjectId(senderId, data.uniqueId);
		// 防止重复初始化
		if (_remoteProjectileDic.TryGetValue(key, out var existing)) {
			// 对象存在, 重设初始化
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
			var projectile = UnityEngine.Object.Instantiate(template, data.position, data.rotation);
			// 恢复原版物理参数
			projectile.gravity = data.gravity;
			projectile.airResistance = data.airResistance;
			projectile.SetVelocityMultiplier(data.velocityMultiplier);
			projectile.lifetime = data.lifetime;
			// direction 使用零向量,随后直接恢复实际 velocity
			projectile.Initialize(Vector3.zero);
			velocityField(projectile) = data.velocity;
			// SphereCast 的上一帧位置必须与初始位置一致
			projectile.lastPos = data.position;
			_remoteProjectile.Add(projectile);
			_remoteProjectileDic[key] = projectile;

			// Rebar 特化处理
			if (projectile is Projectile_ReturnRebar rebar) {
				var identity = projectile.gameObject.AddComponent<NetworkedProjectile>();
				if (!identity.InitializeRemote(senderId, data.uniqueId)) {
					_remoteProjectile.Remove(projectile);
					_remoteProjectileDic.Remove(key);
					UnityEngine.Object.Destroy(projectile.gameObject);
					return;
				}
				rebar.recoveryRootObject.gameObject.SetActive(value: false);
				rebar.recoveryPrimeRootObject.gameObject.SetActive(value: false);
				if (rebar.transform.Find("Rebar Handhold")?.TryGetComponent<CL_Handhold>(out var handhold) ?? false)
					handhold.active = true;
			}
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
		var key = new NetworkObjectId(senderId, uniqueId);
		if (!_remoteProjectileDic.TryGetValue(key, out var projectile) || projectile == null) return;
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
		var key = new NetworkObjectId(senderId, uniqueId);
		if (!_remoteProjectileDic.TryGetValue(key, out var projectile) || projectile == null) return;
		// 重设数据
		projectile.transform.SetPositionAndRotation(position, rotation);
		projectile.lastPos = position;
		// 让远程实例停止后续物理模拟
		Vector3 tracerVelocity = velocityField(projectile);
		velocityField(projectile) = Vector3.zero;
		hasHitSurfaceField(projectile) = true;
		// 触发事件
		projectile.onHitEvent.Invoke();
		// 没有远程投射物组件(有组件则移交生命周期给OnDestroy) && 命中销毁
		if (!projectile.TryGetComponent<NetworkedProjectile>(out var identity) && destroyOnHit) {
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

	/// <summary>
	/// 投射物销毁
	/// 发送函数: <see cref="BroadcastOnDestroy"/>
	/// </summary>
	private void HandleOnDestroy(IDType senderId, DataReader reader) {
		int uniqueId = reader.GetInt();
		// 查找对应投射物
		var key = new NetworkObjectId(senderId, uniqueId);
		if (!_remoteProjectileDic.TryGetValue(key, out var projectile) || projectile == null) return;
		_remoteProjectileDic.Remove(key);
		if (projectile == null) return;
		_remoteProjectile.Remove(projectile);
		// 销毁投射物
		UnityEngine.Object.Destroy(projectile.gameObject);
	}

	#endregion

	#endregion

	/// 状态机:
	///		Flying: 钻头飞行, 行为类似一般投射物, 手柄留在所有者手部
	///			每帧通过 ProjectilePhysics 物理更新, NetPro.ScaleTetherTo 将手柄留在手部
	///			关闭 CollisionCheck 碰撞检测
	///		HasHit: 钻头命中后停止, 手柄留在所有者手部, 所有者在其本地被拉向钻头
	///		Recalling: 钻头距离到极限, NetPro.GrappleUpdate 进行回收钻头, 手柄留在所有者手部
	///		DropHand: 钻头命中后松手, 组件本身更新让手柄向钻头合并, 合并后latched == true, 切换为Attached
	///		Attached: 钻头和手柄合并结束
	/// 信号:
	///		GrappleCreate: 通过额外数据初始化创建投射物
	///		Richochet: 投射物弹射, 矫正坐标
	///		CreateHitEffect: 投射物命中, 触发效果, 矫正坐标, 速度设0
	///		GrappleRecalling: 状态切换为Recalling
	///		GrappleHasHit: 状态切换为HasHit
	///		GrappleDropHand: 状态切换为DropHand
	///		GrappleLatched: 状态切换为Attached, 通知ClimbableSyncModule将其移交控制
	///		OnDestroy: 钻头回收, 进行销毁

	#region[Grapple 抓钩]

	#region[Hook 事件接入 接口]

	/// <summary>
	/// 替代OnInitialize确定投射物创建
	/// </summary>
	public void OnGrappleCreate(Projectile_Grapple grapple, HandItem_Shoot_Grapple handItem) {
		if (grapple == null || ApplyingRemoteState || !IsEnabled || !MPCore.CanSync) return;
		if (!grapple.TryGetComponent<NetworkedProjectile>(out var identity))
			identity = grapple.gameObject.AddComponent<NetworkedProjectile>();
		identity.InitializeLocal();
		BroadcastGrappleCreate(grapple, (byte)handItem.hand.id);
	}

	/// <summary>
	/// 拉回 广播通知NetworkedProjectile进行拉回到手中
	/// </summary>
	public void OnGrappleRecalling(Projectile_Grapple grapple, HandItem_Shoot_Grapple handItem) {
		if (grapple == null || !IsEnabled || !MPCore.CanSync
			|| !grapple.TryGetComponent<NetworkedProjectile>(out var identity)) return;
		identity.SetGrappleState(GrappleSyncState.Recalling);
	}

	/// <summary>
	/// 命中 广播通知NetworkedProjectile进行命中手柄拖拽钻头停滞
	/// </summary>
	public void OnGrappleHasHit(Projectile_Grapple grapple, HandItem_Shoot_Grapple handItem) {
		if (grapple == null || !IsEnabled || !MPCore.CanSync
			|| !grapple.TryGetComponent<NetworkedProjectile>(out var identity)) return;
		identity.SetGrappleState(GrappleSyncState.HasHit);
	}

	/// <summary>
	/// 命中后松手 广播通知NetworkedProjectile进行命中后松手手柄拖拽
	/// </summary>
	public void OnGrappleDropHand(Projectile_Grapple grapple, HandItem_Shoot_Grapple handItem) {
		if (grapple == null || !IsEnabled || !MPCore.CanSync
			|| !grapple.TryGetComponent<NetworkedProjectile>(out var identity)) return;
		identity.SetGrappleState(GrappleSyncState.DropHand);
	}

	/// <summary>
	/// 钻头和手柄合并完成 广播通知NetworkedProjectile强制合并并移交Climbable模块
	/// </summary>
	public void OnGrappleLatched(Projectile_Grapple grapple) {
		if (grapple == null|| !IsEnabled|| !MPCore.CanSync|| _remoteProjectile.Contains(grapple)
			|| !grapple.TryGetComponent<NetworkedProjectile>(out var identity)|| identity.IsRemote)
			return;
		// 本地更新 Projectile 状态
		identity.SetGrappleState(GrappleSyncState.Attached);
		// 广播 Latch 转换事件
		BroadcastGrappleLatched(identity);
		// 本地将已有对象注册为 Climbable
		ClimbableSyncModule.Instance.RegisterExistingObject(
			grapple.gameObject,identity.NetworkId,isRemote: false);
	}

	#endregion

	#region[网络数据发送]

	private void BroadcastGrappleCreate(Projectile_Grapple grapple, byte handIndex) {
		var data = CaptureProjectileData(grapple);

		var writer = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.ProjectileSync);
		writer.Put((byte)ProjectileSyncAction.GrappleInitialize);
		writer.Put(data);
		writer.Put(handIndex);

		MPSteamworks.Instance.Broadcast(writer, SendType.Reliable);
	}

	public void BroadcastGrappleState(NetworkedProjectile identity, GrappleSyncState state) {
		var projectile = identity.projectile;
		if (projectile == null) return;

		var writer = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.ProjectileSync);

		writer.Put((byte)ProjectileSyncAction.GrappleState);
		writer.Put(identity.InstanceId);
		writer.Put((byte)state);
		writer.Put(projectile.transform.position);
		writer.Put(projectile.transform.rotation);
		writer.Put(velocityField(projectile));

		MPSteamworks.Instance.Broadcast(writer, SendType.Reliable);
	}

	public void BroadcastGrappleLatched(NetworkedProjectile identity) {
		var projectile = identity.projectile;
		if (projectile == null) return;

		var writer = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.ProjectileSync);

		writer.Put((byte)ProjectileSyncAction.GrappleLatched);
		writer.Put(identity.InstanceId);
		writer.Put(projectile.transform.position);
		writer.Put(projectile.transform.rotation);

		MPSteamworks.Instance.Broadcast(writer, SendType.Reliable);
	}

	#endregion

	#region[网络数据处理]

	private void HandleGrappleCreate(IDType senderId, DataReader reader) {
		reader.GetOut<ProjectileData>(out var data);
		byte handIndex = reader.GetByte();
		var key = new NetworkObjectId(senderId, data.uniqueId);
		// 防止重复初始化
		if (_remoteProjectileDic.TryGetValue(key, out var existing)) {
			// 对象存在, 重设初始化
			if (existing != null) return;
			_remoteProjectileDic.Remove(key);
		}
		// 获取预制体
		var prefab = MPAssetManager.GetProjectilePrefab(data.prefabName);
		if (prefab == null || !prefab.TryGetComponent<Projectile_Grapple>(out var template)) {
			MPMain.LogWarning($"[MP ProjectileSync] Projectile prefab not found: {data.prefabName}");
			return;
		}
		// 设置标志位
		bool previousApplyingState = ApplyingRemoteState;
		ApplyingRemoteState = true;
		try {
			var grapple = UnityEngine.Object.Instantiate(template, data.position, data.rotation);
			// 恢复原版物理参数
			grapple.gravity = data.gravity;
			grapple.airResistance = data.airResistance;
			grapple.SetVelocityMultiplier(data.velocityMultiplier);
			grapple.lifetime = data.lifetime;
			// direction 使用零向量,随后直接恢复实际 velocity
			grapple.Initialize(Vector3.zero);
			velocityField(grapple) = data.velocity;
			// SphereCast 的上一帧位置必须与初始位置一致
			grapple.lastPos = data.position;
			_remoteProjectile.Add(grapple);
			_remoteProjectileDic[key] = grapple;
			// 额外处理组件 设置生命周期
			var identity = grapple.gameObject.AddComponent<NetworkedProjectile>();
			if (!identity.InitializeRemote(senderId, data.uniqueId) || !identity.GrappleInitialize(handIndex)) {
				UnityEngine.Object.Destroy(grapple.gameObject);
				return;
			}
		} finally {
			ApplyingRemoteState = previousApplyingState;
		}
	}

	private void HandleGrappleState(IDType senderId, DataReader reader) {
		int uniqueId = reader.GetInt();
		var state = (GrappleSyncState)reader.GetByte();

		Vector3 position = reader.GetVector3();
		Quaternion rotation = reader.GetQuaternion();
		Vector3 velocity = reader.GetVector3();

		var key = new NetworkObjectId(senderId, uniqueId);

		if (!_remoteProjectileDic.TryGetValue(key, out var projectile)|| projectile == null)return;
		if (!projectile.TryGetComponent<NetworkedProjectile>(out var identity))return;

		projectile.transform.SetPositionAndRotation(position, rotation);
		projectile.lastPos = position;
		velocityField(projectile) = velocity;

		identity.ApplyGrappleState(state);
	}

	private void HandleGrappleLatched(IDType senderId,DataReader reader) {
		int uniqueId = reader.GetInt();
		Vector3 position = reader.GetVector3();
		Quaternion rotation = reader.GetQuaternion();

		var key = new NetworkObjectId(senderId, uniqueId);

		if (!_remoteProjectileDic.TryGetValue(key, out var projectile) || projectile == null
			|| !projectile.TryGetComponent<NetworkedProjectile>(out var identity)) return;
		// 校正已有 Projectile 的状态
		projectile.transform.SetPositionAndRotation(position, rotation);
		projectile.lastPos = position;

		identity.ApplyGrappleState(GrappleSyncState.Attached);
		// 将已有 GameObject 注册为 Climbable
		ClimbableSyncModule.Instance.RegisterExistingObject(
			projectile.gameObject,identity.NetworkId,isRemote: true);
	}

	#endregion

	#endregion

	/// 状态机:
	///		Flying: 飞行, 本地进行物理模拟, 通过信号矫正关键坐标
	///			每帧通过 ProjectilePhysics 物理更新, 关闭 CollisionCheck 碰撞检测, 关闭 ReturnUpdate
	///		HasHit: 命中后停止, 设置hasHitSurface, 所有者进行低频坐标更新
	///		Recalling: 回收, 所有者进行坐标更新
	/// 信号:
	///		Initialize: 初始化创建矛
	///		Richochet: 矛弹射, 矫正坐标
	///		CreateHitEffect: 矛命中, 触发效果, 矫正坐标, 速度设0, 切换为HasHit
	///		StartReturn: 矛开始返回, 切换为Recalling, 位置由NetPro组件坐标更新
	///		OnDestroy: 矛回收, 进行销毁

	#region[Rebar 矛]

	#region[Hook 事件接入 接口]

	public bool ReturnUpdate(Projectile_ReturnRebar rebar) {
		// 本地对象继续执行原版 ReturnUpdate
		if (!_remoteProjectile.Contains(rebar)) return true;
		// 远端不执行 Owner 的召回逻辑
		return false;
	}

	public void OnStartReturn(Projectile_ReturnRebar rebar) {
		if (rebar == null || ApplyingRemoteState || !IsEnabled|| !MPCore.CanSync 
			|| _remoteProjectile.Contains(rebar)
			|| !rebar.TryGetComponent<NetworkedProjectile>(out var identity)) return;
		identity.SetRebarState(RebarSyncState.Recalling);
	}

	#endregion

	#region[网络数据发送]

	/// <summary>
	/// 广播 Rebar 状态切换
	/// 接收函数: HandleRebarState
	/// </summary>
	public void BroadcastRebarSync(NetworkedProjectile identity, bool isStateTransition) {
		if (identity == null || identity.IsRemote)return;

		var projectile = identity.projectile;
		if (projectile == null) return;

		uint sequence = identity.NextRebarSequence();
		var action = isStateTransition ? ProjectileSyncAction.RebarState : ProjectileSyncAction.RebarSnapshot;
		var sendType = isStateTransition ? SendType.Reliable : SendType.Unreliable;

		var writer = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.ProjectileSync);
		writer.Put((byte)action);
		writer.Put(identity.InstanceId);
		writer.Put(sequence);
		writer.Put((byte)identity.CurrentRebarState);

		writer.Put(projectile.transform.position);
		writer.Put(projectile.transform.rotation);
		writer.Put(velocityField(projectile));

		MPSteamworks.Instance.Broadcast(writer, sendType);
	}

	#endregion

	#region[网络数据接收]

	private void HandleRebarSync(IDType senderId, DataReader reader) {
		int uniqueId = reader.GetInt();
		uint sequence = reader.GetUInt();
		var state = (RebarSyncState)reader.GetByte();

		Vector3 position = reader.GetVector3();
		Quaternion rotation = reader.GetQuaternion();
		Vector3 velocity = reader.GetVector3();

		var key = new NetworkObjectId(senderId, uniqueId);

		if (!_remoteProjectileDic.TryGetValue(key, out var projectile)|| projectile == null
			|| !projectile.TryGetComponent<NetworkedProjectile>(out var identity)) return;

		identity.ApplyRebarSync(state,sequence,position,rotation,velocity);
	}

	#endregion

	#endregion

}
