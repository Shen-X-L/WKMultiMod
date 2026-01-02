
using LiteNetLib.Utils;
using Steamworks;
using Steamworks.Data;
using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using WKMultiMod.src.Data;
using WKMultiMod.src.NetWork;
using WKMultiMod.src.Test;
using WKMultiMod.src.Util;
using static System.Buffers.Binary.BinaryPrimitives;
using static WKMultiMod.src.Util.MPReaderPool;
using static WKMultiMod.src.Util.MPWriterPool;
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
	// 是否在多人模式
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

			// 订阅事件
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
		MPEventBus.Net.OnReceiveData += HandleReceiveData;

		// 订阅大厅事件
		MPEventBus.Net.OnLobbyEntered += ProcessLobbyEntered;
		MPEventBus.Net.OnLobbyMemberJoined += ProcessLobbyMemberJoined;
		MPEventBus.Net.OnLobbyMemberLeft += ProcessLobbyMemberLeft;
		MPEventBus.Net.OnLobbyHostChanged += ProcessLobbyHostChanged;

		// 订阅玩家连接事件
		MPEventBus.Net.OnPlayerConnected += ProcessPlayerConnected;
		MPEventBus.Net.OnPlayerDisconnected += ProcessPlayerDisconnected;

		// 订阅游戏事件
		MPEventBus.Game.OnPlayerDamage += ProcessPlayerDamage;
		MPEventBus.Game.OnPlayerAddForce += ProcessPlayerAddForce;
		MPEventBus.Game.OnPlayerDeath += ResetStateVariables;
	}

	/// <summary>
	/// 取消所有网络事件订阅
	/// </summary>
	private void UnsubscribeFromEvents() {
		// 退订网络数据接收事件
		MPEventBus.Net.OnReceiveData -= HandleReceiveData;

		// 退订大厅事件
		MPEventBus.Net.OnLobbyEntered -= ProcessLobbyEntered;
		MPEventBus.Net.OnLobbyMemberJoined -= ProcessLobbyMemberJoined;
		MPEventBus.Net.OnLobbyMemberLeft -= ProcessLobbyMemberLeft;
		MPEventBus.Net.OnLobbyHostChanged -= ProcessLobbyHostChanged;

		// 退订玩家连接事件
		MPEventBus.Net.OnPlayerConnected -= ProcessPlayerConnected;
		MPEventBus.Net.OnPlayerDisconnected -= ProcessPlayerDisconnected;

		// 退订游戏事件
		MPEventBus.Game.OnPlayerDamage -= ProcessPlayerDamage;
		MPEventBus.Game.OnPlayerAddForce -= ProcessPlayerAddForce;
		MPEventBus.Game.OnPlayerDeath -= ResetStateVariables;
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
			case "Playground":
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

	#region[游戏数据收集处理]
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
		_playerDataWriter.Put(Steamworks.UserSteamId);
		_playerDataWriter.Put(Steamworks.BroadcastId);
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

		//// 获取内部缓冲区及其长度,避免 Copy 为 byte[]
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
	/// 客户端/主机: 发送伤害其他玩家数据
	/// </summary>
	private void ProcessPlayerDamage(ulong steamId, float amount, string type) {
		var writer = GetWriter();
		writer.Put(Steamworks.UserSteamId);
		writer.Put(steamId);
		writer.Put((int)PacketType.PlayerDamage);
		writer.Put(amount);
		writer.Put(type);
		var data = MPDataSerializer.WriterToBytes(writer);
		Steamworks.Send(steamId, data, SendType.Reliable);
	}

	/// <summary>
	/// 客户端/主机: 发送基于其他玩家冲击力数据
	/// </summary>
	private void ProcessPlayerAddForce(ulong steamId, Vector3 force, string source) {
		var writer = GetWriter();
		writer.Put(Steamworks.UserSteamId);
		writer.Put(steamId);
		writer.Put((int)PacketType.PlayerAddForce);
		writer.Put(force.x);
		writer.Put(force.y);
		writer.Put(force.z);
		writer.Put(source);
		var data = MPDataSerializer.WriterToBytes(writer);
		Steamworks.Send(steamId, data, SendType.Reliable);
	}

	#endregion

	#region[命令注册]

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
		CommandConsole.AddCommand("getconnections", GetAllConnections);
		CommandConsole.AddCommand("talk", Talk);
		CommandConsole.AddCommand("tpto", TpToPlayer);
		CommandConsole.AddCommand("test", Test.Test.Main, false);
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

		NetDataWriter writer = GetWriter();
		writer.Put(Steamworks.UserSteamId);
		writer.Put(Steamworks.BroadcastId);
		writer.Put((int)PacketType.BroadcastMessage);

		// 自动处理长度和编码
		writer.Put(message);
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
			var writer = GetWriter();
			writer.Put(Steamworks.UserSteamId);
			writer.Put(ids[0]);
			writer.Put((int)PacketType.PlayerTeleport);

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
	#endregion

	#region [大厅事件处理]
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
	/// 有其他玩家加入大厅回调 创建玩家映射
	/// </summary>
	private void ProcessLobbyMemberJoined(SteamId steamId) {
		if (steamId == Steamworks.UserSteamId) {
			return;
		}

		MPMain.LogInfo(
			$"[MPCore] 创建玩家映射 Id: {steamId.ToString()}",
			$"[MPCore] Create player Id: {steamId.ToString()}");
		RPManager.PlayerCreate(steamId);
	}

	/// <summary>
	/// 有其他玩家离开大厅回调 删除玩家映射
	/// </summary>
	private void ProcessLobbyMemberLeft(SteamId steamId) {
		// Debug
		// 自己已经离线,不需要再销毁
		//if (playerId == Steamworks.UserSteamId)
		//	return;

		MPMain.LogInfo(
			$"[MPCore] 销毁玩家映射 Id: {steamId.ToString()}",
			$"[MPCore] Destroy player Id: {steamId.ToString()}");
	}

	/// <summary>
	/// 大厅所有权变更
	/// </summary>
	private void ProcessLobbyHostChanged(Lobby lobby, SteamId hostId) {
		// 这里删除旧主机的玩家对象
		RPManager.PlayerRemove(hostId);

		// 处理身份切换
		if (Steamworks.IsHost) {
			// 成为新主机
			ProcessIAmNewHost();
		}
	}

	/// <summary>
	/// 成为新主机时执行
	/// </summary>
	private void ProcessIAmNewHost() {
		// 停止握手协程(以防还在运行)
		if (HasInitialized == false) {
			StopCoroutine(InitHandshakeRoutine());
			// 要求所有现存客机重新发送一次完整数据
			MPMain.LogInfo(
				"[MPCore] 意外在未初始化的情况下成为主机,需要向现存客户端要求数据",
				"[MPCore] Unexpectedly became the host before initialization was complete; need to request data from existing clients.");
			//var writer = GetWriter();
			//writer.Put((int)PacketType.RequestClientSync);
			//SteamNetworkEvents.TriggerBroadcast(MPDataSerializer.WriterToBytes(writer), SendType.Reliable);
		}
    }
	#endregion

	#region [网络数据处理]
	/// <summary>
	/// 客户端发送WorldInitRequest: 协程请求初始化数据
	/// </summary>
	public IEnumerator InitHandshakeRoutine() {
		while (!HasInitialized && IsMultiplayerActive) {
			MPMain.LogInfo(
				"[MPCore] 已向主机请求初始化数据",
				"[MPCore] Requested initialization data from the host.");
			var writer = GetWriter();
			writer.Put(Steamworks.UserSteamId);
			writer.Put(Steamworks.HostSteamId);
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
		var writer = GetWriter();
		writer.Put(Steamworks.UserSteamId);
		writer.Put(steamId);
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
		Steamworks.Send(steamId, seedData, SendType.Reliable);
		// Debug
		MPMain.LogInfo(
			"[MPCore] 已向新玩家发送初始化数据",
			"[MPCore] Initialization data has been sent to the new player.");
	}

	/// <summary>
	/// 客户端接收WorldInitData: 新加入玩家,加载世界种子和已存在玩家
	/// </summary>
	private void HandleWorldInit(ArraySegment<byte> payload) {
		var reader = GetReader(payload);

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
		//// Debug
		//MPMain.LogInfo(
		//	$"[MPCore] 玩家接入: {steamId.ToString()}",
		//	$"[MPCore] Player connected: {steamId.ToString()}");
		//// 创建玩家
		//if (Steamworks.IsHost) {
		//	RPManager.PlayerCreate(steamId);
		//	var writer = GetWriter();
		//	writer.Put(Steamworks.UserSteamId);
		//	writer.Put(Steamworks.BroadcastId);
		//	writer.Put((int)PacketType.PlayerCreate);
		//	writer.Put(steamId.Value);
		//	var data = MPDataSerializer.WriterToBytes(writer);
		//	Steamworks.HandleBroadcastExcept(steamId, data, SendType.Reliable);
		//}
	}

	/// <summary>
	/// 客户端接收PlayerCreate: 创建玩家映射
	/// </summary>
	private void HandlePlayerCreate(ulong playerId) {
		// 不需要创建自己的映射
		//if (playerId == Steamworks.UserSteamId) {
		//	return;
		//}

		//MPMain.LogInfo(
		//	$"[MPCore] 创建玩家映射 Id: {playerId.ToString()}",
		//	$"[MPCore] Create player Id: {playerId.ToString()}");
		//RPManager.PlayerCreate(playerId);
	}

	/// <summary>
	/// 主机事件总线OnPlayerDisconnected 发送PlayerRemove: 处理玩家断连 
	/// 客户端事件总线OnPlayerDisconnected: 对可能的主机离线进行删除
	/// </summary>
	private void ProcessPlayerDisconnected(SteamId steamId) {
		//// Debug
		//MPMain.LogInfo(
		//	$"[MPCore] 玩家断连: {steamId.ToString()}",
		//	$"[MPCore] Player disconnected: {steamId.ToString()}");
		//RPManager.PlayerRemove(steamId.Value);
		//// 如果是主机 广播删除玩家映射
		//if (Steamworks.IsHost) {
		//	var writer = GetWriter();
		//	writer.Put(Steamworks.UserSteamId);
		//	writer.Put(Steamworks.BroadcastId);
		//	writer.Put((int)PacketType.PlayerRemove);
		//	writer.Put(steamId.Value);
		//	var data = MPDataSerializer.WriterToBytes(writer);
		//	Steamworks.HandleBroadcastExcept(steamId, data, SendType.Reliable);
		//}
	}

	/// <summary>
	/// 客户端接收PlayerRemove: 销毁玩家映射
	/// </summary>
	private void HandlePlayerRemove(ulong playerId) {
		// 自己已经离线,不需要再销毁
		//if (playerId == Steamworks.UserSteamId)
		//	return;

		//MPMain.LogInfo(
		//	$"[MPCore] 销毁玩家映射 Id: {playerId.ToString()}",
		//	$"[MPCore] Destroy player Id: {playerId.ToString()}");
		//RPManager.PlayerRemove(playerId);
	}

	/// <summary>
	/// 主机/客户端接收PlayerDataUpdate: 处理玩家数据更新
	/// </summary>
	private void HandlePlayerDataUpdate(ArraySegment<byte> payload) {
		var reader = GetReader(payload);

		// 如果是从转发给自己的,忽略
		var playerData = MPDataSerializer.ReadFromNetData(reader);
		var playId = playerData.playId;
		if (playId == Steamworks.UserSteamId) {
			return;
		}
		RPManager.ProcessPlayerData(playId, playerData);
	}

	/// <summary>
	/// 主机/客户端接收BroadcastMessage: 处理玩家标签更新
	/// </summary>
	private void HandlePlayerTagUpdate(ulong senderId, ArraySegment<byte> payload) {
		var reader = GetReader(payload);

		string msg = reader.GetString();    // 读取消息

		CommandConsole.Log($"{senderId}: {msg}");
		// 控制台目前不支持中文
		//string playerName = new Friend(playerId).Name;
		//CommandConsole.Log($"{playerName}: {msg}");
		RPManager.Players[senderId].UpdateNameTag(msg);
	}

	/// <summary>
	/// 主机/客户端接收PlayerDamage: 受到伤害
	/// </summary>
	private void HandlePlayerDamage(ulong senderId, ArraySegment<byte> payload) {
		var reader = GetReader(payload);
		float amount = reader.GetFloat();
		string type = reader.GetString();
		var player = senderId;
		var baseDamage = amount * MPConfig.AllActive;
		switch (type) {
			case "Hammer":
				ENT_Player.GetPlayer().Damage(baseDamage * MPConfig.HammerActive, type);
				break;
			case "rebar":
				ENT_Player.GetPlayer().Damage(baseDamage * MPConfig.RebarActive, type);
				break;
			case "returnrebar":
				ENT_Player.GetPlayer().Damage(baseDamage * MPConfig.ReturnRebarActive, type);
				break;
			case "rebarexplosion":
				ENT_Player.GetPlayer().Damage(baseDamage * MPConfig.RebarExplosionActive, type);
				break;
			case "explosion":
				ENT_Player.GetPlayer().Damage(baseDamage * MPConfig.RebarExplosionActive, type);
				break;
			case "piton":
				ENT_Player.GetPlayer().Damage(baseDamage * MPConfig.PitonActive, type);
				break;
			case "flare":
				ENT_Player.GetPlayer().Damage(baseDamage * MPConfig.FlareActive, type);
				break;
			case "ice":
				ENT_Player.GetPlayer().Damage(baseDamage * MPConfig.IceActive, type);
				break;
			default:
				ENT_Player.GetPlayer().Damage(baseDamage * MPConfig.OtherActive, type);
				break;
		}
	}

	/// <summary>
	/// 主机/客户端接收PlayerAddForce: 受到冲击力
	/// </summary>
	private void HandlePlayerAddForce(ulong senderId, ArraySegment<byte> payload) {
		var reader = GetReader(payload);
		Vector3 force = new Vector3 {
			x = reader.GetFloat(),
			y = reader.GetFloat(),
			z = reader.GetFloat(),
		};
		string source = reader.GetString();
		ENT_Player.GetPlayer().AddForce(force, source);
	}

	/// <summary>
	/// 主机/客户端接收PlayerTeleport
	/// 发送RespondPlayerTeleport: 携带Mess数据
	/// </summary>
	private void HandlePlayerTeleport(ulong senderId, ArraySegment<byte> payload) {
		// 获取数据
		var deathFloorData = DEN_DeathFloor.instance.GetSaveData();
		var position = ENT_Player.GetPlayer().transform.position;
		var writer = GetWriter();
		writer.Put(Steamworks.UserSteamId);// 作为接收方重新变成发送方
		writer.Put(senderId);// 作为发送方重新变成接收方
		writer.Put((int)PacketType.RespondPlayerTeleport);
		writer.Put(deathFloorData.relativeHeight);
		writer.Put(deathFloorData.active);
		writer.Put(deathFloorData.speed);
		writer.Put(deathFloorData.speedMult);
		writer.Put(position.x);
		writer.Put(position.y);
		writer.Put(position.z);
		var data = MPDataSerializer.WriterToBytes(writer);

		Steamworks.Send(senderId, data, SendType.Reliable);
	}

	/// <summary>
	/// 主机/客户端接收RespondPlayerTeleport: 同步Mess数据并传送
	/// </summary>
	private void HandleRespondPlayerTeleport(ulong senderId, ArraySegment<byte> payload) {
		var reader = GetReader(payload);

		var deathFloorData = new DEN_DeathFloor.SaveData {
			relativeHeight = reader.GetFloat(),
			active = reader.GetBool(),
			speed = reader.GetFloat(),
			speedMult = reader.GetFloat(),
		};
		var position = new Vector3 {
			x = reader.GetFloat(),
			y = reader.GetFloat(),
			z = reader.GetFloat(),
		};
		// 关闭可击杀效果
		DEN_DeathFloor.instance.SetCanKill(new string[] { "false" });
		// 重设计数器,期间位移视为传送
		_teleport.Reset();
		ENT_Player.GetPlayer().Teleport(position);
		DEN_DeathFloor.instance.LoadDataFromSave(deathFloorData);
		DEN_DeathFloor.instance.SetCanKill(new string[] { "true" });
	}

	/// <summary>
	/// 处理网络接收数据
	/// </summary>
	/// <param name="connectionId"></param>
	/// <param name="data"></param>
	private void HandleReceiveData(ulong connectionId, ArraySegment<byte> data) {
		if (data.Array == null || data.Count < 20) return;

		// 直接解析头部
		ReadOnlySpan<byte> span = data;
		// 发送方ID
		ulong senderId = ReadUInt64LittleEndian(span);
		// 接收方ID
		ulong targetId = ReadUInt64LittleEndian(span.Slice(8));

		// 主机特权：分拣转发
		if (Steamworks.IsHost) {
			// 验证：如果发件人 ID 和物理连接 ID 对不上,可能是伪造包
			if (senderId != connectionId) return;

			// 转发：目标不是我,也不是广播
			if (targetId != Steamworks.UserSteamId && targetId != Steamworks.BroadcastId) {
				ForwardToPeer(targetId, data);
				return; // 结束
			}

			// 广播：如果是广播,且不是我发出的
			if (targetId == Steamworks.BroadcastId && connectionId != Steamworks.UserSteamId) {
				BroadcastExcept(senderId, data);
				// 继续往下走,因为主机也要处理广播包
			}
		}

		// 包类型
		PacketType packetType = (PacketType)ReadInt32LittleEndian(span.Slice(16));
		// 包具体数据
		var payload = data.Slice(20);

		switch (packetType) {
			// 仅主机接收: 请求初始化数据
			case PacketType.WorldInitRequest: {
				HandleWorldInitRequest(senderId);
				break;
			}
			// 接收: 联机世界初始化数据
			case PacketType.WorldInitData: {
				HandleWorldInit(payload);
				break;
			}
			// 接收: 创建玩家映射
			case PacketType.PlayerCreate: {
				//if (payload.Count >= 8) {
				//	ulong newPlayerId = ReadUInt64LittleEndian(payload);
				//	HandlePlayerCreate(newPlayerId);
				//} else {
				//	// 处理异常情况：载荷长度不足
				//	MPMain.LogError(
				//		"[MPCore] PlayerCreate 包数据长度不足 8 字节",
				//		"[MPCore] PlayerCreate packet data length is less than 8 bytes.");
				//}
				break;
			}
			// 接收: 销毁玩家映射
			case PacketType.PlayerRemove: {
				//if (payload.Count >= 8) {
				//	HandlePlayerRemove(ReadUInt64LittleEndian(payload));
				//} else {
				//	// 处理异常情况：载荷长度不足
				//	MPMain.LogError(
				//		"[MPCore] PlayerRemove 包数据长度不足 8 字节",
				//		"[MPCore] PlayerRemove packet data length is less than 8 bytes.");
				//}
				break;
			}
			// 接收: 玩家数据更新
			case PacketType.PlayerDataUpdate: {
				HandlePlayerDataUpdate(payload);
				break;
			}
			// 接收: 世界状态同步
			case PacketType.WorldStateSync: {
				break;
			}
			// 接收: 广播消息
			case PacketType.BroadcastMessage: {
				HandlePlayerTagUpdate(senderId, payload);
				break;
			}
			// 接收: 受到伤害
			case PacketType.PlayerDamage: {
				HandlePlayerDamage(senderId, payload);
				break;
			}
			// 接收: 受到冲击力
			case PacketType.PlayerAddForce: {
				HandlePlayerAddForce(senderId, payload);
				break;
			}

			// 接收: 请求传送
			case PacketType.PlayerTeleport: {
				HandlePlayerTeleport(senderId, payload);
				break;
			}
			// 接收: 响应传送
			case PacketType.RespondPlayerTeleport: {
				HandleRespondPlayerTeleport(senderId, payload);
				break;
			}

			default: {
				break;
			}
		}
	}
	#endregion

	#region[网络发送工具类]
	/// <summary>
	/// 转发网络数据包到指定的客户端
	/// </summary>
	private void ForwardToPeer(ulong targetId, ArraySegment<byte> data) {
		// 直接从 segment 获取偏移和长度
		int offset = data.Offset;
		int count = data.Count;
		// 解析类型
		PacketType type = (PacketType)BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(16, 4));
		SendType st = (type == PacketType.PlayerDataUpdate)
			? SendType.Unreliable : SendType.Reliable;

		Steamworks.HandleSendToPeer(targetId, data.Array, st);
	}

	/// <summary>
	/// 广播数据包到所有客户端
	/// </summary>
	public void Broadcast(ArraySegment<byte> data) {
		// 直接从 segment 获取偏移和长度
		int offset = data.Offset;
		int count = data.Count;

		// 解析类型
		PacketType type = (PacketType)BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(16, 4));

		SendType st = (type == PacketType.PlayerDataUpdate)
			? SendType.Unreliable : SendType.Reliable;

		// 将全套参数传给底层
		Steamworks.HandleBroadcast(data.Array, offset, count, st);
	}

	/// <summary>
	/// 广播数据包到所有客户端 (除了发送者)
	/// </summary>
	/// <param name="senderId">发送方ID</param>
	public void BroadcastExcept(ulong senderId, ArraySegment<byte> data) {
		// 直接从 segment 获取偏移和长度
		int offset = data.Offset;
		int count = data.Count;

		// 解析类型
		PacketType type = (PacketType)BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(16, 4));

		SendType st = (type == PacketType.PlayerDataUpdate)
			? SendType.Unreliable : SendType.Reliable;

		// 将全套参数传给底层
		Steamworks.HandleBroadcastExcept(senderId, data.Array, offset, count, st);
	}
	#endregion
}
