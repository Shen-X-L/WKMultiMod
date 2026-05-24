using System;
using UnityEngine;
using WKMPMod.Asset;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.RemotePlayer;

namespace WKMPMod.Component;

public class RemoteEntity : GameEntity {
	public ulong playerId;

	public override void Start() {
		base.Start();
		canSave = false;    // 不保存远程实体
	}
	// 对方受到伤害时调用
	public override bool Damage(Damageable.DamageInfo info) {
		// 关闭pvp
		if (!MPCore.IsAllowPVP) return false;

		// 添加屏幕震动
		CL_CameraControl.Shake(0.01f);

		// 计算伤害倍率
		CalculatedDamage(info);

		// 发布到事件总线
		MPEventBusGame.NotifyPlayerDamage(playerId, info);
		// 会不会死由对方决定
		return false;
	}
	// 传送实体
	public override void Teleport(Vector3 pos) {
		base.transform.position = pos;
	}
	// 添加力(基础实现)
	public override void AddForce(Vector3 v, string source = "") {
		// 关闭pvp
		if (!MPCore.IsAllowPVP) return;
		// 发送冲击力通知事件
		MPEventBusGame.NotifyPlayerAddForce(playerId, v / 10, source);
	}
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
}