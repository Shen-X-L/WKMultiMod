using HarmonyLib;
using Steamworks.Data;
using System;
using System.Collections.Generic;
using UnityEngine;
using WKMPMod.Asset;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.NetWork;
using WKMPMod.RemotePlayer;
using WKMPMod.Util;
using static Clickable;

namespace WKMPMod.Component;

public class RemoteEntity : CL_Prop {
	public ulong playerId;
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

	public override void Update() {
		
	}

	#endregion
	#region[CL_Prop重写]

	// 对方受到伤害时调用
	public override bool Damage(Damageable.DamageInfo info) {
		// 关闭pvp
		if (!pvpEnabled) return false;

		_invincibilityTimer.SetInterval(MPCore.damageRules.InvincibilityTime);

		// 如果无敌时间已到 (大于 b), 开启新一轮的伤害判定窗口
		if (_invincibilityTimer.IsTickReached) {
			_invincibilityTimer.Reset();   // 重置 TickTimer
			_burstStartTime = Time.time;   // 记录本轮第一发子弹打中的时间
		}
		// 如果还在无敌倒计时内, 但时间处于并发窗口期 (小于 a)
		else if (Time.time - _burstStartTime <= MPCore.damageRules.BurstWindow) {
			// 允许伤害通过! (这是和第一发子弹几乎同时到达的霰弹枪弹丸)
		}
		// 处于 (a, b) 之间, 属于真正的无敌保护期
		else {
			return false; // 免疫伤害
		}

		// 添加屏幕震动
		CL_CameraControl.Shake(0.01f);

		// 计算伤害倍率
		CalculatedDamage(info);

		// 发布到事件总线
		MPEventBusGame.NotifyPlayerDamage(playerId, info);

		// 如果对方正在抓着我, 强制对方放手
		if (LocalPlayer.IsHoldingMe(playerId))
			MPEventBusGame.NotifyPlayerStopInteraction(playerId);

		// 会不会死由对方决定
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
		// 关闭pvp
		if (!pvpEnabled) return;
		// 发送冲击力通知事件
		MPEventBusGame.NotifyPlayerAddForce(playerId, v / 10, source);
	}

	// 在指定位置添加力
	public override void AddForceAtPosition(Vector3 v, Vector3 p, string source = "") {
		AddForce(v, source);
	}

	// 舌头拉扯
	public override void TonguePull(Vector3 v) { 
	}
	#endregion
	#region[工具函数]

	// 计算伤害
	public static void CalculatedDamage(Damageable.DamageInfo info) {
		var baseDamage = info.amount * MPCore.damageRules.All;
		info.amount = info.type switch {
			"Hammer" => baseDamage * MPCore.damageRules.Hammer,
			"Melee" => baseDamage * MPCore.damageRules.Melee,
			"rebar" => baseDamage * MPCore.damageRules.Rebar,
			"returnrebar" => baseDamage * MPCore.damageRules.ReturnRebar,
			"rebarexplosion" => baseDamage * MPCore.damageRules.RebarExplosion,
			"explosion" => baseDamage * MPCore.damageRules.Explosion,
			"piton" => baseDamage * MPCore.damageRules.Piton,
			"flare" => baseDamage * MPCore.damageRules.Flare,
			"ice" => baseDamage * MPCore.damageRules.Ice,
			_ => baseDamage * MPCore.damageRules.Other
		};
		return;
	}

	#endregion
}

/*
锤子		类型:Melee	标签:Melee blunt	 hammer	伤害1-3
自动钻头	类型:piton		伤害3
砖头		类型				伤害3
信号枪	类型:flare	标签:flare incendiary-long	伤害4
钢筋/骨矛		类型:rebar	伤害10
带绳钢筋		类型			伤害10
神器长矛(投出/返回)	类型:returnrebar		标签:returnrebar		伤害10
爆炸钢筋		类型:explosion		标签:explosion	伤害10
			类型:rebarexplosion	标签:rebarexplosion explosion explosive	伤害10 × 3
爆炸钢筋(自伤)	类型:rebarexplosion	标签:rebarexplosion explosion explosive	伤害1
造冰枪(不蓄力/蓄力)	类型:ice		标签:ice			伤害10
					类型			标签:explosion explosive	伤害 0 × 3
造冰枪(自伤)			类型			标签:explosion explosive	伤害 0
 */
[Serializable]
public class DamageRules {
	public float All;
	public float Hammer;
	public float Melee;
	public float Rebar;
	public float ReturnRebar;
	public float RebarExplosion;
	public float Explosion;
	public float Piton;
	public float Flare;
	public float Ice;
	public float Other;
	public float FireTime;
	public float FireDamage;
	// 新增: 并发伤害允许的窗口期 (秒)
	public float BurstWindow;
	// 新增: 受伤后的完整无敌时间 (秒)
	public float InvincibilityTime;
}