using HarmonyLib;
using Steamworks.ServerList;
using System;
using System.Collections.Generic;
using System.Text;
using System.Xml;
using UnityEngine;
using WKMPMod.Core;
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

public enum GrappleSyncState : byte {
	Flying,     // 常规前进:	钻头飞行 手柄停留在手部
	HasHit,     // 命中持握:	钻头停留 手柄握在手部
	Recalling,  // 拉回:		钻头飞回 手柄停留在手部
	Attached,   // 抓取完成:	手柄到达并贴合钻头, 停止运动
	DropHand,   // 命中松手:	钻头停留 手柄向钻头移动
}

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
	public IDType OwnerId { get; private set; }
	public int NetworkId { get; private set; }

	#region[Grapple特化]

	private Transform _handleObject;
	private GrappleSyncState _grappleState = GrappleSyncState.Flying;

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

	public void InitializeLocal(IDType ownerId) {
		IsRemote = false;
		OwnerId = ownerId;
		NetworkId = projectile.gameObject.GetInstanceID();
	}

	public bool InitializeRemote(IDType ownerId, int uniqueId) {
		IsRemote = true;
		OwnerId = ownerId;
		NetworkId = uniqueId;
		RPManager.Instance.Players.TryGetValue(ownerId, out _container);
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
		switch (_grappleState) {
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
					// 已经接近手部，停止运动，等待所有者的销毁 RPC
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
				if (latchedField(grapple)) {
					grapple.onHitEvent.Invoke();
					transform.localScale = Vector3.one;
					_grappleState = GrappleSyncState.Attached;
				}
				grapple.ScaleTetherTo(_handleObject.position);
				break;
			case GrappleSyncState.DropHand:
				Patch_Projectile.velocityField(grapple) = Vector3.zero;
				Patch_Projectile.hasHitSurfaceField(grapple) = true;
				if (latchedField(grapple)) {
					transform.localScale = Vector3.one;
					_grappleState = GrappleSyncState.Attached;
				}
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
		if (_grappleState == state) return;
		_grappleState = state;
		ProjectileSyncModule.Instance?.BroadcastGrappleState(this, state);
	}

	public void ApplyGrappleState(GrappleSyncState state) {
		if (!IsRemote) return;
		_grappleState = state;
	}

	#endregion
}
