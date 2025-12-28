
using LiteNetLib.Utils;
using Steamworks;
using Steamworks.Data;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.SceneManagement;
using WKMultiMod.src.Data;
using WKMultiMod.src.NetWork;
using WKMultiMod.src.Util;
using static WKMultiMod.src.Data.MPDataSerializer;
namespace WKMultiMod.src.Core;

public class MPCore : MonoBehaviour {

	// Debug日志输出间隔
	private TickTimer _debugTick = new TickTimer(5f);

	// 单例实例
	public static MPCore Instance { get; private set; }
	// 标识这是否是"有效"实例(防止使用游戏初期被销毁的实例)
	public static bool HasValidInstance => Instance != null && Instance.isActiveAndEnabled;

	// Steam网络管理器 远程玩家管理器 
	internal MPSteamworks Steamworks { get; private set; }
	internal RemotePlayerManager RPManager { get; private set; }
	// 本地数据获取类已经变成静态类
	//internal LocalPlayerManager LPManager { get; private set; }

	// 玩家数据发送时间 每秒30次
	private TickTimer _playerDataTick = new TickTimer(30);
	private readonly NetDataWriter _playerDataWriter = new NetDataWriter();

	// 世界种子 - 用于同步游戏世界生成
	public int WorldSeed { get; private set; }
	// 用于控制是否启用关卡标准化 Patch
	public static bool IsMultiplayerActive { get; private set; } = false;
	// 混乱模式开关
	public static bool IsChaosMod { get; private set; } = false;
	// 是否已初始化
	public static bool HasInitialized { get; private set; } = false;

	// 注意：日志通过 MultiPlayerMain.Logger 访问

