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
	Base,   // 基类
	Unknown,// 未知子类
}

#region[状态机状态枚举]

public enum GrappleSyncState : byte {
	Flying,     // 常规前进:	钻头飞行 手柄停留在手部
	HasHit,     // 命中持握:	钻头停留 手柄握在手部
	Recalling,  // 拉回:		钻头飞回 手柄停留在手部
	Attached,   // 抓取完成:	手柄到达并贴合钻头, 停止运动
	DropHand,   // 命中松手:	钻头停留 手柄向钻头移动
}

public enum RebarSyncState : byte {
	Flying,     // 正常飞行
	HasHit,     // 命中并固定
	Recalling,  // 召回飞行
}

#endregion

/// <summary>
/// 
/// </summary>
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

	#region[Grapple特化]

	private Transform _handleObject;
	public GrappleSyncState CurrentGrappleState { get; private set; } = GrappleSyncState.Flying;

	#endregion

	#endregion

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
		return true;
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
		switch (CurrentGrappleState) {
			case GrappleSyncState.Flying:
				// 飞行 绳索始终连接到远程玩家的手部
				grapple.ScaleTetherTo(_handleObject.position);
				break;

			case GrappleSyncState.Recalling:
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
			case GrappleSyncState.HasHit:
				Patch_Projectile.velocityField(grapple) = Vector3.zero;
				Patch_Projectile.hasHitSurfaceField(grapple) = true;
				grapple.ScaleTetherTo(_handleObject.position);
				break;
			case GrappleSyncState.DropHand:
				Patch_Projectile.velocityField(grapple) = Vector3.zero;
				Patch_Projectile.hasHitSurfaceField(grapple) = true;
				if (latchedField(grapple)) {
					transform.localScale = Vector3.one;
					CurrentGrappleState = GrappleSyncState.Attached;
				}
				break;
			case GrappleSyncState.Attached:
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

	public void SetGrappleState(GrappleSyncState state, float recallSpeed = 0f) {
		if (IsRemote || projectileType != ProjectileType.Grapple) return;
		if (CurrentGrappleState == state) return;
		CurrentGrappleState = state;
		ProjectileSyncModule.Instance?.BroadcastGrappleState(this, state);
	}

	public void ApplyGrappleState(GrappleSyncState state) {
		MPMain.LogDebug("state: " + state.ToString());
		if (!IsRemote) return;
		if (projectile is not Projectile_Grapple grapple) return;
		CurrentGrappleState = state;
		switch (CurrentGrappleState) {
			case GrappleSyncState.Attached:
				transform.localScale = Vector3.one;
				grapple.hookHandleObject.transform.localPosition = Vector3.zero;
				grapple.hookHandleObject.transform.localRotation = Quaternion.identity;
				latchedField(grapple) = true;
				grapple.hookMesh.gameObject.SetActive(value: false);
				break;
		}
	}

	#endregion

	#region[Rebar特化]

	public RebarSyncState CurrentRebarState { get; private set; } = RebarSyncState.Flying;
	// 网络包序号
	private uint _rebarSequence;
	internal uint NextRebarSequence() {return ++_rebarSequence;}

	private bool IsNewerRebarSequence(uint sequence) {
		// 支持 uint 溢出后的序号比较
		return sequence - _rebarSequence > 0;
	}

	/// <summary>
	/// 本地 Owner 切换 Rebar 状态
	/// </summary>
	public void SetRebarState(RebarSyncState state) {
		if (IsRemote) return;
		if (CurrentRebarState == state) return;
		CurrentRebarState = state;
		ProjectileSyncModule.Instance?.BroadcastRebarSync(this,true);
	}

	/// <summary>
	/// 远端应用 Rebar 状态 或 快照
	/// </summary>
	public void ApplyRebarSync(RebarSyncState state, uint sequence,
		Vector3 position, Quaternion rotation, Vector3 velocity
	) {
		if (!IsRemote) return;
		if (!IsNewerRebarSequence(sequence)) return;

		_rebarSequence = sequence;
		CurrentRebarState = state;

		ApplyRebarTransform(position, rotation, velocity);

		if(projectile is Projectile_ReturnRebar rebar){
			switch (state) {
				case RebarSyncState.Flying:
				case RebarSyncState.Recalling:
					Patch_Projectile_ReturnRebar.hasHitSurfaceField(rebar) = false;
					break;
				case RebarSyncState.HasHit:
					Patch_Projectile_ReturnRebar.hasHitSurfaceField(rebar) = true;
					Patch_Projectile.velocityField(projectile) = Vector3.zero;
					break;
			}
		}
	}

	/// <summary>
	/// 应用远端位置、旋转、速度
	/// </summary>
	private void ApplyRebarTransform(Vector3 position, Quaternion rotation, Vector3 velocity) {
		if (projectile == null) return;

		projectile.transform.SetPositionAndRotation(position, rotation);
		projectile.lastPos = position;

		Patch_Projectile.velocityField(projectile) = velocity;

		// 命中状态不允许残留速度
		if (CurrentRebarState == RebarSyncState.HasHit)
			Patch_Projectile.velocityField(projectile) = Vector3.zero;
	}

	#endregion

}
