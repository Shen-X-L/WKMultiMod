
using LiteNetLib.Utils;
using Steamworks;
using Steamworks.Data;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using WKMultiMod.src.Data;
using WKMultiMod.src.NetWork;
using WKMultiMod.src.Util;
using static WKMultiMod.src.Data.MPDataSerializer;
namespace WKMultiMod.src.Core;

public class MultiPlayerCore : MonoBehaviour {

	// 单例实例
	public static MultiPlayerCore Instance { get; private set; }
	// 标识这是否是"有效"实例(防止使用游戏初期被销毁的实例)
	public static bool HasValidInstance => Instance != null && Instance.isActiveAndEnabled;

	// Steam网络管理器 玩家ID管理器 远程玩家管理器
	public MPSteamworks Steamworks { get; private set; }
	public PlayerIdManager PlayerIdManager { get; private set; }
	public RemotePlayerManager RPManager { get; private set; }

	// 最大玩家数量
	private int _maxPlayerCount;
	// 下一个可用玩家ID
	private int _nextPlayerId = 0;
	// 单独的方法来获取并递增
	public int GetNextPlayerId() => _nextPlayerId++;

	// 世界种子 - 用于同步游戏世界生成
	public int WorldSeed { get; private set; }
	// 用于控制是否启用关卡标准化 Patch
	public static bool IsMultiplayerActive { get; private set; } = false;
	// 混乱模式开关
	public static bool IsChaosMod { get; private set; } = false;

	// 注意：日志通过 MultiPlayerMain.Logger 访问

	void Awake() {
		MPMain.Logger.LogInfo("[MP Mod loading] MultiplayerCore Awake");

		// 简单的重复检查作为安全网
		if (Instance != null && Instance != this) {
			MPMain.Logger.LogWarning("[MP Mod loading] 检测到重复实例，销毁当前");
			Destroy(gameObject);
			return;
		}

		Instance = this;

		// 初始化网络监听器和远程玩家管理器
		InitializeAllManagers();
	}

	void Start() {
		// 订阅场景切换
		SceneManager.sceneLoaded += OnSceneLoaded;
	}

	/// <summary>
	/// 初始化所有管理器
	/// </summary>
	private void InitializeAllManagers() {
		try {
			// 创建Steamworks组件（无状态）
			Steamworks = gameObject.AddComponent<MPSteamworks>();

			// 创建玩家ID管理器
			PlayerIdManager = gameObject.AddComponent<PlayerIdManager>();

			// 创建远程玩家管理器
			RPManager = gameObject.AddComponent<RemotePlayerManager>();

			// 订阅网络事件
			SubscribeToEvents();
			MPMain.Logger.LogInfo("[MP Mod init] 所有管理器初始化完成");

		} catch (Exception e) {
			MPMain.Logger.LogError("[MP Mod init] 管理器初始化失败: " + e.Message);
		}
	}

	/// <summary>
	/// 初始化网络事件订阅
	/// </summary>
	private void SubscribeToEvents() {
		// 订阅网络数据接收事件
		SteamNetworkEvents.OnReceiveData += ProcessReceiveData;

		// 订阅Steam玩家连接事件
		SteamNetworkEvents.OnPlayerConnected += ProcessPlayerConnected;
		SteamNetworkEvents.OnPlayerDisconnected += ProcessPlayerDisconnected;

		// 订阅大厅事件
		SteamNetworkEvents.OnLobbyEntered += ProcessLobbyEntered;
		SteamNetworkEvents.OnLobbyMemberJoined += ProcessLobbyMemberJoined;
		SteamNetworkEvents.OnLobbyMemberLeft += ProcessLobbyMemberLeft;

		// 订阅主机专用事件
		SteamNetworkEvents.OnHostNewPlayerConnected += ProcessHostNewPlayerConnected;
		SteamNetworkEvents.OnHostResponseExistingPlayers += ProcessHostResponseExistingPlayers;
	}

	private void Update() {

	}

