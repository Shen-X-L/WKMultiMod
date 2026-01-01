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
		// 发送伤害通知事件
		MPEventBus.Game.NotifyPlayerDamage(PlayerId, amount/5, type);
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
