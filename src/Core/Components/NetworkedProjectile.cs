using HarmonyLib;
using Steamworks.ServerList;
using System;
using System.Collections.Generic;
using System.Text;
using System.Xml;
using UnityEngine;
using UnityEngine.Rendering;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.RemotePlayers;
using WKMPMod.World;
using static AIGameEntity;

namespace WKMPMod.Components;

public enum ProjectileType { 
	Grapple,// 钩子
	Rebar,	// 矛
	Homing,	// 自追踪
	Boomerang,	// 回旋镖
	Base,   // 基类
	Unknown,// 未知子类
}


public enum ProjectileSyncState : byte {
	Flying,     // 其他: 常规飞行		Grapple: 钻头飞行,手柄停留在手部
	HasHit,     // Rebar: 命中停止	Grapple: 钻头停留,手柄握在手部
	Recalling,  // Rebar|Boomerang: 召回飞行		Grapple: 钻头飞回,手柄停留在手部
	Attached,   // Grapple: 手柄与钻头合并
	DropHand,   // Grapple: 松开手柄,钻头停留,手柄向钻头移动
}

public class NetworkedProjectile : MonoBehaviour {

	#region[静态反射工具]
	public static readonly AccessTools.FieldRef<Projectile_Grapple, bool> latchedField =
		AccessTools.FieldRefAccess<Projectile_Grapple, bool>("latched");

	#endregion

	#region[字段和属性]

	public bool IsRemote { get; private set; }  // 是否是远程对象

	// 引用
	public Projectile projectile { get; private set; }
	private ProjectileType projectileType;
	private RPContainer _container;

	// 身份数据
	public NetworkObjectId NetworkId { get; private set; }
	public IDType OwnerId => NetworkId.OwnerId;
	public int InstanceId => NetworkId.InstanceId;

	public ProjectileSyncState CurrentState { get; private set; } = ProjectileSyncState.Flying;

	private uint _sequence;
	internal uint NextSequence() {
		return ++_sequence;
	}
	private bool IsNewerSequence(uint sequence) {
		return unchecked((int)(sequence - _sequence)) > 0;
	}

	#region[Grapple特化]

	private Transform _handleObject;

	#endregion

	#endregion

	#region[快照变化检测]

	private Vector3 _lastSyncPosition;
	private Quaternion _lastSyncRotation;
	private Vector3 _lastSyncVelocity;

	private const float SNAPSHOT_POS_EPSILON_SQR = 0.0025f;   // 约 0.05m 位置变化阈值平方
	private const float SNAPSHOT_ROT_EPSILON = 1.0f;           // 1 度旋转变化阈值
	private const float SNAPSHOT_VEL_EPSILON_SQR = 0.01f;      // 约 0.1 m/s 速度变化阈值平方

	public bool ShouldBroadcastSnapshot {
		get {
			if (IsRemote || projectile == null) return false;
			return projectileType switch {
				ProjectileType.Rebar => CurrentState != ProjectileSyncState.Flying,
				ProjectileType.Boomerang => CurrentState == ProjectileSyncState.Recalling,
				ProjectileType.Homing => true,
				_ => false
			};
		}
	}

	public bool HasMeaningfulChange {
		get {
			if (projectile == null) return false;

			Vector3 currentPos = projectile.transform.position;
			Quaternion currentRot = projectile.transform.rotation;
			Vector3 currentVel = Patch_Projectile.velocityField(projectile);

			if ((currentPos - _lastSyncPosition).sqrMagnitude > SNAPSHOT_POS_EPSILON_SQR)
				return true;

			if (Quaternion.Angle(currentRot, _lastSyncRotation) > SNAPSHOT_ROT_EPSILON)
				return true;

			if ((currentVel - _lastSyncVelocity).sqrMagnitude > SNAPSHOT_VEL_EPSILON_SQR)
				return true;

			return false;
		}
	}

	public void RememberSyncState() {
		if (projectile == null) return;
		_lastSyncPosition = projectile.transform.position;
		_lastSyncRotation = projectile.transform.rotation;
		_lastSyncVelocity = Patch_Projectile.velocityField(projectile);
	}

	#endregion

	#region[Unity生命周期]

	private void Awake() {
		projectile = GetComponent<Projectile>();
		if (projectile == null) {
			MPMain.LogError("Projectile is null");
			return; 
		}
		projectileType = projectile.GetType().Name switch {
			nameof(Projectile_ReturnRebar) => ProjectileType.Rebar,
			nameof(Projectile_Homing_Rocket) => ProjectileType.Homing,
			nameof(Projectile_Homing) => ProjectileType.Homing,
			nameof(Projectile_Grapple) => ProjectileType.Grapple,
			nameof(Projectile_Boomerang) => ProjectileType.Boomerang,
			nameof(Projectile) => ProjectileType.Base,
			_ => ProjectileType.Unknown,
		};
	}

