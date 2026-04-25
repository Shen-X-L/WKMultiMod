using System;
using UnityEngine;
using WKMPMod.Asset;
using WKMPMod.Core;
using WKMPMod.Data;

namespace WKMPMod.Component;

public class RemoteEntity : GameEntity {
	private ulong _playerId;
	public ulong PlayerId {
		get => _playerId;
		set {
			_playerId = value;
		}
	}

	public GameObject DamageObject; // 受到伤害时生成的特效对象(如果为null则使用默认对象)

	public float AllActive = 1;
	public float HammerActive = 1;
	public float RebarActive = 1;
	public float ReturnRebarActive = 1;
	public float RebarExplosionActive = 1;
	public float ExplosionActive = 1;
	public float PitonActive = 1;
	public float FlareActive = 1;
	public float IceActive = 1;
	public float OtherActive = 1;

	public override void Start() {
		base.Start();
		canSave = false;    // 不保存远程实体

		if (DamageObject == null) {
			DamageObject = MPAssetManager.GetAssetGameObject(MPAssetManager.DAMAGE_OBJECT_NAME);
		}
	}
	// 对方受到伤害时调用
	public override bool Damage(Damageable.DamageInfo info) {
		// 关闭pvp
		if (!MPCore.IsAllowPVP) return false;

		// 生成伤害特效
		if (DamageObject != null) {
			Instantiate(DamageObject, base.transform.position, base.transform.rotation, base.transform.parent);
		}

		// 添加屏幕震动
		CL_CameraControl.Shake(0.01f);
		// 发布到事件总线
		var baseDamage = info.amount * AllActive;
		float amount;
		switch (info.type) {
			case "Hammer":
				amount = baseDamage * HammerActive;
				break;
			case "rebar":
				amount = baseDamage * RebarActive;
				break;
			case "returnrebar":
				amount = baseDamage * ReturnRebarActive;
				break;
			case "rebarexplosion":
				amount = baseDamage * RebarExplosionActive;
				break;
			case "explosion":
				amount = baseDamage * ExplosionActive;
				break;
			case "piton":
				amount = baseDamage * PitonActive;
				break;
			case "flare":
				amount = baseDamage * FlareActive;
				break;
			case "ice":
				amount = baseDamage * IceActive;
				break;
			default:
				amount = baseDamage * OtherActive;
				break;
		}

		MPEventBusGame.NotifyPlayerDamage(PlayerId, 
			Damageable.DamageInfo.CreateDamageInfo(amount, info.type,info.tags));
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
		MPEventBusGame.NotifyPlayerAddForce(PlayerId, v / 10, source);
	}
}
