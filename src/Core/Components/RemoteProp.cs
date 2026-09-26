using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using WKMPMod.Asset;
using WKMPMod.Core;
using WKMPMod.Util;

namespace WKMPMod.Components;

public class RemoteProp : CL_Prop, Clickable {
	public RemotePlayer remoteEntity;
	public IDType playerId;
	// 使用 Harmony 的 FieldRef 高效读写基类 CL_Prop 中的私有变量 rigid 和 initialized
	private static readonly AccessTools.FieldRef<CL_Prop, Rigidbody> PropRigidRef =
		AccessTools.FieldRefAccess<CL_Prop, Rigidbody>("rigid");
	private static readonly AccessTools.FieldRef<CL_Prop, bool> PropInitializedRef =
		AccessTools.FieldRefAccess<CL_Prop, bool>("initialized");
	private static readonly AccessTools.FieldRef<CL_Prop, List<Collider>> PropCollidersRef =
		AccessTools.FieldRefAccess<CL_Prop, List<Collider>>("colliders");

	// 负责处理 无敌帧 的重置计时器
	private TickTimer _invincibilityTimer = new TickTimer(0.5f);
	// 负责记录每次 无敌帧并发窗口 的起始物理时间
	private float _burstStartTime = -999f;
	// 是否启用PVP伤害判定, 可通过玩家数据更新
	public bool pvpEnabled = false;

	#region[Unity生命周期函数]

	public override void Start() {
		// 动态获取当前克隆实例上的 Root Rigidbody (绝对不能用预制体母本的)
		Rigidbody rootRigidbody = transform.root.GetComponent<Rigidbody>();
		if (rootRigidbody == null) {
			// 保底方案: 如果顶层没有, 就往父级或自身找
			rootRigidbody = GetComponentInParent<Rigidbody>() ?? GetComponent<Rigidbody>();
		}

		// 核心: 利用 FieldRef 强行将当前实例的物理和碰撞体塞进基类的私有变量中
		PropRigidRef(this) = rootRigidbody;
		PropCollidersRef(this) = new List<Collider>(GetComponentsInChildren<Collider>());

		// 提前标记为已初始化, 双重保险
		PropInitializedRef(this) = true;
		canSave = false;    // 不保存远程实体

		base.Start();
	}

	public override void Update() {}
	#endregion

	#region[CL_Prop重写]

		// 对方受到伤害时调用RemoteEntity结算
	public override bool Damage(Damageable.DamageInfo info) {
		MPMain.LogError("RemoteProp.Damage");
		remoteEntity.Damage(info);
		return false;
	}

	public override void Kill(string type = "", Damageable.DamageInfo damageInfo = null) {
	}

	// 传送实体
	public override void Teleport(Vector3 pos) {
		base.transform.position = pos;
	}

	// 添加力(基础实现)
	public override void AddForce(Vector3 v, string source = "") {
		remoteEntity.AddForce(v,source);
	}

	// 在指定位置添加力
	public override void AddForceAtPosition(Vector3 v, Vector3 p, string source = "") {
		AddForce(v, source);
	}

	// 舌头拉扯
	public override void TonguePull(Vector3 v) {
	}

	#endregion

	#region[Clickable重写]

	/// <summary>
	/// 检查是否可以交互
	/// </summary>
	bool Clickable.CanInteract(Interaction info) {
		return canInteract;
	}

	ObjectTagger Clickable.GetTagger() {
		return gameObject.GetComponent<ObjectTagger>();
	}

	Sprite Clickable.GetSprite() {
		if (MPCore.IsGrabOrHangState == ENT_Player.InteractType.grab)
			return MPAssetManager.grubSprite;
		if (MPCore.IsGrabOrHangState == ENT_Player.InteractType.hanging)
			return MPAssetManager.hangSprite;
		return null;
	}

	#endregion
}