	/// <summary>
	/// 远程Homing类型进行使用加速度进行 位置 角度 速度插值
	/// 远程Grapple类型对hookHandleObject进行隐藏 仿照HandItem_Shoot_Grapple进行更新 
	/// 手部index获取: 
	///		目前可能要重构? 目前postfix Projectile.Initialize,但HandItem_Shoot没有绑定生成源 
	///		postfix CreateProjectile可行但是敌人生成投射物不经过该函数
	/// 手部Transform获取:
	///		RPManager.Instance.Players.TryGetValue(SendId,out var container)	
	///		container.GetHand(index)
	/// 其余类型暂定
	/// </summary>
	private void Update() {
		if (!IsRemote)return;
		if (projectileType == ProjectileType.Grapple) GrappleUpdate();
	}

	private void OnDestroy() {
		ProjectileSyncModule.Instance.OnDestroy(this);
	}

	#endregion

	#region[初始化行为]

	public void InitializeLocal() {
		IsRemote = false;
		int instanceId = projectile != null ? projectile.gameObject.GetInstanceID() : gameObject.GetInstanceID();
		NetworkId = new NetworkObjectId(instanceId);
	}

	public bool InitializeRemote(IDType ownerId, int uniqueId) {
		return InitializeRemote(new NetworkObjectId(ownerId, uniqueId));
	}

	public bool InitializeRemote(NetworkObjectId objectId) {
		IsRemote = true;
		NetworkId = objectId;
		RPManager.Instance.Players.TryGetValue(objectId.OwnerId, out _container);
		switch (projectileType) {
			case ProjectileType.Rebar:
				return InitializeRebar();
			case ProjectileType.Boomerang:
				return InitializeBoomerang();
			case ProjectileType.Grapple:
				// Grapple 由 GrappleInitialize 处理
				return true;
			default:
				return true;
		}
	}

	private bool InitializeRebar() {
		if (projectile is not Projectile_ReturnRebar rebar)return false;
		rebar.recoveryRootObject?.gameObject.SetActive(false);
		rebar.recoveryPrimeRootObject?.gameObject.SetActive(false);
		if (rebar.transform.Find("Rebar Handhold")
			?.TryGetComponent<CL_Handhold>(out var handhold) ?? false) {
			handhold.active = true;
		}
		return true;
	}

	private bool InitializeBoomerang() {
		if (projectile is not Projectile_Boomerang)return false;
		return true;
	}
	#endregion

	/// <summary>
	/// 本地 Owner 切换 投射物 状态
	/// </summary>
	public void SetState(ProjectileSyncState state) {
		if (IsRemote || !IsValidState(state) || CurrentState == state) return;
		CurrentState = state;

		if (projectileType == ProjectileType.Grapple) 
			ProjectileSyncModule.Instance?.BroadcastGrappleState(this, state);
		else  ProjectileSyncModule.Instance?.BroadcastMotionSync(this, true);
	}

	private bool IsValidState(ProjectileSyncState state) {
		return projectileType switch {
			ProjectileType.Grapple =>
				state == ProjectileSyncState.Flying
				|| state == ProjectileSyncState.HasHit
				|| state == ProjectileSyncState.Recalling
				|| state == ProjectileSyncState.Attached 
				|| state == ProjectileSyncState.DropHand,

			ProjectileType.Rebar =>
				state == ProjectileSyncState.Flying 
				|| state == ProjectileSyncState.HasHit
				|| state == ProjectileSyncState.Recalling,

			ProjectileType.Boomerang =>
				state == ProjectileSyncState.Flying 
				|| state == ProjectileSyncState.Recalling,
			ProjectileType.Homing =>
				state == ProjectileSyncState.Flying,
			_ => false
		};
	}

