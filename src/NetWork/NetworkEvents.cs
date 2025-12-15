using LiteNetLib;
using LiteNetLib.Utils;
using Steamworks;
using Steamworks.Data;
using System;
using System.Collections.Generic;
using System.Text;
using WKMultiMod.src.Data;

namespace WKMultiMod.src.NetWork;

public static class SteamNetworkEvents {
	// 发送事件：本地玩家数据 -> 主机网络
	public static event Action<byte[], SendType,ushort> OnSendToHost;
	// 发送事件: 主机广播数据 -> 所有客户端网络
	public static event Action<byte[], SendType, ushort> OnHostBroadcast;
	// 发送事件: 主机广播数据 -> 特定客户端网络
	public static event Action<byte[], SteamId, SendType, ushort> OnHostSendToPeer;

	public static void TriggerSendToHost(byte[] data, SendType sendType = SendType.Reliable, ushort laneIndex = 0)
		=> OnSendToHost?.Invoke(data, sendType, laneIndex);
	public static void TriggerHostBroadcast(byte[] data, SendType sendType = SendType.Reliable, ushort laneIndex = 0)
		=> OnHostBroadcast?.Invoke(data, sendType, laneIndex);
	public static void TriggerHostSendToPeer(
		byte[] data, SteamId steamId, 
		SendType sendType = SendType.Reliable, ushort laneIndex = 0)
		=> OnHostSendToPeer?.Invoke(data, steamId, sendType, laneIndex);

	// 接收事件：网络 -> 远程玩家管理类
	public static event Action<int, byte[]> OnReceiveData;
	public static void TriggerReceiveSteamData(int playId, byte[] data)
		=> OnReceiveData?.Invoke(playId, data);

	// 接收事件: 主机接收到新玩家连接（只有主机收到）
	public static event Action<SteamId, Connection> OnHostNewPlayerConnected;
	public static void TriggerHostNewPlayerConnected(SteamId steamId, Connection connection)
		=> OnHostNewPlayerConnected?.Invoke(steamId, connection);
	// 接收事件: 获取所有现有玩家的ID（主机用）
	public static event Action<SteamId> OnHostRequestExistingPlayers;
	public static void TriggerHostRequestExistingPlayers(SteamId newPlayerSteamId)
		=> OnHostRequestExistingPlayers?.Invoke(newPlayerSteamId);
	// 接收事件: 响应获取现有玩家的ID
	public static event Action<List<int>> OnHostResponseExistingPlayers;
	public static void TriggerHostResponseExistingPlayers(List<int> playerIds)
		=> OnHostResponseExistingPlayers?.Invoke(playerIds);

	// 接收事件: 玩家连接信息 -> 主机
	public static event Action<SteamId> OnPlayerConnected;
	public static event Action<SteamId> OnPlayerDisconnected;

	public static void TriggerPlayerConnected(SteamId steamId)
	=> OnPlayerConnected?.Invoke(steamId);

	public static void TriggerPlayerDisconnected(SteamId steamId)
		=> OnPlayerDisconnected?.Invoke(steamId);

	// 大厅事件
	// 接收事件: 进入大厅
	// lobbyId
	public static event Action<Lobby> OnLobbyEntered;
	// 接收事件: 玩家加入大厅
	// playerName
	public static event Action<SteamId> OnLobbyMemberJoined;
	// 接收事件: 玩家离开大厅
	// playerName
	public static event Action<SteamId> OnLobbyMemberLeft;    

	public static void TriggerLobbyEntered(Lobby lobby)
		=> OnLobbyEntered?.Invoke(lobby);

	public static void TriggerLobbyMemberJoined(SteamId steamId)
		=> OnLobbyMemberJoined?.Invoke(steamId);

	public static void TriggerLobbyMemberLeft(SteamId steamId)
		=> OnLobbyMemberLeft?.Invoke(steamId);
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