
using Steamworks;
using Steamworks.Data;
using System;

namespace WKMPMod.Data;

// 数据包类型枚举 - 定义不同类型的网络消息
public enum PacketType {
	WorldInitRequest = 0,   // 客机->主机: 请求初始化世界数据
	WorldInitData = 1,      // 主机->客机: 接收初始化世界数据,创建玩家,重加载地图
	//PlayerCreate = 2,       // 主机->客机: 创建新玩家
	PlayerRemove = 3,       // 主机->客机: 移除玩家
	PlayerDataUpdate = 4,   // 客机->主机->客机: 玩家数据更新
	WorldStateSync = 5,     // 主机->客机: 世界状态同步, 如Mess高度
	BroadcastMessage = 6,   // 客机->主机->客机: 广播信息
	PlayerDamage = 7,       // 客机->主机->客机: 玩家造成伤害
	PlayerAddForce = 8,     // 客机->主机->客机: 玩家添加冲击力
	PlayerDeath = 9,        // 客机->主机->客机: 玩家死亡, 发送广播
	PlayerCreateRequest = 10, // 客机->主机->客机: 请求创建玩家相应信息(内容:模型工厂名)
	PlayerCreateResponse = 11, // 主机->客机: 响应创建玩家请求的相关信息(内容:模型工厂名)


	// 临时措施
	PlayerTeleportRequest = 40, // 客机->主机->客机: 请求传送
	PlayerTeleportRespond = 41, // 客机->主机->客机: 响应传送
}

public static class MPEventBusNet {
	// 接收事件:网络 -> 远程玩家管理类
	public static event Action<ulong, ArraySegment<byte>> OnReceiveData;
	public static void NotifyReceive(ulong steamId, ArraySegment<byte> data)
		=> OnReceiveData?.Invoke(steamId, data);

	// 接收事件: 玩家连接信息 玩家 -> 主机
	public static event Action<SteamId> OnPlayerConnected;
	// 接收事件: 断开连接
	public static event Action<SteamId> OnPlayerDisconnected;

	public static void NotifyPlayerConnected(SteamId steamId)
		=> OnPlayerConnected?.Invoke(steamId);
	public static void NotifyPlayerDisconnected(SteamId steamId)
		=> OnPlayerDisconnected?.Invoke(steamId);

	// 大厅事件
	// 接收事件: 进入大厅
	public static event Action<Lobby> OnLobbyEntered;
	// 接收事件: 玩家加入大厅
	public static event Action<Friend> OnLobbyMemberJoined;
	// 接收事件: 玩家离开大厅
	public static event Action<Friend> OnLobbyMemberLeave;
	// 接收事件: 大厅成员数据或大厅所有权发生变更
	public static event Action<Lobby, Friend> OnLobbyHostChanged;

	public static void NotifyLobbyEntered(Lobby lobby)
		=> OnLobbyEntered?.Invoke(lobby);
	public static void NotifyLobbyMemberJoined(Friend steamId)
		=> OnLobbyMemberJoined?.Invoke(steamId);
	public static void NotifyLobbyMemberLeave(Friend steamId)
		=> OnLobbyMemberLeave?.Invoke(steamId);
	public static void NotifyLobbyHostChanged(Lobby lobby, Friend hostId)
		=> OnLobbyHostChanged?.Invoke(lobby, hostId);

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