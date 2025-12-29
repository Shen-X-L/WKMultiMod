using LiteNetLib.Utils;
using Steamworks;
using Steamworks.Data;
using System;

namespace WKMultiMod.src.NetWork;

// 数据包类型枚举 - 定义不同类型的网络消息
public enum PacketType {
	WorldInitRequest = 0,  // 客机->主机: 请求初始化世界数据
	WorldInitData = 1,		// 主机->客机: 接收初始化世界数据,创建玩家,重加载地图
	PlayerCreate = 2,       // 主机->客机: 创建新玩家
	PlayerRemove = 3,       // 主机->客机: 移除玩家
	PlayerDataUpdate = 4,   // 客机->主机->客机: 玩家数据更新
	WorldStateSync = 5,         // 主机->客机: 世界状态同步, 如Mess高度
	BroadcastMessage = 6,       // 客机->主机->客机: 广播信息

	// 临时措施
	PlayerTeleport = 40,        // 客机->主机->客机: 请求传送
	RespondPlayerTeleport = 41, // 客机->主机->客机: 响应传送
}

public static class SteamNetworkEvents {
	// 接收事件：网络 -> 远程玩家管理类
	public static event Action<ulong, byte[]> OnReceiveData;
	public static void TriggerReceiveSteamData(ulong steamId, byte[] data)
		=> OnReceiveData?.Invoke(steamId, data);

	// 接收事件: 玩家连接信息 玩家 -> 主机
	public static event Action<SteamId> OnPlayerConnected;
	// 接收事件: 断开连接
	public static event Action<SteamId> OnPlayerDisconnected;

	public static void TriggerPlayerConnected(SteamId steamId)
		=> OnPlayerConnected?.Invoke(steamId);
	public static void TriggerPlayerDisconnected(SteamId steamId)
		=> OnPlayerDisconnected?.Invoke(steamId);

	// 大厅事件
	// 接收事件: 进入大厅
	public static event Action<Lobby> OnLobbyEntered;
	// 接收事件: 玩家加入大厅
	public static event Action<SteamId> OnLobbyMemberJoined;
	// 接收事件: 玩家离开大厅
	public static event Action<SteamId> OnLobbyMemberLeft;
	// 接收事件: 大厅成员数据或大厅所有权发生变更
	public static event Action<Lobby, SteamId> OnLobbyHostChanged;

	public static void TriggerLobbyEntered(Lobby lobby)
		=> OnLobbyEntered?.Invoke(lobby);

	public static void TriggerLobbyMemberJoined(SteamId steamId)
		=> OnLobbyMemberJoined?.Invoke(steamId);

	public static void TriggerLobbyMemberLeft(SteamId steamId)
		=> OnLobbyMemberLeft?.Invoke(steamId);

	public static void TriggerLobbyHostChanged(Lobby lobby, SteamId hostId)
		=> OnLobbyHostChanged?.Invoke(lobby, hostId);
}

public static class LiteNetworkEvents {
	// 发送事件：本地玩家数据 -> 网络
	public static event Action<NetDataWriter> OnSendToHost;
	public static void TriggerSendToHost(NetDataWriter data)
		=> OnSendToHost?.Invoke(data);

	// 连接事件
	public static event Action<ulong> OnPlayerConnected;
	public static event Action<ulong> OnPlayerDisconnected;
}