	public void ApplyMotionSync(ProjectileSyncState state,uint sequence,
		Vector3 position,Quaternion rotation,Vector3 velocity
	) {
		if (!IsRemote || !IsNewerSequence(sequence)) return;
		if (projectile == null) return;
		// 验证状态
		if (!IsValidState(state)) return;
		_sequence = sequence;
		CurrentState = state;
		// 通用位置同步
		projectile.transform.SetPositionAndRotation(position, rotation);
		projectile.lastPos = position;
		Patch_Projectile.velocityField(projectile) = velocity;
		// 类型特化
		switch (projectileType) {
			case ProjectileType.Rebar:
				var rebar = (Projectile_ReturnRebar)projectile;
				bool hasHit = state == ProjectileSyncState.HasHit;
				Patch_Projectile_ReturnRebar.hasHitSurfaceField(rebar) = hasHit;
				if (hasHit) Patch_Projectile.velocityField(projectile) = Vector3.zero;
				break;

			case ProjectileType.Boomerang:
				var boomerang = (Projectile_Boomerang)projectile;
				boomerang.returning = state == ProjectileSyncState.Recalling;
				if (state == ProjectileSyncState.Recalling)
					Patch_Projectile_Boomerang.hasHitSurfaceField(boomerang) = false;
				break;
			case ProjectileType.Homing:
				if (projectile is Projectile_Homing homing && homing.bankingRootTransform != null) {
					Vector3 velDir = velocity.sqrMagnitude > 0.0001f ? velocity.normalized : homing.transform.forward;
					float bankAngle = Mathf.Clamp(
						Vector3.SignedAngle(homing.transform.forward, velDir, Vector3.up) * homing.bankAngleMult,
						-homing.maxBankAngle,
						homing.maxBankAngle
					);
					Quaternion targetRot = Quaternion.Euler(0f, 0f, -bankAngle);
					homing.bankingRootTransform.localRotation = Quaternion.Lerp(
						homing.bankingRootTransform.localRotation,
						targetRot,
						Time.deltaTime * 5f
					);
				}
				break;
		}
	}

	#region[Grapple特化]

	/// <summary>
	/// 状态机行为 
	///		Flying: Projectile.ScaleTetherTo(手部坐标) 手部Transform靠发送者额外发送手部id 
	///		Recalling: 仿照HandItem_Shoot_Grapple拉回逻辑
	///		HasHit: 停止运动, 矫正位置, hookHandleObject在 LateUpdate下自动归位交给ClimbableSyncModule处理
	/// </summary>
	private void GrappleUpdate() {
		if (projectile is not Projectile_Grapple grapple) return;
		if (_handleObject == null) {
			Destroy(gameObject);
			return;
		}
		Vector3 toHand = _handleObject.position - grapple.transform.position;
		switch (CurrentState) {
			case ProjectileSyncState.Flying:
				// 飞行 绳索始终连接到远程玩家的手部
				grapple.ScaleTetherTo(_handleObject.position);
				break;

			case ProjectileSyncState.Recalling:
				Patch_Projectile.hasHitSurfaceField(grapple) = false;
				grapple.hitMask = default;
				// 回拉 朝手部方向移动
				if (toHand.sqrMagnitude < 1f) {
					Patch_Projectile.velocityField(grapple) = Vector3.zero;
					// 已经接近手部, 停止运动, 等待所有者的销毁信号
					break;
				}
				var target = Patch_Projectile.velocityField(grapple);
				Patch_Projectile.velocityField(grapple) = toHand.normalized * target.magnitude;
				// 回拉过程中绳索仍然连接手部
				grapple.ScaleTetherTo(_handleObject.position);
				break;
			case ProjectileSyncState.HasHit:
				Patch_Projectile.velocityField(grapple) = Vector3.zero;
				Patch_Projectile.hasHitSurfaceField(grapple) = true;
				grapple.ScaleTetherTo(_handleObject.position);
				break;
			case ProjectileSyncState.DropHand:
				Patch_Projectile.velocityField(grapple) = Vector3.zero;
				Patch_Projectile.hasHitSurfaceField(grapple) = true;
				if (latchedField(grapple)) {
					transform.localScale = Vector3.one;
					CurrentState = ProjectileSyncState.Attached;
				}
				break;
			case ProjectileSyncState.Attached:
				break;
		}
	}

	public bool GrappleInitialize(byte handIndex) {
		if (projectile is not Projectile_Grapple grapple) return false;
		if (_container == null) return false;
		projectile.destroyOnHit = false;
		projectile.SetLifetime(float.PositiveInfinity);
		_handleObject = _container.GetHand(handIndex)?.transform;
		return _handleObject != null;
	}

	public void ApplyGrappleState(ProjectileSyncState state) {
		MPMain.LogDebug("state: " + state.ToString());
		if (!IsRemote) return;
		if (projectile is not Projectile_Grapple grapple) return;
		CurrentState = state;
		switch (CurrentState) {
			// 合并,所有投射物行为结束
			case ProjectileSyncState.Attached:
				transform.localScale = Vector3.one;
				grapple.hookHandleObject.transform.localPosition = Vector3.zero;
				grapple.hookHandleObject.transform.localRotation = Quaternion.identity;
				latchedField(grapple) = true;
				grapple.hookMesh.gameObject.SetActive(value: false);
				break;
		}
	}

	#endregion

}
