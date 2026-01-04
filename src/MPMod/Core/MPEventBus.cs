using LiteNetLib.Utils;
using Steamworks;
using Steamworks.Data;
using System;
using UnityEngine;
using static Steamworks.InventoryItem;

namespace WKMultiMod.src.Core;

// 数据包类型枚举 - 定义不同类型的网络消息
public enum PacketType {
	WorldInitRequest = 0,   // 客机->主机: 请求初始化世界数据
	WorldInitData = 1,      // 主机->客机: 接收初始化世界数据,创建玩家,重加载地图
	PlayerCreate = 2,       // 主机->客机: 创建新玩家
	PlayerRemove = 3,       // 主机->客机: 移除玩家
	PlayerDataUpdate = 4,   // 客机->主机->客机: 玩家数据更新
	WorldStateSync = 5,     // 主机->客机: 世界状态同步, 如Mess高度
	BroadcastMessage = 6,   // 客机->主机->客机: 广播信息
	PlayerDamage = 7,       // 客机->主机->客机: 玩家造成伤害
	PlayerAddForce = 8,     // 客机->主机->客机: 玩家添加冲击力

	// 临时措施
	PlayerTeleport = 40,        // 客机->主机->客机: 请求传送
	RespondPlayerTeleport = 41, // 客机->主机->客机: 响应传送
}

public static class MPEventBus {
	public static class Net {
		// 接收事件：网络 -> 远程玩家管理类
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
		public static event Action<SteamId> OnLobbyMemberJoined;
		// 接收事件: 玩家离开大厅
		public static event Action<SteamId> OnLobbyMemberLeft;
		// 接收事件: 大厅成员数据或大厅所有权发生变更
		public static event Action<Lobby, SteamId> OnLobbyHostChanged;

		public static void NotifyLobbyEntered(Lobby lobby)
			=> OnLobbyEntered?.Invoke(lobby);
		public static void NotifyLobbyMemberJoined(SteamId steamId)
			=> OnLobbyMemberJoined?.Invoke(steamId);
		public static void NotifyLobbyMemberLeft(SteamId steamId)
			=> OnLobbyMemberLeft?.Invoke(steamId);
		public static void NotifyLobbyHostChanged(Lobby lobby, SteamId hostId)
			=> OnLobbyHostChanged?.Invoke(lobby, hostId);

	}

	public static class Game {
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
}