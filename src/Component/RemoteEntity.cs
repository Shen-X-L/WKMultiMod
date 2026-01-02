using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using WKMultiMod.src.Core;
using WKMultiMod.src.Util;
using static Steamworks.InventoryItem;

namespace WKMultiMod.src.Component;

public class RemoteEntity : GameEntity {
	public ulong PlayerId;
	// 固定更新逻辑
	public override void TickUpdate() {
	}
	// 受到伤害时调用
	public override bool Damage(float amount, string type) {
		//MPMain.Logger.LogInfo($"伤害{amount.ToString()} 类型{type}");
		var baseDamage = amount * MPConfig.AllActive;
		switch (type) {
			case "Hammer":
				MPEventBus.Game.NotifyPlayerDamage(PlayerId, baseDamage * MPConfig.HammerActive, type);
				break;
			case "rebar":
				MPEventBus.Game.NotifyPlayerDamage(PlayerId, baseDamage * MPConfig.RebarActive, type);
				break;
			case "returnrebar":
				MPEventBus.Game.NotifyPlayerDamage(PlayerId, baseDamage * MPConfig.ReturnRebarActive, type);
				break;
			case "rebarexplosion":
				MPEventBus.Game.NotifyPlayerDamage(PlayerId, baseDamage * MPConfig.RebarExplosionActive, type);
				break;
			case "explosion":
				MPEventBus.Game.NotifyPlayerDamage(PlayerId, baseDamage * MPConfig.RebarExplosionActive, type);
				break;
			case "piton":
				MPEventBus.Game.NotifyPlayerDamage(PlayerId, baseDamage * MPConfig.PitonActive, type);
				break;
			case "flare":
				MPEventBus.Game.NotifyPlayerDamage(PlayerId, baseDamage * MPConfig.FlareActive, type);
				break;
			case "ice":
				MPEventBus.Game.NotifyPlayerDamage(PlayerId, baseDamage * MPConfig.IceActive, type);
				break;
			default:
				MPEventBus.Game.NotifyPlayerDamage(PlayerId, baseDamage * MPConfig.OtherActive, type);
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
		MPEventBus.Game.NotifyPlayerAddForce(PlayerId, v / 10, source);
	}
}