	void Awake() {
		// Debug
		MPMain.Logger.LogInfo("[MPCore] MultiplayerCore Awake");

		// 简单的重复检查作为安全网
		if (Instance != null && Instance != this) {
			// Debug
			MPMain.LogWarning(
				"[MPCore] 检测到重复实例,销毁当前",
				"[MPCore] Duplicate instance detected, destroying the current one.");
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

	void Update() {
		// 没有开启多人时停止更新
		if (IsMultiplayerActive == false || HasInitialized == false)
			return;
		// 没有链接时停止更新
		if (!Instance.Steamworks.HasConnections)
			return;
		// 发送本地玩家数据
		SeedLocalPlayerData();
	}

	/// <summary>
	/// 初始化所有管理器
	/// </summary>
	private void InitializeAllManagers() {
		try {
			// 创建Steamworks组件(无状态)
			Steamworks = gameObject.AddComponent<MPSteamworks>();

			// 创建远程玩家管理器
			RPManager = gameObject.AddComponent<RemotePlayerManager>();

			//// 创建本地信息获取发送管理器
			//LPManager = gameObject.AddComponent<LocalPlayerManager>();

			// 订阅网络事件
			SubscribeToEvents();
			// Debug
			MPMain.LogInfo(
				"[MPCore] 所有管理器初始化完成",
				"[MPCore] All managers initialized.");
		} catch (Exception e) {
			MPMain.LogError(
				$"[MPCore] 管理器初始化失败: {e.Message}",
				$"[MPCore] Failed to initialize Manager: {e.Message}");
		}
	}

	/// <summary>
	/// 初始化网络事件订阅
	/// </summary>
	private void SubscribeToEvents() {
		// 订阅网络数据接收事件
		SteamNetworkEvents.OnReceiveData += ProcessReceiveData;

		// 订阅大厅事件
		SteamNetworkEvents.OnLobbyEntered += ProcessLobbyEntered;
		//SteamNetworkEvents.OnLobbyMemberJoined += ProcessLobbyMemberJoined;
		SteamNetworkEvents.OnLobbyMemberLeft += ProcessLobbyMemberLeft;
		SteamNetworkEvents.OnLobbyHostChanged += ProcessLobbyHostChanged;

		// 订阅玩家连接事件
		SteamNetworkEvents.OnPlayerConnected += ProcessPlayerConnected;
		SteamNetworkEvents.OnPlayerDisconnected += ProcessPlayerDisconnected;
	}

	/// <summary>
	/// 取消所有网络事件订阅
	/// </summary>
	private void UnsubscribeFromEvents() {
		// 订阅网络数据接收事件
		SteamNetworkEvents.OnReceiveData -= ProcessReceiveData;

		// 订阅大厅事件
		SteamNetworkEvents.OnLobbyEntered -= ProcessLobbyEntered;
		//SteamNetworkEvents.OnLobbyMemberJoined -= ProcessLobbyMemberJoined;
		SteamNetworkEvents.OnLobbyMemberLeft -= ProcessLobbyMemberLeft;
		SteamNetworkEvents.OnLobbyHostChanged -= ProcessLobbyHostChanged;

		// 订阅玩家连接事件
		SteamNetworkEvents.OnPlayerConnected -= ProcessPlayerConnected;
		SteamNetworkEvents.OnPlayerDisconnected -= ProcessPlayerDisconnected;
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

		// Debug
		MPMain.LogInfo(
			"[MPCore] MPCore 已被销毁",
			"[MPCore] MPCore Destroy");
	}

	/// <summary>
	/// 场景加载完成时调用
	/// </summary>
	/// <param name="scene"></param>
	/// <param name="mode"></param>
	private void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
		// Debug
		MPMain.LogInfo(
			$"[MPCore] 场景加载完成: {scene.name}",
			$"[MPCore] Scene loading completed: {scene.name}");

		IsChaosMod = false;

		switch (scene.name) {
			case "Game-Main":
				// 注册命令和初始化世界数据
				if (CommandConsole.instance != null) {
					RegisterCommands();
				} else {
					// Debug
					MPMain.LogError(
						"[MPCore] 场景加载后 CommandConsole 实例仍为 null, 无法注册命令.",
						"[MPCore] After scene loading, the CommandConsole instance is still null; cannot register commands.");
				}
				break;

			case "Main-Menu":
				ResetStateVariables();
				break;

			default:
				ResetStateVariables();
				break;

		}
	}

	/// <summary>
	/// 退出联机模式时重置设置
	/// </summary>
	private void ResetStateVariables() {
		CloseMultiPlayerMode();
		Steamworks.DisconnectAll();
		RPManager.ResetAll();
		MPMain.LogInfo(
			"[MPCore] 所有资源清理完毕",
			"[MPCore] All resources cleaned up");
	}

	/// <summary>
	/// 客户端/主机: 发送本地玩家数据
	/// </summary>
	private void SeedLocalPlayerData() {
		// 限制发送频率(20Hz)
		if (!_playerDataTick.TryTick())
			return;

		var playerData = LocalPlayerManager.CreateLocalPlayerData(Steamworks.UserSteamId);
		if (playerData == null) {
			MPMain.LogError(
				"[LPMan] 本地玩家信息异常",
				"[LPMan] Local player data acquisition exception.");
			return;
		}

		//// Debug
		//playerData.IsTeleport = true;

		// 进行数据写入
		_playerDataWriter.Reset();
		_playerDataWriter.Put((int)PacketType.PlayerDataUpdate);
		MPDataSerializer.WriteToNetData(_playerDataWriter, playerData);

		// 触发Steam数据发送
		// 转为byte[]
		// 使用不可靠+立即发送
		if (Steamworks.IsHost) {
			// 广播所有人
			Steamworks.HandleBroadcast(
				MPDataSerializer.WriterToBytes(_playerDataWriter),
				SendType.Unreliable | SendType.NoNagle);
		} else {
			// 直写到主机
			Steamworks.HandleSendToHost(
				MPDataSerializer.WriterToBytes(_playerDataWriter),
				SendType.Unreliable | SendType.NoNagle);
		}

		//// 获取内部缓冲区及其长度，避免 Copy 为 byte[]
		//byte[] rawBuffer = _playerDataWriter.Data;
		//ushort length = (ushort)_playerDataWriter.Length;

		//if (Steamworks.IsHost) {
		//	SteamNetworkEvents.TriggerBroadcast(rawBuffer, SendType.Unreliable | SendType.NoNagle, length);
		//} else {
		//	SteamNetworkEvents.TriggerSendToHost(rawBuffer, SendType.Unreliable | SendType.NoNagle, length);
		//}

		return;
	}

	/// <summary>
	/// 命令注册
	/// </summary>
	private void RegisterCommands() {
		// 将命令注册到 CommandConsole
		CommandConsole.AddCommand("host", Host);
		CommandConsole.AddCommand("join", Join);
		CommandConsole.AddCommand("leave", Leave);
		CommandConsole.AddCommand("chaos", ChaosMod);
		CommandConsole.AddCommand("getlobbyid", GetLobbyId);
		CommandConsole.AddCommand("test", GetAllConnections);
	}

	// 命令实现
	public void Host(string[] args) {
		if (IsMultiplayerActive) {
			CommandConsole.LogError("You are already in online mode, \n" +
				"please use the leave command to leave and then go online");
			return;
		}
		if (args.Length < 1) {
			CommandConsole.LogError("Usage: host <room_name> [max_players]");
			return;
		}

		string roomName = args[0];
		int maxPlayers = args.Length >= 2 ? int.Parse(args[1]) : 4;
		// Debug
		MPMain.LogInfo(
			$"[MPCore] 正在创建大厅: {roomName}...",
			$"[MPCore] Creating lobby: {roomName}...");

		// 使用协程版本(内部已改为异步)
		Steamworks.CreateRoom(roomName, maxPlayers, (success) => {
			if (success) {
				StartMultiPlayerMode();
				WorldLoader.ReloadWithSeed(new string[] { WorldLoader.instance.seed.ToString() });
			} else {
				CommandConsole.LogError("Fail to create lobby");
			}
		});
	}

	public void Join(string[] args) {
		if (IsMultiplayerActive) {
			CommandConsole.LogError("You are already in online mode, \n" +
				"please use the leave command to leave and then go online");
			return;
		}
		if (args.Length < 1) {
			CommandConsole.LogError("Usage: join <lobby_id>");
			return;
		}

		if (ulong.TryParse(args[0], out ulong lobbyId)) {
			MPMain.LogInfo(
				$"[MPCore] 正在加入大厅: {lobbyId.ToString()}...",
				$"[MPCore] Joining lobby: {lobbyId.ToString()}...");

			Steamworks.JoinRoom(lobbyId, (success) => {
				if (success) {
					//由加载器实现模式切换
					//StartMultiPlayerMode();
					IsMultiplayerActive = true;
				} else {
					CommandConsole.LogError("Fail to join lobby");
				}
			});
		} else {
			CommandConsole.LogError("ForMat error \nUsage: join <lobby_id>");
		}
	}

	public void Leave(string[] args) {
		ResetStateVariables();
		// Debug
		MPMain.LogInfo(
			"[MPCore] 所有连接已断开, 远程玩家已清理.",
			"[MPCore] All connections have been disconnected, remote players have been cleaned up.");
	}

	public void ChaosMod(string[] args) {
		if (args.Length <= 0) {
			IsChaosMod = !IsChaosMod;
		} else {
			try {
				IsChaosMod = TypeConverter.ToBool(args[0]);
			} catch {
				CommandConsole.LogError("Usage: chaos <bool> \nbool value can be: true false 1 0");
			}
		}
	}

	/// <summary>
	/// 获取大厅Id
	/// </summary>
	public void GetLobbyId(string[] args) {
		if (!IsMultiplayerActive) {
			CommandConsole.LogError("Please use this command after online");
			return;
		}
		CommandConsole.Log($"Lobby Id: {Steamworks.GetLobbyId().ToString()}");
	}

	/// <summary>
	/// 调试用,主机获取所有链接
	/// </summary>
	public void GetAllConnections(string[] args) {
		if (!IsMultiplayerActive) {
			CommandConsole.LogError("You need in online mode, \n" +
				"please use the host or join");
			return;
		}
		foreach (var (steamid, connection) in Steamworks._connectedClients) {
			MPMain.LogInfo(
				$"[MPCore] 全部连接 SteamId: {steamid.ToString()} 连接Id: {connection.ToString()}",
				$"[MPCore] All connections SteamId: {steamid.ToString()} connections Id: {connection.ToString()}");
		}
	}

	// 协程请求种子
	public IEnumerator InitHandshakeRoutine() {
		while (!HasInitialized && IsMultiplayerActive) {
			MPMain.LogInfo(
				"[MPCore] 已向主机请求初始化数据",
				"[MPCore] Requested initialization data from the host.");
			var writer = new NetDataWriter();
			writer.Put((int)PacketType.ConnectedToServer);
			var requestData = MPDataSerializer.WriterToBytes(writer);
			Steamworks.HandleSendToHost(requestData);
			yield return new WaitForSeconds(2.0f);
		}
	}

	/// <summary>
	/// 加入大厅
	/// </summary>
	/// <param name="lobby"></param>
	private void ProcessLobbyEntered(Lobby lobby) {
		// Debug
		MPMain.LogInfo(
			$"[MPCore] 正在加入大厅,ID: {lobby.Id.ToString()}",
			$"[MPCore] Joining the lobby, ID: {lobby.Id.ToString()}");

		// 启动多人模式标准, 这里的触发可能先于join回调
		IsMultiplayerActive = true;

		// 启动协程发送请求初始化数据
		if (!Steamworks.IsHost) {
			StartCoroutine(InitHandshakeRoutine());
		}
	}

	/// <summary>
	/// 离开大厅
	/// </summary>
	/// <param name="steamId"></param>
	private void ProcessLobbyMemberLeft(SteamId steamId) {
		// Debug
		MPMain.LogInfo(
			$"[MPCore] 玩家离开大厅: {steamId.ToString()}",
			$"[MPCore] Player left the lobby: {steamId.ToString()}");
	}

	/// <summary>
	/// 大厅所有权变更
	/// </summary>
	private void ProcessLobbyHostChanged(Lobby lobby, SteamId hostId) {
		//// 这里删除旧主机的玩家对象
		//RPManager.DestroyPlayer(hostId);

		//// 处理身份切换
		//if (Steamworks.IsHost) {
		//	// 成为新主机
		//	ProcessIAmNewHost();
		//} else {
		//	// 别人成为新主机,进行连接
		//	Steamworks.ConnectToHost();
		//}
	}


	/// <summary>
	/// 成为新主机时执行
	/// </summary>
	private void ProcessIAmNewHost() {
		// 停止握手协程（以防还在运行）
		if (HasInitialized == false) {
			StopCoroutine(InitHandshakeRoutine());
			//// 要求所有现存客机重新发送一次完整数据
			MPMain.LogInfo(
				"[MPCore] 意外在未初始化的情况下成为主机,需要向现存客户端要求数据",
				"[MPCore] Unexpectedly became the host before initialization was complete; need to request data from existing clients.");
			//var writer = new NetDataWriter();
			//writer.Put((int)PacketType.RequestClientSync);
			//SteamNetworkEvents.TriggerBroadcast(MPDataSerializer.WriterToBytes(writer), SendType.Reliable);
		}
	}

	/// <summary>
	/// 处理玩家接入事件
	/// </summary>
	private void ProcessPlayerConnected(SteamId steamId) {
		// Debug
		MPMain.LogInfo(
			$"[MPCore] 玩家接入: {steamId.ToString()}",
			$"[MPCore] Player connected: {steamId.ToString()}");
		// 创建玩家
		if (Steamworks.IsHost) {
			RPManager.CreatePlayer(steamId);
			var writer = new NetDataWriter();
			writer.Put((int)PacketType.CreatePlayer);
			writer.Put(steamId.Value);
			var data = MPDataSerializer.WriterToBytes(writer);
			Steamworks.HandleBroadcastExcept(steamId, data, SendType.Reliable);
		}
	}

	/// <summary>
	/// 客户端: 创建玩家映射
	/// </summary>
	private void ProcessCreatePlayer(ulong playerId) {
		// 不需要创建自己的映射
		if (playerId == Steamworks.UserSteamId) {
			return;
		}

		MPMain.LogInfo(
			$"[MPCore] 创建玩家映射 Id: {playerId.ToString()}",
			$"[MPCore] Create player Id: {playerId.ToString()}");
		RPManager.CreatePlayer(playerId);
	}

	/// <summary>
	/// 主机: 处理玩家断连 客户端: 对可能的主机离线进行删除
	/// </summary>
	private void ProcessPlayerDisconnected(SteamId steamId) {
		// Debug
		MPMain.LogInfo(
			$"[MPCore] 玩家断连: {steamId.ToString()}",
			$"[MPCore] Player disconnected: {steamId.ToString()}");
		// 如果是主机 删除玩家映射并广播
		if (Steamworks.IsHost) {
			RPManager.DestroyPlayer(steamId.Value);
			var writer = new NetDataWriter();
			writer.Put((int)PacketType.DestroyPlayer);
			writer.Put(steamId.Value);
			var data = MPDataSerializer.WriterToBytes(writer);
			Steamworks.HandleBroadcastExcept(steamId, data, SendType.Reliable);
		}
	}


	/// <summary>
	/// 客户端: 销毁玩家映射
	/// </summary>
	private void ProcessDestroyPlayer(ulong playerId) {
		// 自己已经离线,不需要再销毁
		//if (playerId == Steamworks.MySteamId)
		//	return;

		MPMain.LogInfo(
			$"[MPCore] 销毁玩家映射 Id: {playerId.ToString()}",
			$"[MPCore] Destroy player Id: {playerId.ToString()}");
		RPManager.DestroyPlayer(playerId);
	}

	/// <summary>
	/// 处理网络接收数据
	/// </summary>
	/// <param name="playId"></param>
	/// <param name="data"></param>
	private void ProcessReceiveData(ulong playId, byte[] data) {
		//// Debug
		//if (_debugTick.Test()) {
		//	MPMain.Logger.LogInfo($"[MPCore Process] 接收数据");
		//}

		// 基本验证：确保数据足够读取一个整数(数据包类型)
		var reader = MPDataSerializer.BytesToReader(data);
		PacketType packetType = (PacketType)reader.GetInt();

		switch (packetType) {
			// 连接成功通知 发送世界种子等初始化数据
			// 仅主机使用
			case PacketType.ConnectedToServer:
				SendInitializationData(playId);
				break;
			// 联机世界初始化
			case PacketType.InitializeWorld:
				InitializationMultiMod(reader);
				break;
			// 创建玩家映射
			case PacketType.CreatePlayer:
				ProcessCreatePlayer(reader.GetULong());
				break;
			// 销毁玩家映射
			case PacketType.DestroyPlayer:
				ProcessDestroyPlayer(reader.GetULong());
				break;
			// 玩家数据更新
			case PacketType.PlayerDataUpdate:
				ProcessPlayerDataUpdate(reader);
				break;
		}
	}

	/// <summary>
	/// 主机/客户端: 处理玩家数据更新
	/// </summary>
	private void ProcessPlayerDataUpdate(NetDataReader reader) {
		// 如果是从转发给自己的,忽略
		var playerData = MPDataSerializer.ReadFromNetData(reader);
		var playId = playerData.playId;
		if (playId == Steamworks.UserSteamId) {
			return;
		}

		RPManager.ProcessPlayerData(playId, playerData);
		// 是主机,广播所有人
		if (Steamworks.IsHost) {
			var writer = new NetDataWriter();
			writer.Put((int)PacketType.PlayerDataUpdate);
			MPDataSerializer.WriteToNetData(writer, playerData);
			Steamworks.HandleBroadcastExcept(
				playId, MPDataSerializer.WriterToBytes(writer),
				SendType.Unreliable | SendType.NoNagle);
		}
	}

	/// <summary>
	/// 主机: 发送初始化数据给新玩家
	/// </summary>
	/// <todo>将writer的拷贝byte[]改成byte[]视图,实现零拷贝</todo>
	private void SendInitializationData(SteamId steamId) {
		// 发送世界种子
		var writer = new NetDataWriter();
		writer.Put((int)PacketType.InitializeWorld);
		writer.Put(WorldLoader.instance.seed);

		// 已存在的玩家数
		writer.Put(RPManager.Players.Count);

		writer.Put(Steamworks.UserSteamId);
		WriteToNetData(writer, LocalPlayerManager.CreateLocalPlayerData(Steamworks.UserSteamId));

		// 发送已存在玩家数据
		foreach (var (playerId, playerData) in RPManager.Players) {
			// 如果映射玩家的Id是其自己,跳过发送
			if (steamId == playerId)
				continue;
			writer.Put(playerId);
			WriteToNetData(writer, playerData.PlayerData);
		}

		// 可以添加其他初始化数据,如游戏状态、物品状态等

		var seedData = MPDataSerializer.WriterToBytes(writer);
		Steamworks.HandleSendToPeer(steamId, seedData, SendType.Reliable);
		// Debug
		MPMain.LogInfo(
			"[MPCore] 已向新玩家发送初始化数据",
			"[MPCore] Initialization data has been sent to the new player.");
	}

	/// <summary>
	/// 客户端: 新加入玩家,加载世界种子和已存在玩家
	/// </summary>
	private void InitializationMultiMod(NetDataReader reader) {
		// 获取种子
		int seed = reader.GetInt();

		// 获取玩家数
		int playerCount = reader.GetInt();

		StartMultiPlayerMode();

		// Debug
		MPMain.LogInfo(
			$"[MPCore] 加载世界, 种子号: {seed.ToString()}",
			$"[MPCore] Loaging world, seed: {seed.ToString()}");
		WorldLoader.ReloadWithSeed(new string[] { seed.ToString() });

		for (int i = 0; i < playerCount; i++) {
			ulong playerId = reader.GetULong(); // 记得使用 GetULong 对应 SteamId
			var playerData = MPDataSerializer.ReadFromNetData(reader);
			RPManager.CreatePlayer(playerId);
			// 调用你的玩家管理器进行创建
			RPManager.ProcessPlayerData(playerId, playerData);
			MPMain.LogInfo(
				$"[MPCore] 创建已存在的玩家 Id: {playerId.ToString()}",
				$"[MPCore] Create an existing player Id: {playerId.ToString()}");
		}
	}

	// 开启多人联机模式
	public static void StartMultiPlayerMode() {
		IsMultiplayerActive = true;
		HasInitialized = true;
	}
	// 关闭多人联机模式
	public static void CloseMultiPlayerMode() {
		IsMultiplayerActive = false;
		HasInitialized = false;
	}
}
