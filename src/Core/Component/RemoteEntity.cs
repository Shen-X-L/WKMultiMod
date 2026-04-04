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
	public override bool Damage(float amount, string type) {
		// 生成伤害特效
		if (DamageObject != null) {
			UnityEngine.Object.Instantiate(DamageObject, base.transform.position, base.transform.rotation, base.transform.parent);
		}

		// 添加屏幕震动
		CL_CameraControl.Shake(0.01f);
		// 发布到事件总线
		var baseDamage = amount * AllActive;
		switch (type) {
			case "Hammer":
				MPEventBusGame.NotifyPlayerDamage(PlayerId, baseDamage * HammerActive, type);
				break;
			case "rebar":
				MPEventBusGame.NotifyPlayerDamage(PlayerId, baseDamage * RebarActive, type);
				break;
			case "returnrebar":
				MPEventBusGame.NotifyPlayerDamage(PlayerId, baseDamage * ReturnRebarActive, type);
				break;
			case "rebarexplosion":
				MPEventBusGame.NotifyPlayerDamage(PlayerId, baseDamage * RebarExplosionActive, type);
				break;
			case "explosion":
				MPEventBusGame.NotifyPlayerDamage(PlayerId, baseDamage * ExplosionActive, type);
				break;
			case "piton":
				MPEventBusGame.NotifyPlayerDamage(PlayerId, baseDamage * PitonActive, type);
				break;
			case "flare":
				MPEventBusGame.NotifyPlayerDamage(PlayerId, baseDamage * FlareActive, type);
				break;
			case "ice":
				MPEventBusGame.NotifyPlayerDamage(PlayerId, baseDamage * IceActive, type);
				break;
			default:
				MPEventBusGame.NotifyPlayerDamage(PlayerId, baseDamage * OtherActive, type);
				break;
		}
		// 会不会死由对方决定
		return false;
	}
	// 传送实体
	public override void Teleport(Vector3 pos) {
		base.transform.position = pos;
	}
	// 添加力(基础实现)
	public override void AddForce(Vector3 v, string source = "") {
		// 发送冲击力通知事件
		MPEventBusGame.NotifyPlayerAddForce(PlayerId, v / 10, source);
	}
}
