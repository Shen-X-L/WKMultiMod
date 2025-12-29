
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
using static System.Buffers.Binary.BinaryPrimitives;
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
	private TickTimer _teleport = new TickTimer(0.5f);

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
		SteamNetworkEvents.OnReceiveData += HandleReceiveData;

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
		SteamNetworkEvents.OnReceiveData -= HandleReceiveData;

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

		// 使用tp命令会重设计时器
		// 如果计时器到时间,设为不传送
		playerData.IsTeleport = !_teleport.IsTickReached;


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
		CommandConsole.AddCommand("talk", Talk);
		CommandConsole.AddCommand("tpto", TpToPlayer);

	}

	// 命令实现
	/// <summary>
	/// 创建大厅
	/// </summary>
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

	/// <summary>
	/// 加入大厅
	/// </summary>
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

	/// <summary>
	/// 离开大厅
	/// </summary>
	public void Leave(string[] args) {
		ResetStateVariables();
		// Debug
		MPMain.LogInfo(
			"[MPCore] 所有连接已断开, 远程玩家已清理.",
			"[MPCore] All connections have been disconnected, remote players have been cleaned up.");
	}

	/// <summary>
	/// 没什么用
	/// </summary>
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
	/// 发送信息
	/// </summary>
	public void Talk(string[] args) {
		if (!IsMultiplayerActive) {
			CommandConsole.LogError("You need in online mode, \n" +
				"please use the host or join");
			return;
		}
		// 将参数数组组合成一个字符串
		string message = string.Join(" ", args);

		NetDataWriter writer = new NetDataWriter();
		writer.Put((int)PacketType.BroadcastMessage);
		writer.Put(Steamworks.UserSteamId);
		writer.Put(message); // 自动处理长度和编码
		var data = MPDataSerializer.WriterToBytes(writer);

		// 发送给所有人
		if (Steamworks.IsHost) {
			Steamworks.HandleBroadcast(data, SendType.Reliable);
		} else {
			Steamworks.HandleSendToHost(data, SendType.Reliable);
		}

	}

	/// <summary>
	/// 向某人TP
	/// </summary>
	public void TpToPlayer(string[] args) {
		if (!IsMultiplayerActive) {
			CommandConsole.LogError("You need in online mode, \n" +
				"please use the host or join");
			return;
		}
		if (ulong.TryParse(args[0], out ulong playerId)) {
			var ids = DictionaryExtensions.FindByKeySuffix(RPManager.Players, playerId);
			// 未找到对应id
			if (ids.Count == 0) {
				CommandConsole.LogError("Target ID not found. This command uses suffix matching.\n" +
					"Example: Target ID: 76561198279116422 → tpto 6422.");
				return;
			}
			// 找到多个对应id
			if (ids.Count > 1) {
				string idStr = string.Join("\n", ids);
				CommandConsole.LogError(
					"Found multiple matching IDs. Below is the corresponding list:\n" + idStr);
				return;
			}
			// 找到对应id,发出传送请求
			var writer = new NetDataWriter();
			writer.Put((int)PacketType.PlayerTeleport);
			writer.Put(Steamworks.UserSteamId);
			writer.Put(ids[0]);

			var sendData = MPDataSerializer.WriterToBytes(writer);

			if (Steamworks.IsHost) {
				// 是主机,直接发送
				Steamworks.HandleSendToPeer(ids[0], sendData, SendType.Reliable);
			} else {
				// 不是主机,请求转发
				Steamworks.HandleSendToHost(sendData, SendType.Reliable);
			}
		}
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

	/// <summary>
	/// 加入大厅回调
	/// </summary>
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
	/// 离开大厅回调
	/// </summary>
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
		//} 
	}

	/// <summary>
	/// 成为新主机时执行
	/// </summary>
	private void ProcessIAmNewHost() {
		// 停止握手协程（以防还在运行）
		if (HasInitialized == false) {
			StopCoroutine(InitHandshakeRoutine());
			// 要求所有现存客机重新发送一次完整数据
			MPMain.LogInfo(
				"[MPCore] 意外在未初始化的情况下成为主机,需要向现存客户端要求数据",
				"[MPCore] Unexpectedly became the host before initialization was complete; need to request data from existing clients.");
			//var writer = new NetDataWriter();
			//writer.Put((int)PacketType.RequestClientSync);
			//SteamNetworkEvents.TriggerBroadcast(MPDataSerializer.WriterToBytes(writer), SendType.Reliable);
		}
	}

	/// <summary>
	/// 客户端发送WorldInitRequest: 协程请求初始化数据
	/// </summary>
	public IEnumerator InitHandshakeRoutine() {
		while (!HasInitialized && IsMultiplayerActive) {
			MPMain.LogInfo(
				"[MPCore] 已向主机请求初始化数据",
				"[MPCore] Requested initialization data from the host.");
			var writer = new NetDataWriter();
			writer.Put((int)PacketType.WorldInitRequest);
			var requestData = MPDataSerializer.WriterToBytes(writer);
			Steamworks.HandleSendToHost(requestData);
			yield return new WaitForSeconds(2.0f);
		}
	}

	/// <summary>
	/// 主机接收WorldInitRequest: 请求初始化数据
	/// 发送WorldInitData: 初始化数据给新玩家
	/// </summary>
	/// <todo>将writer的拷贝byte[]改成byte[]视图,实现零拷贝</todo>
	private void HandleWorldInitRequest(SteamId steamId) {
		// 发送世界种子
		var writer = new NetDataWriter();
		writer.Put((int)PacketType.WorldInitData);
		writer.Put(WorldLoader.instance.seed);

		// 玩家列表
		List<ulong> playerList = new List<ulong>();
		// 主机本身
		playerList.Add(Steamworks.UserSteamId);
		// 发送已存在玩家数据
		foreach (var playerId in Steamworks._connectedClients.Keys) {
			// 如果映射玩家的Id是其自己,跳过发送
			if (steamId == playerId)
				continue;
			playerList.Add(playerId);
		}

		writer.Put(playerList.Count);
		foreach (var playerId in playerList) {
			writer.Put(playerId);
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
	/// 客户端接收WorldInitData: 新加入玩家,加载世界种子和已存在玩家
	/// </summary>
	private void HandleWorldInit(NetDataReader reader) {
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
			RPManager.PlayerCreate(playerId);
			MPMain.LogInfo(
				$"[MPCore] 创建已存在的玩家 Id: {playerId.ToString()}",
				$"[MPCore] Create an existing player Id: {playerId.ToString()}");
		}
	}

	/// <summary>
	/// 主机接收事件总线OnPlayerConnected 发送PlayerCreate: 处理玩家接入事件
	/// </summary>
	private void ProcessPlayerConnected(SteamId steamId) {
		// Debug
		MPMain.LogInfo(
			$"[MPCore] 玩家接入: {steamId.ToString()}",
			$"[MPCore] Player connected: {steamId.ToString()}");
		// 创建玩家
		if (Steamworks.IsHost) {
			RPManager.PlayerCreate(steamId);
			var writer = new NetDataWriter();
			writer.Put((int)PacketType.PlayerCreate);
			writer.Put(steamId.Value);
			var data = MPDataSerializer.WriterToBytes(writer);
			Steamworks.HandleBroadcastExcept(steamId, data, SendType.Reliable);
		}
	}

	/// <summary>
	/// 客户端接收PlayerCreate: 创建玩家映射
	/// </summary>
	private void HandlePlayerCreate(ulong playerId) {
		// 不需要创建自己的映射
		if (playerId == Steamworks.UserSteamId) {
			return;
		}

		MPMain.LogInfo(
			$"[MPCore] 创建玩家映射 Id: {playerId.ToString()}",
			$"[MPCore] Create player Id: {playerId.ToString()}");
		RPManager.PlayerCreate(playerId);
	}

	/// <summary>
	/// 主机事件总线OnPlayerDisconnected 发送PlayerRemove: 处理玩家断连 
	/// 客户端事件总线OnPlayerDisconnected: 对可能的主机离线进行删除
	/// </summary>
	private void ProcessPlayerDisconnected(SteamId steamId) {
		// Debug
		MPMain.LogInfo(
			$"[MPCore] 玩家断连: {steamId.ToString()}",
			$"[MPCore] Player disconnected: {steamId.ToString()}");
		RPManager.PlayerRemove(steamId.Value);
		// 如果是主机 广播删除玩家映射
		if (Steamworks.IsHost) {
			var writer = new NetDataWriter();
			writer.Put((int)PacketType.PlayerRemove);
			writer.Put(steamId.Value);
			var data = MPDataSerializer.WriterToBytes(writer);
			Steamworks.HandleBroadcastExcept(steamId, data, SendType.Reliable);
		}
	}

	/// <summary>
	/// 客户端接收PlayerRemove: 销毁玩家映射
	/// </summary>
	private void HandlePlayerRemove(ulong playerId) {
		// 自己已经离线,不需要再销毁
		//if (playerId == Steamworks.UserSteamId)
		//	return;

		MPMain.LogInfo(
			$"[MPCore] 销毁玩家映射 Id: {playerId.ToString()}",
			$"[MPCore] Destroy player Id: {playerId.ToString()}");
		RPManager.PlayerRemove(playerId);
	}

	/// <summary>
	/// 主机接收PlayerDataUpdate 发送PlayerDataUpdate: 处理玩家数据更新
	/// 客户端接收PlayerDataUpdate: 处理玩家数据更新
	/// </summary>
	private void HandlePlayerDataUpdate(NetDataReader reader) {
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
	/// 主机接收BroadcastMessage 发送BroadcastMessage: 处理玩家标签更新
	/// 客户端接收BroadcastMessage: 处理玩家标签更新
	/// </summary>
	private void ProcessPlayerTagUpdate(ArraySegment<byte> payload) {
		var reader = new NetDataReader(payload.Array, payload.Offset, payload.Count);

		ulong playerId = reader.GetULong(); // 读取具体 ID
		string msg = reader.GetString();    // 读取消息

		CommandConsole.Log($"{playerId}: {msg}");
		// 控制台目前不支持中文
		//string playerName = new Friend(playerId).Name;
		//CommandConsole.Log($"{playerName}: {msg}");
		RPManager.Players[playerId].UpdateNameTag(msg);

		// 是主机,广播除发送者外所有人
		if (Steamworks.IsHost) {
			Broadcast(PacketType.BroadcastMessage, playerId, payload);
		}
	}

	/// <summary>
	/// 主机接收PlayerTeleport 目标非本机转发PlayerTeleport
	/// 客户端接收PlayerTeleport: 携带Mess数据发送RespondPlayerTeleport
	/// </summary>
	private void ProcessPlayerTeleport(ArraySegment<byte> payload) {
		// 请求方ID
		ulong requestId = ReadUInt64LittleEndian(payload);
		// 响应方ID
		ulong respondId = ReadUInt64LittleEndian(payload.Slice(8));
		// 不是自己并且不是主机,退出
		if (respondId != Steamworks.UserSteamId && !Steamworks.IsHost)
			return;
		// 不是自己并且是主机,转发
		if (respondId != Steamworks.UserSteamId && Steamworks.IsHost) {
			ForwardToPeer(PacketType.PlayerTeleport, requestId, respondId, payload);
			return;
		}
		// 获取数据
		var deathFloorData = DEN_DeathFloor.instance.GetSaveData();
		var writer = new NetDataWriter();
		writer.Put((int)PacketType.RespondPlayerTeleport);
		writer.Put(respondId);
		writer.Put(requestId);
		writer.Put(deathFloorData.relativeHeight);
		writer.Put(deathFloorData.active);
		writer.Put(deathFloorData.speed);
		writer.Put(deathFloorData.speedMult);
		var data = MPDataSerializer.WriterToBytes(writer);

		if (Steamworks.IsHost) {
			// 是主机 直接发送
			Steamworks.HandleSendToPeer(requestId, data, SendType.Reliable);
		} else {
			// 请求主机转发
			Steamworks.HandleSendToHost(data, SendType.Reliable);
		}
	}

	/// <summary>
	/// 主机接收RespondPlayerTeleport 目标非本机转发RespondPlayerTeleport
	/// 客户端接收RespondPlayerTeleport: 同步Mess数据并传送
	/// </summary>
	private void ProcessRespondPlayerTeleport(NetDataReader reader) {
		// 响应方ID
		var respondId = reader.GetULong();
		// 请求方ID
		var requestId = reader.GetULong();
		// 不是自己并且不是主机,退出
		if (respondId != Steamworks.UserSteamId || !Steamworks.IsHost)
			return;
		// 不是自己并且是主机,转发
		if (respondId != Steamworks.UserSteamId || Steamworks.IsHost) {
			ForwardToPeer(PacketType.PlayerTeleport, respondId, requestId, reader);
			return;
		}

		var deathFloorData = new DEN_DeathFloor.SaveData {
			relativeHeight = reader.GetFloat(),
			active = reader.GetBool(),
			speed = reader.GetFloat(),
			speedMult = reader.GetFloat(),
		};
		// 关闭可击杀效果
		DEN_DeathFloor.instance.SetCanKill(new string[] { "false" });
		// 重设计数器,期间位移视为传送
		_teleport.Reset();
		ENT_Player.GetPlayer().Teleport(RPManager.Players[respondId].PlayerObject.transform.position);
		DEN_DeathFloor.instance.LoadDataFromSave(deathFloorData);
		DEN_DeathFloor.instance.SetCanKill(new string[] { "true" });
	}

	/// <summary>
	/// 转发网络数据包到指定的客户端
	/// </summary>
	/// <param name="packetType">数据包类型</param>
	/// <param name="requestId">请求方ID</param>
	/// <param name="respondId">响应方ID（接收方）</param>
	/// <param name="reader">包含原始数据的读取器</param>
	/// <param name="sendType">发送类型（默认可靠）</param>
	private void ForwardToPeer(PacketType packetType, ulong requestId,
		ulong respondId, NetDataReader reader, SendType sendType = SendType.Reliable) {
		// 创建新的写入器
		var writer = new NetDataWriter();

		// 按照指定顺序写入包头
		writer.Put((int)packetType);
		writer.Put(requestId);
		writer.Put(respondId);

		// 复制原始数据
		// reader 的当前位置到末尾的所有数据
		byte[] originalData = reader.GetRemainingBytes();
		writer.Put(originalData);

		// 转换为字节数组
		byte[] sendData = writer.Data;

		// 发送到目标对等方
		Steamworks.HandleSendToPeer(respondId, sendData, sendType);
	}

	/// <summary>
	/// 转发网络数据包到指定的客户端
	/// </summary>
	/// <param name="packetType">数据包类型</param>
	/// <param name="requestId">请求方ID</param>
	/// <param name="respondId">响应方ID（接收方）</param>
	/// <param name="reader">包含原始数据的读取器</param>
	/// <param name="sendType">发送类型（默认可靠）</param>
	private void ForwardToPeer(PacketType packetType, ulong requestId,
		ulong respondId, ArraySegment<byte> dataSegment, SendType sendType = SendType.Reliable) {
		// 创建新的写入器
		var writer = new NetDataWriter();

		// 按照指定顺序写入包头
		writer.Put((int)packetType);
		writer.Put(dataSegment.Array, dataSegment.Offset, dataSegment.Count);

		// 发送到目标对等方
		Steamworks.HandleSendToPeer(respondId, writer.Data, 0, writer.Length, sendType);
	}

	/// <summary>
	/// 广播数据包到所有客户端 (除了发送者)
	/// </summary>
	/// <param name="packetType">数据包类型</param>
	/// <param name="senderId">发送方ID</param>
	/// <param name="reader">包含原始数据的读取器</param>
	/// <param name="sendType">发送类型(默认可靠)</param>
	public void Broadcast(PacketType packetType, ulong senderId,
		NetDataReader reader, SendType sendType = SendType.Reliable) {
		var writer = new NetDataWriter();
		writer.Put((int)packetType);
		writer.Put(senderId);
		byte[] data = reader.GetRemainingBytes();
		writer.Put(data);
		Steamworks.HandleBroadcastExcept(senderId, writer.Data, sendType);
	}

	/// <summary>
	/// 广播数据包到所有客户端 (除了发送者)
	/// </summary>
	/// <param name="packetType">数据包类型</param>
	/// <param name="senderId">发送方ID</param>
	/// <param name="dataSegment">原始数据的数据端</param>
	/// <param name="sendType">发送类型(默认可靠)</param>
	public void Broadcast(PacketType packetType, ulong senderId,
		ArraySegment<byte> dataSegment, SendType sendType = SendType.Reliable) {
		var writer = new NetDataWriter();
		writer.Put((int)packetType);
		writer.Put(dataSegment.Array, dataSegment.Offset, dataSegment.Count);
		Steamworks.HandleBroadcastExcept(senderId, writer.Data, 0, writer.Length, sendType);
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

	/// <summary>
	/// 处理网络接收数据
	/// </summary>
	/// <param name="playId"></param>
	/// <param name="data"></param>
	private void HandleReceiveData(ulong playId, byte[] data) {
		if (data == null || data.Length < 4) return;

		// 直接解析头部
		ReadOnlySpan<byte> span = data;
		PacketType packetType = (PacketType)ReadInt32LittleEndian(span);

		// 虽然你后续可能还需要转回 ArraySegment 给旧接口，但逻辑很清晰
		var payload = new ArraySegment<byte>(data, 4, data.Length - 4);

		switch (packetType) {
			// 仅主机接收: 请求初始化数据
			case PacketType.WorldInitRequest: {
				HandleWorldInitRequest(playId);
				break;
			}
			// 接收: 联机世界初始化数据
			case PacketType.WorldInitData: {
				var reader = new NetDataReader(payload.Array, payload.Offset, payload.Count);
				HandleWorldInit(reader);
				break;
			}
			// 接收: 创建玩家映射
			case PacketType.PlayerCreate: {
				HandlePlayerCreate(ReadUInt64LittleEndian(span.Slice(4)));
				break;
			}
			// 接收: 销毁玩家映射
			case PacketType.PlayerRemove: {
				HandlePlayerRemove(ReadUInt64LittleEndian(span.Slice(4)));
				break;
			}
			// 接收: 玩家数据更新
			case PacketType.PlayerDataUpdate: {
				var reader = new NetDataReader(payload.Array, payload.Offset, payload.Count);
				HandlePlayerDataUpdate(reader);
				break;
			}
			// 接收: 世界状态同步
			case PacketType.WorldStateSync: {
				break;
			}
			// 接收: 广播消息
			case PacketType.BroadcastMessage: {
				ProcessPlayerTagUpdate(payload);
				break;
			}
			// 接收: 请求传送
			case PacketType.PlayerTeleport: {
				ProcessPlayerTeleport(payload);
				break;
			}
			// 接收: 响应传送
			case PacketType.RespondPlayerTeleport: {
				var reader = new NetDataReader(payload.Array, payload.Offset, payload.Count);
				ProcessRespondPlayerTeleport(reader);
				break;
			}

			default: {
				break;
			}
		}
	}
}
