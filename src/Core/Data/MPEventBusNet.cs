using WKMPMod.Core;
using Steamworks;
using Steamworks.Data;
using System;
using System.Collections.Generic;

namespace WKMPMod.Data;

// 数据包类型枚举 - 定义不同类型的网络消息
public enum PacketType {
	LobbyDataRequest = 0,	// 客机->主机: 请求房间数据
	LobbyDataResponse = 1,  // 主机->客机: 响应房间数据
	MemberDataRequest = 2,  // 客机->主机->客机: 请求玩家数据
	MemberDataResponse = 3, // 客机->主机->客机: 响应玩家数据

	//PlayerCreate = 4,      // 主机->客机: 创建新玩家
	//PlayerRemove = 5,       // 主机->客机: 移除玩家
	GameUIMessage = 4,      // 客机->主机->客机: 调用游戏本体UI组件显示消息
	BroadcastMessage = 5,   // 客机->主机->客机: 广播信息
	WorldStateSync = 6,     // 主机->客机: 世界状态同步, 如Mess高度
	RemoteCommand = 7,		// 主机->客机: 主机命令注入, 如切换地图/重置世界/设置队伍

	// 非玩家实体状态同步
	PitonStateSync = 16,    // 客机->主机->客机: 同步已放置可攀爬物(岩钉/自动岩钉/钢筋/带绳钢筋)的创建/敲入/失效状态
	ItemStateSync = 17,     // 客机->主机->客机: 同步物品的扔出/拾取
	EnemyStateSync = 18,    // 主机权威: 同步敌人位置/生命值/死亡, 客机可向主机请求伤害

	// 玩家间互动
	PlayerDataUpdate = 32,   // 客机->主机->客机: 玩家数据更新
	PlayerDamage = 33,       // 客机->主机->客机: 玩家造成伤害
	PlayerAddForce = 34,     // 客机->主机->客机: 玩家添加冲击力
	PlayerStopInteraction = 35, // 客机->主机->客机: 玩家停止当前交互(如抓取)
	PlayerDeath = 36,        // 客机->主机->客机: 玩家死亡, 发送广播

	// 杂项
	PlayerTeleportRequest = 48, // 客机->主机->客机: 请求传送
	PlayerTeleportRespond = 49, // 客机->主机->客机: 响应传送
	PlayerCheckRequest = 50,    // 客机->主机->客机: 请求检查数据
}

public static class MPEventBusNet {
	// 接收事件:网络 -> 远程玩家管理类
	public static event Action<ulong, ArraySegment<byte>> OnReceiveData;
	public static void NotifyReceive(ulong steamId, ArraySegment<byte> data)
		=> OnReceiveData?.Invoke(steamId, data);

	/// <summary>
	/// 接收事件: 玩家连接信息 玩家 -> 主机
	/// </summary>
	public static event Action<SteamId> OnPlayerConnected;
	/// <summary>
	/// 接收事件: 断开连接
	/// </summary>
	public static event Action<SteamId> OnPlayerDisconnected;
	/// <summary>
	/// 接收事件: 玩家数据发送改变 订阅者<see cref="MPCore.HandleMemberDataChanged"/>
	/// </summary>
	public static event Action<Friend, Dictionary<string, string>> OnMemberDataChanged;

	public static void NotifyPlayerConnected(SteamId steamId)
		=> OnPlayerConnected?.Invoke(steamId);
	public static void NotifyPlayerDisconnected(SteamId steamId)
		=> OnPlayerDisconnected?.Invoke(steamId);
	public static void NotifyMemberDataChanged(Friend steamId, Dictionary<string, string> data)
	=> OnMemberDataChanged?.Invoke(steamId, data);

	#region[大厅事件]

	/// <summary>
	/// 接收事件: 进入大厅
	/// </summary>
	public static event Action<Lobby> OnLobbyEntered;
	/// <summary>
	/// 接收事件: 玩家加入大厅
	/// </summary>
	public static event Action<Friend> OnLobbyMemberJoined;
	/// <summary>
	/// 接收事件: 玩家离开大厅
	/// </summary>
	public static event Action<Friend> OnLobbyMemberLeave;
	/// <summary>
	/// 接收事件: 大厅所有权发生变更
	/// </summary>
	public static event Action<Friend,bool> OnLobbyHostChanged;
	/// <summary>
	/// 接收事件: 大厅数据(规则)变动 订阅者<see cref="MPCore.HandleLobbyDataChanged"/>
	/// </summary>
	public static event Action<Dictionary<string, string>> OnLobbyDataChanged;

	public static void NotifyLobbyEntered(Lobby lobby)
		=> OnLobbyEntered?.Invoke(lobby);
	public static void NotifyLobbyMemberJoined(Friend steamId)
		=> OnLobbyMemberJoined?.Invoke(steamId);
	public static void NotifyLobbyMemberLeave(Friend steamId)
		=> OnLobbyMemberLeave?.Invoke(steamId);
	public static void NotifyLobbyHostChanged(Friend hostId,bool isHost)
		=> OnLobbyHostChanged?.Invoke(hostId, isHost);
	public static void NotifyLobbyDataChanged(Dictionary<string, string> delta)
		=> OnLobbyDataChanged?.Invoke(delta);

	#endregion


	// 邀请事件
	// 接收世界: 接收大厅邀请
	public static event Action<Friend, Lobby> OnLobbyInvite;
	public static void NotifyLobbyInvite(Friend friend, Lobby lobby)
		=> OnLobbyInvite?.Invoke(friend, lobby);
	// 接收事件: 接受游戏邀请
	public static event Action<Lobby, SteamId> OnGameLobbyJoinRequested;
	public static void NotifyGameLobbyJoinRequested(Lobby lobby, SteamId steamId)
		=> OnGameLobbyJoinRequested?.Invoke(lobby, steamId);
}