	/// <summary>
	/// 当核心对象被销毁时调用
	/// </summary>
	private void OnDestroy() {
		// 订阅场景切换
		SceneManager.sceneLoaded -= OnSceneLoaded;

		// 取消所有事件订阅
		UnsubscribeFromEvents();

		// 重置状态
		ResetStateVariables();

		MPMain.Logger.LogInfo("[MP Mod destroy] MultiPlayerCore 已被销毁");
	}

	/// <summary>
	/// 场景加载完成时调用
	/// </summary>
	/// <param name="scene"></param>
	/// <param name="mode"></param>
	private void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
		MPMain.Logger.LogInfo("[MP Mod] 核心场景加载完成: " + scene.name);

		IsChaosMod = false;

		if (scene.name == "Game-Main") {
			// 注册命令和初始化世界数据
			if (CommandConsole.instance != null) {
				RegisterCommands();
			} else {
				MPMain.Logger.LogError("[MP Mod] 场景加载后 CommandConsole 实例仍为 null, 无法注册命令.");
			}
		}
		if (scene.name == "Main-Menu") {
			// 返回主菜单时关闭连接 重设置
			ResetStateVariables();
		}
	}

	/// <summary>
	/// 取消所有网络事件订阅
	/// </summary>
	private void UnsubscribeFromEvents() {
		// 订阅网络数据接收事件
		SteamNetworkEvents.OnReceiveData -= ProcessReceiveData;

		// 订阅Steam玩家连接事件
		SteamNetworkEvents.OnPlayerConnected -= ProcessPlayerConnected;
		SteamNetworkEvents.OnPlayerDisconnected -= ProcessPlayerDisconnected;

		// 订阅大厅事件
		SteamNetworkEvents.OnLobbyEntered -= ProcessLobbyEntered;
		SteamNetworkEvents.OnLobbyMemberJoined -= ProcessLobbyMemberJoined;
		SteamNetworkEvents.OnLobbyMemberLeft -= ProcessLobbyMemberLeft;

		// 订阅主机专用事件
		SteamNetworkEvents.OnHostNewPlayerConnected -= ProcessHostNewPlayerConnected;
		SteamNetworkEvents.OnHostResponseExistingPlayers -= ProcessHostResponseExistingPlayers;
	}

	// 重置设置
	private void ResetStateVariables() {
		IsMultiplayerActive = false;
		IsChaosMod = false;

		PlayerIdManager.ResetAll();
		RPManager.ResetAll();
	}

	// 命令注册
	private void RegisterCommands() {
		// 将命令注册到 CommandConsole
		CommandConsole.AddCommand("host", Host);
		CommandConsole.AddCommand("join", Join);
		CommandConsole.AddCommand("leave", Leave);
		CommandConsole.AddCommand("chaos", ChaosMod);
		MPMain.Logger.LogInfo("[MP Mod loading] 命令集 注册成功");
	}

	// 命令实现
	public async void Host(string[] args) {
		if (args.Length < 1) {
			CommandConsole.LogError("Usage: host <room_name> [max_players]");
			return;
		}

		string roomName = args[0];
		int maxPlayers = args.Length >= 2 ? int.Parse(args[1]) : 4;

		MPMain.Logger.LogInfo($"正在创建房间: {roomName}...");

		try {
			// 使用异步版本
			await Steamworks.CreateRoomAsync(roomName, maxPlayers, (success) => {
				MPMain.Logger.LogWarning("[MP Mod MPSteamworks] TestF");
				if (success) {
					MPMain.Logger.LogInfo($"房间创建成功: {roomName} ID: {Steamworks.CurrentLobbyId.ToString()}");
					StartMultiPlayerMode();
				} else {
					MPMain.Logger.LogError("房间创建失败");
				}
			});
		} catch (Exception ex) {
			MPMain.Logger.LogError($"创建房间异常: {ex.Message}");
		}
	}

	public async void Join(string[] args) {
		if (args.Length < 1) {
			CommandConsole.LogError("Usage: join <lobby_id>");
			return;
		}

		if (ulong.TryParse(args[0], out ulong lobbyId)) {
			MPMain.Logger.LogInfo($"正在加入房间: {lobbyId}...");

			try {
				await Steamworks.JoinRoomAsync(lobbyId, (success) => {
					if (success) {
						MPMain.Logger.LogInfo("加入房间成功");
						StartMultiPlayerMode();
					} else {
						MPMain.Logger.LogError("加入房间失败");
					}
				});
			} catch (Exception ex) {
				MPMain.Logger.LogError($"加入房间异常: {ex.Message}");
			}
		} else {
			MPMain.Logger.LogError("无效的房间ID格式");
		}
	}

	public void Leave(string[] args) {
		Steamworks.DisconnectAll();
		ResetStateVariables();
		MPMain.Logger.LogInfo("[MP Mod] 所有连接已断开, 远程玩家已清理.");
	}

	public void ChaosMod(string[] args) {
		if (args.Length <= 0) {
			IsChaosMod = !IsChaosMod;
		} else {
			try {
				IsChaosMod = TypeConverter.ToBool(args[0]);
			} catch {
				CommandConsole.LogError("Usage: chaos <bool> \n bool value can be: true false 1 0");
			}
		}
	}

	/// <summary>
	/// 处理主机接收到新玩家网络连接
	/// </summary>
	private void ProcessHostNewPlayerConnected(SteamId newPlayerSteamId, Connection connection) {
		if (!Steamworks.IsHost) return;

		MPMain.Logger.LogInfo($"[MP Mod] 主机：新玩家网络连接 {newPlayerSteamId.Value}");

		// 1. 为新玩家分配玩家ID
		int newPlayerId = PlayerIdManager.AssignPlayerId(newPlayerSteamId);

		// 2. 获取所有现有玩家ID（排除新玩家）
		var existingPlayerIds = PlayerIdManager.GetAllExistingPlayerIds(newPlayerSteamId);

		// 3. 为新玩家发送连接成功消息
		SendConnectionSuccessToNewPlayer(newPlayerSteamId, newPlayerId, existingPlayerIds);

		// 4. 通知所有现有玩家创建新玩家
		NotifyAllPlayersCreateNewPlayer(newPlayerId);

		// 5. 发送初始化数据（世界种子等）
		SendInitializationDataToNewPlayer(newPlayerSteamId);
	}

	/// <summary>
	/// 发送连接成功消息给新玩家
	/// </summary>
	private void SendConnectionSuccessToNewPlayer(SteamId newPlayerSteamId, int newPlayerId, List<int> existingPlayerIds) {
		var writer = new NetDataWriter();
		writer.Put((int)PacketType.ConnectedToServer);
		writer.Put(existingPlayerIds.Count);

		foreach (var id in existingPlayerIds) {
			writer.Put(id);
		}

		// 通过Steamworks发送给新玩家
		var data = MPDataSerializer.WriterToBytes(writer);
		SteamNetworkEvents.TriggerHostSendToPeer(data, newPlayerSteamId, SendType.Reliable);

		MPMain.Logger.LogInfo($"[MP Mod] 已向新玩家发送连接成功消息，分配ID: {newPlayerId}");
	}

	/// <summary>
	/// 通知所有玩家创建新玩家
	/// </summary>
	private void NotifyAllPlayersCreateNewPlayer(int newPlayerId) {
		var writer = new NetDataWriter();
		writer.Put((int)PacketType.CreatePlayer);
		writer.Put(newPlayerId);

		var data = MPDataSerializer.WriterToBytes(writer);
		SteamNetworkEvents.TriggerHostBroadcast(data, SendType.Reliable);

		// 主机自己也创建本地表示（如果需要）
		RPManager.CreatePlayer(newPlayerId);

		MPMain.Logger.LogInfo($"[MP Mod] 已通知所有玩家创建新玩家 ID: {newPlayerId}");
	}

	/// <summary>
	/// 发送初始化数据给新玩家
	/// </summary>
	private void SendInitializationDataToNewPlayer(SteamId newPlayerSteamId) {
		// 发送世界种子
		var seedWriter = new NetDataWriter();
		seedWriter.Put((int)PacketType.SeedUpdate);
		seedWriter.Put(WorldLoader.instance.seed);

		var seedData = MPDataSerializer.WriterToBytes(seedWriter);
		SteamNetworkEvents.TriggerHostSendToPeer(seedData, newPlayerSteamId, SendType.Reliable);

		// 可以添加其他初始化数据，如游戏状态、物品状态等

		MPMain.Logger.LogInfo($"[MP Mod] 已向新玩家发送初始化数据");
	}

	/// <summary>
	/// 处理大厅成员加入
	/// </summary>
	private void ProcessLobbyMemberJoined(SteamId steamId) {
		MPMain.Logger.LogInfo($"[MP Mod] 玩家加入大厅: {steamId}");
	}

	// 玩家断连
	private void ProcessPlayerDisconnected(SteamId steamId) {

	}

	// 加入大厅
	private void ProcessLobbyEntered(Lobby lobby) {
		MPMain.Logger.LogWarning("[MP Mod MPSteamworks] TestD2");
	}

	// 离开大厅
	private void ProcessLobbyMemberLeft(SteamId steamId) {

	}

	// 获取现有玩家的ID
	private void ProcessHostResponseExistingPlayers(List<int> playerIds) { 
	}

	/// <summary>
	/// 处理普通玩家连接事件（所有玩家都会收到）
	/// </summary>
	private void ProcessPlayerConnected(SteamId steamId) {
		MPMain.Logger.LogInfo($"[MP Mod] 玩家连接: {steamId}");

		// 如果不是主机，可能需要执行一些操作
		// 比如：更新玩家列表UI、记录连接状态等
	}

	/// <summary>
	/// 处理连接成功消息（客户端收到）
	/// </summary>
	private void ProcessConnectionSuccess(NetDataReader reader) {
		int peerCount = reader.GetInt();
		MPMain.Logger.LogInfo($"[MP Mod] 客户端：连接成功，加载 {peerCount} 个现有玩家");

		CommandConsole.Log($"连接成功！\n正在创建 {peerCount} 个玩家实例");

		// 创建所有现有玩家
		for (int i = 0; i < peerCount; i++) {
			int playerId = reader.GetInt();
			RPManager.CreatePlayer(playerId);
			MPMain.Logger.LogInfo($"[MP Mod] 创建现有玩家 ID: {playerId}");
		}
	}

	/// <summary>
	/// 处理创建玩家消息
	/// </summary>
	private void ProcessCreatePlayer(int playerId) {
		RPManager.CreatePlayer(playerId);
		MPMain.Logger.LogInfo($"[MP Mod] 创建新玩家 ID: {playerId}");
	}

	/// <summary>
	/// 处理网络接收数据
	/// </summary>
	/// <param name="playId"></param>
	/// <param name="data"></param>
	private void ProcessReceiveData(int playId, byte[] data) {
		// 基本验证：确保数据足够读取一个整数(数据包类型)
		var reader = MPDataSerializer.BytesToReader(data);
		PacketType packetType = (PacketType)reader.GetInt();

		switch (packetType) {
			// 连接成功
			case PacketType.ConnectedToServer:
				ProcessConnectionSuccess(reader);
				break;
			// 种子加载
			case PacketType.SeedUpdate:
				ProcessSeedUpdate(reader.GetInt());
				break;
			// 创建玩家
			case PacketType.CreatePlayer:
				RPManager.CreatePlayer(reader.GetInt());
				break;
			// 移除玩家
			case PacketType.RemovePlayer:
				RPManager.DestroyPlayer(reader.GetInt());
				break;
			// 玩家数据更新
			case PacketType.PlayerDataUpdate:
				var playerData = MPDataSerializer.ReadFromNetData(reader);
				RPManager.ProcessPlayerData(playId, playerData);
				break;
		}
	}

	/// <summary>
	/// 加载世界种子
	/// </summary>
	/// <param name="seed"></param>
	private void ProcessSeedUpdate(int seed) {
		MPMain.Logger.LogInfo("[MP Mod client] 加载世界, 种子号: " + seed.ToString());
		WorldLoader.ReloadWithSeed(new string[] { seed.ToString() });
	}

	// 开启多人联机模式
	public static void StartMultiPlayerMode() { IsMultiplayerActive = true;}
	// 关闭多人联机模式
	public static void CloseMultiPlayerMode() { IsMultiplayerActive = false; }
}
