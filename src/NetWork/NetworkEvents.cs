using LiteNetLib.Utils;
using Steamworks;
using Steamworks.Data;
using System;

namespace WKMultiMod.src.NetWork;

// 数据包类型枚举 - 定义不同类型的网络消息
public enum PacketType {
	ConnectionEstablished = 0,	// 连接成功通知
	WorldSeedUpdate = 1,		// 世界种子更新
	PlayerCreate = 2,			// 创建新玩家
	PlayerRemove = 3,			// 移除玩家
	PlayerDataUpdate = 4,		// 玩家数据更新
	InitialDataRequest = 5,     // 请求初始化世界数据
	GameStateSync = 6,          // 游戏内状态同步,目前仅占位
	BroadcastMessage = 7,		// 广播信息
	RequestTeleport = 8,        // 向其他玩家请求传送,并同步deathgoo状态
	RespondTeleport = 9,		// 响应其他玩家传送,并发送部分状态数据

}

public static class SteamNetworkEvents {

	// 发送事件: 本地玩家数据 -> 主机网络
	public static event Action<byte[], SendType, ushort> OnSendToHost;
	// 发送事件: 本地玩家数据 -> 广播所有网络
	public static event Action<byte[], SendType, ushort> OnBroadcast;
	// 发送事件: 本地玩家数据 -> 特定网络
	public static event Action<byte[], SteamId, SendType, ushort> OnSendToPeer;


	public static void TriggerSendToHost(byte[] data, SendType sendType = SendType.Reliable, ushort laneIndex = 0)
		=> OnSendToHost?.Invoke(data, sendType, laneIndex);
	public static void TriggerBroadcast(byte[] data, SendType sendType = SendType.Reliable, ushort laneIndex = 0)
		=> OnBroadcast?.Invoke(data, sendType, laneIndex);
	public static void TriggerSendToPeer(
		byte[] data, SteamId steamId,
		SendType sendType = SendType.Reliable, ushort laneIndex = 0)
		=> OnSendToPeer?.Invoke(data, steamId, sendType, laneIndex);

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
	public static event Action<Lobby,SteamId> OnLobbyHostChanged;

	public static void TriggerLobbyEntered(Lobby lobby)
		=> OnLobbyEntered?.Invoke(lobby);

	public static void TriggerLobbyMemberJoined(SteamId steamId)
		=> OnLobbyMemberJoined?.Invoke(steamId);

	public static void TriggerLobbyMemberLeft(SteamId steamId)
		=> OnLobbyMemberLeft?.Invoke(steamId);

	public static void TriggerLobbyHostChanged(Lobby lobby,SteamId hostId)
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