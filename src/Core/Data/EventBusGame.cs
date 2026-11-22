using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using WKMPMod.Components;
using WKMPMod.Core;
using WKMPMod.RemotePlayers;
using static WorldLoader;

namespace WKMPMod.Data;

public static class EventBusGame {
	#region[游戏事件]

	// 游戏事件: 广播位置
	public static event Action<PlayerData> OnPlayerMove;
	public static void NotifyPlayerMove(PlayerData playerData) => OnPlayerMove(playerData);

	/// <summary>
	/// 游戏组件事件: 受到攻击 订阅者:<br/>
	/// <see cref="MPCore.HandlePlayerDamage"/><br/>
	/// <see cref="RPManager.ProcessPlayerDamage"/><br/>
	/// </summary>
	public static event Action<IDType, Damageable.DamageInfo> OnPlayerDamage;
	/// <summary>
	/// 调用者 <see cref="RemotePlayer.Damage">
	/// </summary>
	public static void NotifyPlayerDamage(IDType steamId, Damageable.DamageInfo info)
		=> OnPlayerDamage(steamId, info);

	/// <summary>
	/// 游戏组件事件: 受到冲击力 订阅者:<br/>
	/// <see cref="MPCore.HandlePlayerAddForce"/><br/>
	/// </summary>
	public static event Action<IDType, Vector3, string> OnPlayerAddForce;
	public static void NotifyPlayerAddForce(IDType steamId, Vector3 force, string source = "")
		=> OnPlayerAddForce(steamId, force, source);

	/// <summary>
	/// 游戏事件: 玩家死亡 订阅者:<br/>
	/// <see cref="MPCore.HandlePlayerDeath"/>
	/// </summary>
	public static event Action<string, Damageable.DamageInfo> OnPlayerDeath;
	public static void NotifyPlayerDeath(string type, Damageable.DamageInfo info) => OnPlayerDeath(type, info);

	// 游戏事件: 玩家胜利
	public static event Action OnPlayerWin;
	public static void NotifyPlayerWin() => OnPlayerWin();

	// 游戏事件: 命令目标玩家停止抓握/拖拽交互
	public static event Action<IDType> OnPlayerStopInteraction;
	public static void NotifyPlayerStopInteraction(IDType playerId) => OnPlayerStopInteraction(playerId);

	// 游戏事件: 地图开始生成
	public static event Action<WorldLoader, BranchInfo, GenerationParameters> OnGenerateLevelsStart;
	public static void NotifyGenerateLevelsStart(WorldLoader __instance, BranchInfo branch, GenerationParameters genParams)
		=> OnGenerateLevelsStart(__instance, branch, genParams);

	// 游戏事件: 地图生成完成
	public static event Action<WorldLoader, BranchInfo, GenerationParameters> OnGenerateLevelsEnd;
	public static void NotifyGenerateLevelsEnd(WorldLoader __instance, BranchInfo branch, GenerationParameters genParams)
		=> OnGenerateLevelsEnd(__instance, branch, genParams);

	#endregion

	// 游戏事件: 规则更新事件
	public static event Action OnRulesUpdated;
	public static void NotifyRulesUpdated()=> OnRulesUpdated();

	#region[UI事件]

	// UI事件: 显示加载界面
	public static event Action<float> OnShowLoading;
	public static void NotifyShowLoading(float duration) => OnShowLoading(duration);

	// UI事件: 隐藏加载界面
	public static event Action OnHideLoading;
	public static void NotifyHideLoading() => OnHideLoading();

	// UI事件: 刷新大厅列表
	public static event Func<Task> OnRefreshLobbyList;
	public static void NotifyRefreshLobbyList() => OnRefreshLobbyList();

	// 远程玩家 抓取/松开 了本地玩家
	public static event Action<IDType, bool> OnRemoteHangStateChanged;
	// 远程玩家 拖拽/释放 了本地玩家
	public static event Action<IDType, bool> OnRemoteGrabStateChanged;

	public static void NotifyRemoteHang(IDType playerId, bool isActive) => OnRemoteHangStateChanged(playerId, isActive);
	public static void NotifyRemoteGrab(IDType playerId, bool isActive) => OnRemoteGrabStateChanged(playerId, isActive);

	#endregion
}
