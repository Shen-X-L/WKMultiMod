using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace WKMPMod.Data;

public static class MPEventBusGame {
	// 游戏事件: 广播位置
	public static event Action<PlayerData> OnPlayerMove;
	public static void NotifyPlayerMove(PlayerData playerData) => OnPlayerMove?.Invoke(playerData);

	// 游戏组件事件: 收到攻击
	public static event Action<ulong, float, string> OnPlayerDamage;
	public static void NotifyPlayerDamage(ulong steamId, float amount, string type)
		=> OnPlayerDamage?.Invoke(steamId, amount, type);

	// 游戏组件事件: 受到冲击力
	public static event Action<ulong, Vector3, string> OnPlayerAddForce;
	public static void NotifyPlayerAddForce(ulong steamId, Vector3 force, string source)
		=> OnPlayerAddForce?.Invoke(steamId, force, source);

	// 游戏事件: 玩家死亡
	public static event Action OnPlayerDeath;
	public static void NotifyPlayerDeath() => OnPlayerDeath?.Invoke();
}
