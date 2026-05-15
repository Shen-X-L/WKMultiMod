using System;
using System.Threading.Tasks;
using UnityEngine;
using WKMPMod.Component;
using WKMPMod.Core;

namespace WKMPMod.Data;

public static class MPEventBusGame {
	// 游戏事件: 广播位置
	public static event Action<PlayerData> OnPlayerMove;
	public static void NotifyPlayerMove(PlayerData playerData) => OnPlayerMove?.Invoke(playerData);

	/// <summary>
	/// 游戏组件事件: 收到攻击 订阅者<see cref="MPCore.HandlePlayerDamage">
	/// </summary>
	public static event Action<ulong, Damageable.DamageInfo> OnPlayerDamage;
	/// <summary>
	/// 调用者 <see cref="RemoteEntity.Damage">
	/// </summary>
	public static void NotifyPlayerDamage(ulong steamId, Damageable.DamageInfo info)
		=> OnPlayerDamage?.Invoke(steamId, info);

	// 游戏组件事件: 受到冲击力
	public static event Action<ulong, Vector3, string> OnPlayerAddForce;
	public static void NotifyPlayerAddForce(ulong steamId, Vector3 force, string source)
		=> OnPlayerAddForce?.Invoke(steamId, force, source);

	// 游戏事件: 玩家死亡
	public static event Action<string> OnPlayerDeath;
	public static void NotifyPlayerDeath(string type) => OnPlayerDeath?.Invoke(type);

	// 游戏事件: 玩家胜利
	public static event Action OnPlayerWin;
	public static void NotifyPlayerWin() => OnPlayerWin?.Invoke();


	// UI事件: 显示加载界面
	public static event Action<float> OnShowLoading;
	public static void NotifyShowLoading(float duration) => OnShowLoading?.Invoke(duration);

	// UI事件: 隐藏加载界面
	public static event Action OnHideLoading;
	public static void NotifyHideLoading() => OnHideLoading?.Invoke();

	// UI事件: 刷新大厅列表
	public static event Func<Task> OnRefreshLobbyList;
	public static void NotifyRefreshLobbyList() => OnRefreshLobbyList?.Invoke();
}
