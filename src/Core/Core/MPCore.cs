
using Steamworks;
using Steamworks.Data;
using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using WKMPMod.Component;
using WKMPMod.Data;
using WKMPMod.NetWork;
using WKMPMod.RemoteManager;
using WKMPMod.Util;
using static System.Buffers.Binary.BinaryPrimitives;
using static WKMPMod.Data.MPReaderPool;
using static WKMPMod.Data.MPWriterPool;
namespace WKMPMod.Core;

[Flags]
public enum MPStatus {
	NotInitialized = 0b0,    // 未初始化
	Initialized = 0b1,       // 已初始化

	NotInLobby = 0b00_0,     // 未加入大厅
	JoiningLobby = 0b01_0,   // 正在加入大厅
	InLobby = 0b10_0,        // 已加入大厅
	LobbyConnectionError = 0b11_0,// 大厅连接错误

	INIT_MASK = 0b1,    // 初始化掩码
	LOBBY_MASK = 0b11_0,// 大厅状态掩码
}
public static class MPStatusExtension {
	// 设置特定字段
	public static MPStatus SetField(this ref MPStatus status, MPStatus mask, MPStatus value) {
		// 清除原有值，设置新值
		return status = (status & ~mask) | (value & mask);
	}

	// 获取特定字段
	public static MPStatus GetField(this MPStatus status, MPStatus mask) {
		return status & mask;
	}

	public static bool IsInLobby(this MPStatus status) {
		return GetField(status, MPStatus.LOBBY_MASK) == MPStatus.InLobby
			|| GetField(status, MPStatus.LOBBY_MASK) == MPStatus.JoiningLobby;
	}

	public static bool IsInitialized(this MPStatus status) {
		return GetField(status, MPStatus.INIT_MASK) == MPStatus.Initialized;
	}
}

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
	internal LocalPlayer LPManager { get; private set; }

	// 世界种子 - 用于同步游戏世界生成
	public int WorldSeed { get; private set; }

	// 多人模式状态
	private static MPStatus _multiPlayerStatus = MPStatus.NotInitialized;
	public static MPStatus MultiPlayerStatus { get => _multiPlayerStatus; private set => _multiPlayerStatus = value; }
	public static bool IsInLobby => MultiPlayerStatus.IsInLobby();
	public static bool IsInitialized => MultiPlayerStatus.IsInitialized();

	#region[Unity组件生命周期函数]

	void Awake() {
		// Debug
		MPMain.LogInfo(Localization.Get("MPCore", "Awake"));

		// 简单的重复检查作为安全网
		if (Instance != null && Instance != this) {
			// Debug
			MPMain.LogWarning(Localization.Get("MPCore", "DuplicateInstanceDetected"));
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
		LPManager.ShouldSendData = IsInLobby && IsInitialized && Steamworks.HasConnections;
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
		MPMain.LogInfo(Localization.Get("MPCore", "Destroy"));
	}

	#endregion

	#region[RAII函数]

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
			LPManager = gameObject.AddComponent<LocalPlayer>();
			LPManager.Initialize(Steamworks.UserSteamId);

			// 订阅事件
			SubscribeToEvents();
			// Debug
			MPMain.LogInfo(Localization.Get("MPCore", "AllManagersInitialized"));
		} catch (Exception e) {
			MPMain.LogError(Localization.Get("MPCore", "ManagerInitializationFailed", e.Message));
		}
	}

	/// <summary>
	/// 初始化网络事件订阅
	/// </summary>
	private void SubscribeToEvents() {
		// 订阅网络数据接收事件
		MPEventBusNet.OnReceiveData += ProcessReceiveData;

		// 订阅大厅事件
		MPEventBusNet.OnLobbyEntered += HandleLobbyEntered;
		MPEventBusNet.OnLobbyMemberJoined += HandleLobbyMemberJoined;
		MPEventBusNet.OnLobbyMemberLeft += HandleLobbyMemberLeft;
		MPEventBusNet.OnLobbyHostChanged += HandleLobbyHostChanged;

		// 订阅玩家连接事件
		MPEventBusNet.OnPlayerConnected += HandlePlayerConnected;
		MPEventBusNet.OnPlayerDisconnected += HandlePlayerDisconnected;

		// 订阅游戏事件
		MPEventBusGame.OnPlayerMove += SeedLocalPlayerData;
		MPEventBusGame.OnPlayerDamage += HandlePlayerDamage;
		MPEventBusGame.OnPlayerAddForce += HandlePlayerAddForce;
		MPEventBusGame.OnPlayerDeath += HandlePlayerDeath;
	}

	/// <summary>
	/// 取消所有网络事件订阅
	/// </summary>
	private void UnsubscribeFromEvents() {
		// 退订网络数据接收事件
		MPEventBusNet.OnReceiveData -= ProcessReceiveData;

		// 退订大厅事件
		MPEventBusNet.OnLobbyEntered -= HandleLobbyEntered;
		MPEventBusNet.OnLobbyMemberJoined -= HandleLobbyMemberJoined;
		MPEventBusNet.OnLobbyMemberLeft -= HandleLobbyMemberLeft;
		MPEventBusNet.OnLobbyHostChanged -= HandleLobbyHostChanged;

		// 退订玩家连接事件
		MPEventBusNet.OnPlayerConnected -= HandlePlayerConnected;
		MPEventBusNet.OnPlayerDisconnected -= HandlePlayerDisconnected;

		// 退订游戏事件
		MPEventBusGame.OnPlayerMove -= SeedLocalPlayerData;
		MPEventBusGame.OnPlayerDamage -= HandlePlayerDamage;
		MPEventBusGame.OnPlayerAddForce -= HandlePlayerAddForce;
		MPEventBusGame.OnPlayerDeath -= HandlePlayerDeath;
	}

	#endregion

	#region[场景切换回调]
	/// <summary>
	/// 场景加载完成时调用
	/// </summary>
	private void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
		// Debug
		MPMain.LogInfo(Localization.Get("MPCore", "SceneLoadingCompleted", scene.name));

		switch (scene.name) {
			case "Game-Main": {
				// 注册命令和初始化世界数据
				if (CommandConsole.instance != null) {
					RegisterCommands();
				} else {
					// Debug
					MPMain.LogError(Localization.Get("MPCore", "CommandConsoleNullAfterSceneLoad"));
				}
				break;
			}
			case "Playground": {
				// 注册命令和初始化世界数据
				if (CommandConsole.instance != null) {
					RegisterCommands();
					_multiPlayerStatus.SetField(MPStatus.INIT_MASK, MPStatus.Initialized);
				} else {
					// Debug
					MPMain.LogError(Localization.Get("MPCore", "CommandConsoleNullAfterSceneLoad"));
				}
				break;
			}
			case "Main-Menu":
				ResetStateVariables();
				break;

			default:
				ResetStateVariables();
				break;

		}
	}
	#endregion

	#region[状态设置]

	/// <summary>
	/// 死亡时延迟退出联机模式
	/// </summary>
	private IEnumerator OnDeathSequence() {
		yield return new WaitForSeconds(0.5f);
		ResetStateVariables();
		yield break;
	}

	/// <summary>
	/// 退出联机模式时重置设置
	/// </summary>
	private void ResetStateVariables() {
		MultiPlayerStatus = MPStatus.NotInitialized | MPStatus.NotInLobby;
		Steamworks.DisconnectAll();
		RPManager.ResetAll();
	}

	#endregion

	#region[游戏数据收集处理]
	/// <summary>
	/// 客户端/主机: 发送本地玩家数据
	/// </summary>
	private void SeedLocalPlayerData(PlayerData data) {
		var writer = GetWriter(
			Steamworks.UserSteamId,
			Steamworks.BroadcastId,
			PacketType.PlayerDataUpdate);

		MPDataSerializer.WriteToNetData(writer, data);
		// 触发Steam数据发送
		// 转为byte[]
		// 使用不可靠+立即发送
		// 广播所有人
		Steamworks.Broadcast(writer, SendType.Unreliable | SendType.NoNagle);
		return;
	}

	/// <summary>
	/// 客户端/主机: 发送伤害其他玩家数据
	/// </summary>
	private void HandlePlayerDamage(ulong steamId, float amount, string type) {
		var writer = GetWriter(Steamworks.UserSteamId, steamId, PacketType.PlayerDamage);
		writer.Put(amount);
		writer.Put(type);
		Steamworks.SendToPeer(steamId, writer);
	}

	/// <summary>
	/// 客户端/主机: 发送基于其他玩家冲击力数据
	/// </summary>
	private void HandlePlayerAddForce(ulong steamId, Vector3 force, string source) {
		var writer = GetWriter(Steamworks.UserSteamId, steamId, PacketType.PlayerAddForce);
		writer.Put(force.x);
		writer.Put(force.y);
		writer.Put(force.z);
		writer.Put(source);
		Steamworks.SendToPeer(steamId, writer);
	}

	/// <summary>
	/// 发送玩家死亡信息
	/// </summary>
	private void HandlePlayerDeath(string type) {
		var writer = GetWriter(Steamworks.UserSteamId, Steamworks.BroadcastId, PacketType.PlayerDeath);
		writer.Put(type);
		Steamworks.Broadcast(writer);

		switch (SceneManager.GetActiveScene().name) {
			case "Game-Main": {
				// 断开网络连接
				StartCoroutine(OnDeathSequence());
				break;
			}
			default: {

				break;
			}
		}
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
		CommandConsole.AddCommand("getlobbyid", GetLobbyId);
		CommandConsole.AddCommand("getconnections", GetAllConnections);
		CommandConsole.AddCommand("talk", Talk);
		CommandConsole.AddCommand("tpto", TpToPlayer);
		CommandConsole.AddCommand("initialized", Initialized, false);
		CommandConsole.AddCommand("test", Test.Test.Main, false);
	}

	// 命令实现
	/// <summary>
	/// 创建大厅
	/// </summary>
	public void Host(string[] args) {
		if (IsInLobby) {
			CommandConsole.LogError(Localization.Get("CommandConsole", "AlreadyInOnlineMode"));
			return;
		}
		if (args.Length < 1) {
			CommandConsole.LogError(Localization.Get("CommandConsole", "HostUsage"));
			return;
		}

		string roomName = args[0];
		int maxPlayers = args.Length >= 2 ? int.Parse(args[1]) : 4;
		// Debug
		MPMain.LogInfo(Localization.Get("MPCore", "CreatingLobby", roomName));

		//设置为正在连接
		_multiPlayerStatus.SetField(MPStatus.LOBBY_MASK, MPStatus.JoiningLobby);

		// 使用协程版本(内部已改为异步)
		Steamworks.CreateRoom(roomName, maxPlayers, (success) => {
			if (success) {
				// 这个触发比加入大厅回调触发慢
				_multiPlayerStatus.SetField(MPStatus.LOBBY_MASK, MPStatus.InLobby);
				_multiPlayerStatus.SetField(MPStatus.INIT_MASK, MPStatus.Initialized);

				switch (SceneManager.GetActiveScene().name) {
					case "Game-Main": {
						WorldLoader.ReloadWithSeed(new string[] { WorldLoader.instance.seed.ToString() });
						break;
					}
					default: {
						break;
					}
				}
			} else {
				_multiPlayerStatus.SetField(MPStatus.LOBBY_MASK, MPStatus.LobbyConnectionError);
				CommandConsole.LogError(Localization.Get("CommandConsole", "CreateLobbyFailed"));
			}
		});
	}

	/// <summary>
	/// 加入大厅
	/// </summary>
	public void Join(string[] args) {
		if (IsInLobby) {
			CommandConsole.LogError(Localization.Get("CommandConsole", "AlreadyInOnlineMode"));
			return;
		}
		if (args.Length < 1) {
			CommandConsole.LogError(Localization.Get("CommandConsole", "JoinUsage"));
			return;
		}

		if (!ulong.TryParse(args[0], out ulong lobbyId)) {
			CommandConsole.LogError(Localization.Get("CommandConsole", "JoinFormatError"));
			return;
		}
		MPMain.LogInfo(Localization.Get("MPCore", "JoiningLobby", lobbyId.ToString()));

		//设置为正在连接
		_multiPlayerStatus.SetField(MPStatus.LOBBY_MASK, MPStatus.JoiningLobby);

		Steamworks.JoinRoom(lobbyId, (success) => {
			if (success) {
				_multiPlayerStatus.SetField(MPStatus.LOBBY_MASK, MPStatus.InLobby);
			} else {
				_multiPlayerStatus.SetField(MPStatus.LOBBY_MASK, MPStatus.LobbyConnectionError);
				CommandConsole.LogError(Localization.Get("CommandConsole", "JoinLobbyFailed"));
			}
		});
	} 

	/// <summary>
	/// 离开大厅
	/// </summary>
	public void Leave(string[] args) {
		ResetStateVariables();
		// Debug
		MPMain.LogInfo(Localization.Get("MPCore", "DisconnectedAndCleaned"));
	}

	/// <summary>
	/// 获取大厅Id
	/// </summary>
	public void GetLobbyId(string[] args) {
		if (!IsInLobby) {
			CommandConsole.LogError(Localization.Get("CommandConsole", "NeedToBeOnline"));
			return;
		}
		CommandConsole.Log(Localization.Get(
			"CommandConsole", "LobbyIdOutput", Steamworks.LobbyId.ToString()));
	}

	/// <summary>
	/// 发送信息
	/// </summary>
	public void Talk(string[] args) {
		if (!IsInLobby) {
			CommandConsole.LogError(Localization.Get("CommandConsole", "NeedToBeOnline"));
			return;
		}
		// 将参数数组组合成一个字符串
		string message = string.Join(" ", args);

		var writer = GetWriter(
			Steamworks.UserSteamId,
			Steamworks.BroadcastId,
			PacketType.BroadcastMessage);

		// 自动处理长度和编码
		writer.Put(message);

		// 发送给所有人
		Steamworks.Broadcast(writer);
	}

	/// <summary>
	/// 向某人TP
	/// </summary>
	public void TpToPlayer(string[] args) {
		if (!IsInLobby) {
			CommandConsole.LogError(Localization.Get("CommandConsole", "NeedToBeOnline"));
			return;
		}
		if (!IsInitialized) {
			CommandConsole.LogError(Localization.Get("CommandConsole", "WorldNotInitialized"));
			return;
		}
		if (ulong.TryParse(args[0], out ulong playerId)) {
			var ids = DictionaryExtensions.FindByKeySuffix(RPManager.Players, playerId);
			// 未找到对应id
			if (ids.Count == 0) {
				CommandConsole.LogError(Localization.Get("CommandConsole", "TargetIdNotFound"));
				return;
			}
			// 找到多个对应id
			if (ids.Count > 1) {
				string idStr = string.Join("\n", ids);
				CommandConsole.LogError(Localization.Get(
					"CommandConsole", "MultipleMatchingIds", idStr));
				return;
			}
			// 找到对应id,发出传送请求
			var writer = GetWriter(Steamworks.UserSteamId, ids[0], PacketType.PlayerTeleport);
			Steamworks.SendToPeer(ids[0], writer);
		}
	}

	/// <summary>
	/// 调试用,主机获取所有链接
	/// </summary>
	public void GetAllConnections(string[] args) {
		if (!IsInLobby) {
			CommandConsole.LogError(Localization.Get("CommandConsole", "NeedToBeOnline"));
			return;
		}
		foreach (var (steamid, connection) in Steamworks._connectedClients) {
			MPMain.LogInfo(Localization.Get(
				"MPCore", "AllConnectionLog", steamid.ToString(), connection.ToString()));
		}
	}

	/// <summary>
	/// 获取全部玩家
	/// </summary>
	public void GetAllPlayer(string[] args) {
		foreach (var friend in Steamworks.Friends) {
			CommandConsole.Log(Localization.Get(
				"CommandConsole", "AllPlayer", friend.Name, friend.Id));
		}
	}

	/// <summary>
	/// 主动设置加载位
	/// </summary>
	public void Initialized(string[] args) {
		_multiPlayerStatus.SetField(MPStatus.INIT_MASK, MPStatus.Initialized);
	}
	#endregion

	#region [Steamwork事件处理]
	/// <summary>
	/// 加入大厅回调
	/// </summary>
	private void HandleLobbyEntered(Lobby lobby) {
		// Debug
		MPMain.LogInfo(Localization.Get("MPCore", "EnteringLobby", lobby.Id.ToString()));

		// 启动协程发送请求初始化数据
		StartCoroutine(InitHandshakeRoutine());
	}

	/// <summary>
	/// 有其他玩家加入大厅回调 创建玩家映射
	/// </summary>
	private void HandleLobbyMemberJoined(SteamId steamId) {
		if (steamId == Steamworks.UserSteamId) {
			return;
		}

		MPMain.LogInfo(Localization.Get("MPCore", "PlayerJoinedLobby", steamId.ToString()));

		RPManager.PlayerCreate(steamId);
	}

	/// <summary>
	/// 有其他玩家离开大厅回调 删除玩家映射
	/// </summary>
	private void HandleLobbyMemberLeft(SteamId steamId) {
		// Debug
		// 自己已经离线,不需要再销毁
		//if (playerId == Steamworks.UserSteamId)
		//	return;

		MPMain.LogInfo(Localization.Get("MPCore", "PlayerLeftLobby", steamId.ToString()));

		RPManager.PlayerRemove(steamId);
	}

	/// <summary>
	/// 大厅所有权变更
	/// </summary>
	private void HandleLobbyHostChanged(Lobby lobby, SteamId hostId) {
		// 这里删除旧主机的玩家对象
		RPManager.PlayerRemove(hostId);

		// 处理身份切换
		if (Steamworks.IsHost) {
			// 成为新主机
			HandleIAmNewHost();
		}
	}

	/// <summary>
	/// 成为新主机时执行
	/// </summary>
	private void HandleIAmNewHost() {
		// 停止握手协程(以防还在运行)
		if (IsInitialized == false) {
			StopCoroutine(InitHandshakeRoutine());
			// 要求所有现存客机重新发送一次完整数据
			// 意外在未初始化的情况下成为主机,需要向现存客户端要求数据
			// 要求其他客户端断开旧主机连接,连接此主机

		}
	}

	/// <summary>
	/// 主机接收事件总线OnPlayerConnected 发送PlayerCreate: 处理玩家接入事件
	/// </summary>
	private void HandlePlayerConnected(SteamId steamId) {
	}

	/// <summary>
	/// 主机事件总线OnPlayerDisconnected 发送PlayerRemove: 处理玩家断连 
	/// 客户端事件总线OnPlayerDisconnected: 对可能的主机离线进行删除
	/// </summary>
	private void HandlePlayerDisconnected(SteamId steamId) {
	}

	#endregion

	#region [网络数据处理]
	/// <summary>
	/// 客户端发送WorldInitRequest: 协程请求初始化数据
	/// </summary>
	public IEnumerator InitHandshakeRoutine() {
		yield return new WaitForSeconds(1.0f);
		// 在大厅并且未加载
		while (IsInLobby && !IsInitialized) {
			MPMain.LogInfo(Localization.Get("MPCore", "RequestedInitData"));
			var writer = GetWriter(Steamworks.UserSteamId, Steamworks.HostSteamId, PacketType.WorldInitRequest);
			Steamworks.SendToPeer(Steamworks.HostSteamId, writer);
			yield return new WaitForSeconds(4.0f);
		}
	}

	/// <summary>
	/// 主机接收WorldInitRequest: 请求初始化数据
	/// 发送WorldInitData: 初始化数据给新玩家
	/// </summary>
	/// <todo>将writer的拷贝byte[]改成byte[]视图,实现零拷贝</todo>
	private void ProcessWorldInitRequest(SteamId steamId) {
		// 发送世界种子
		var writer = GetWriter(Steamworks.UserSteamId, steamId, PacketType.WorldInitData);
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
		Steamworks.SendToPeer(steamId, writer);
		// Debug
		MPMain.LogInfo(Localization.Get("MPCore", "SentInitData"));
	}

	/// <summary>
	/// 客户端接收WorldInitData: 新加入玩家,加载世界种子和已存在玩家
	/// </summary>
	private void ProcessWorldInit(ArraySegment<byte> payload) {
		var reader = GetReader(payload);

		// 获取种子
		int seed = reader.GetInt();

		// 获取玩家数
		int playerCount = reader.GetInt();

		// Debug
		MPMain.LogInfo(Localization.Get("MPCore", "LoadingWorld", seed.ToString()));

		// 种子相同默认为已经联机过,只不过断开了
		if (seed != WorldLoader.instance.seed)
			WorldLoader.ReloadWithSeed(new string[] { seed.ToString() });
		_multiPlayerStatus.SetField(MPStatus.INIT_MASK, MPStatus.Initialized);

		for (int i = 0; i < playerCount; i++) {
			ulong playerId = reader.GetULong(); // 记得使用 GetULong 对应 SteamId
			RPManager.PlayerCreate(playerId);
			MPMain.LogInfo(Localization.Get("MPCore", "CreateExistingPlayer",playerId.ToString()));
		}
	}

	/// <summary>
	/// 客户端接收PlayerCreate: 创建玩家映射
	/// </summary>
	private void ProcessPlayerCreate(ulong playerId) {

	}

	/// <summary>
	/// 客户端接收PlayerRemove: 销毁玩家映射
	/// </summary>
	private void ProcessPlayerRemove(ulong playerId) {

	}

	/// <summary>
	/// 主机/客户端接收PlayerDataUpdate: 处理玩家数据更新
	/// </summary>
	private void ProcessPlayerDataUpdate(ArraySegment<byte> payload) {
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
	private void ProcessPlayerTagUpdate(ulong senderId, ArraySegment<byte> payload) {
		var reader = GetReader(payload);

		string msg = reader.GetString();    // 读取消息

		string playerName = new Friend(senderId).Name;
		CommandConsole.Log($"{playerName}: {msg}");
		RPManager.Players[senderId].UpdateNameTag(msg);
	}

	/// <summary>
	/// 主机/客户端接收PlayerDamage: 受到伤害
	/// </summary>
	private void ProcessPlayerDamage(ulong senderId, ArraySegment<byte> payload) {
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
	private void ProcessPlayerAddForce(ulong senderId, ArraySegment<byte> payload) {
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
	/// 主机/客户端接收PlayerDeath: 玩家死亡
	/// </summary>
	private void ProcessPlayerDeath(ulong senderId, ArraySegment<byte> payload) {
		var reader = GetReader(payload);
		string type = reader.GetString();
		string playerName = new Friend(senderId).Name;
		CommandConsole.Log(Localization.Get("CommandConsole", "PlayerDeath", playerName, type));

	}

	/// <summary>
	/// 主机/客户端接收PlayerTeleport
	/// 发送RespondPlayerTeleport: 携带Mess数据
	/// </summary>
	private void ProcessPlayerTeleport(ulong senderId, ArraySegment<byte> payload) {
		// 获取数据
		var positionData = ENT_Player.GetPlayer().transform.position;
		// 作为接收方重新变成发送方, 作为发送方重新变成接收方
		var writer = GetWriter(Steamworks.UserSteamId, senderId, PacketType.RespondPlayerTeleport);
		writer.Put(positionData.x);
		writer.Put(positionData.y);
		writer.Put(positionData.z);
		if (DEN_DeathFloor.instance == null) {
			writer.Put(false);
		} else {
			var deathFloorData = DEN_DeathFloor.instance.GetSaveData();
			writer.Put(true);
			writer.Put(deathFloorData.relativeHeight);
			writer.Put(deathFloorData.active);
			writer.Put(deathFloorData.speed);
			writer.Put(deathFloorData.speedMult);
		}
		Steamworks.SendToPeer(senderId, writer);
	}

	/// <summary>
	/// 主机/客户端接收RespondPlayerTeleport: 同步Mess数据并传送
	/// </summary>
	private void ProcessRespondPlayerTeleport(ulong senderId, ArraySegment<byte> payload) {
		var reader = GetReader(payload);
		var position = new Vector3 {
			x = reader.GetFloat(),
			y = reader.GetFloat(),
			z = reader.GetFloat(),
		};
		if (reader.GetBool()) {
			var deathFloorData = new DEN_DeathFloor.SaveData {
				relativeHeight = reader.GetFloat(),
				active = reader.GetBool(),
				speed = reader.GetFloat(),
				speedMult = reader.GetFloat(),
			};

			// 关闭可击杀效果
			DEN_DeathFloor.instance.SetCanKill(new string[] { "false" });
			// 重设计数器,期间位移视为传送
			LPManager.TriggerTeleport();
			ENT_Player.GetPlayer().Teleport(position);
			DEN_DeathFloor.instance.LoadDataFromSave(deathFloorData);
			DEN_DeathFloor.instance.SetCanKill(new string[] { "true" });
		} else {
			// 重设计数器,期间位移视为传送
			LPManager.TriggerTeleport();
			ENT_Player.GetPlayer().Teleport(position);
		}
	}

	/// <summary>
	/// 处理网络接收数据
	/// </summary>
	/// <param name="connectionId"></param>
	/// <param name="data"></param>
	private void ProcessReceiveData(ulong connectionId, ArraySegment<byte> data) {
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
			if (senderId != connectionId) 
				return;

			// 转发：目标不是我,也不是广播
			if (targetId != Steamworks.UserSteamId && targetId != Steamworks.BroadcastId) {
				ProcessForwardToPeer(targetId, data);
				return; // 结束
			}

			// 广播：如果是广播,且不是我发出的
			if (targetId == Steamworks.BroadcastId && connectionId != Steamworks.UserSteamId) {
				ProcessBroadcastExcept(senderId, data);
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
				ProcessWorldInitRequest(senderId);
				break;
			}
			// 接收: 联机世界初始化数据
			case PacketType.WorldInitData: {
				ProcessWorldInit(payload);
				break;
			}
			// 接收: 创建玩家映射
			case PacketType.PlayerCreate: {

				break;
			}
			// 接收: 销毁玩家映射
			case PacketType.PlayerRemove: {

				break;
			}
			// 接收: 玩家数据更新
			case PacketType.PlayerDataUpdate: {
				ProcessPlayerDataUpdate(payload);
				break;
			}
			// 接收: 世界状态同步
			case PacketType.WorldStateSync: {
				break;
			}
			// 接收: 广播消息
			case PacketType.BroadcastMessage: {
				ProcessPlayerTagUpdate(senderId, payload);
				break;
			}
			// 接收: 受到伤害
			case PacketType.PlayerDamage: {
				ProcessPlayerDamage(senderId, payload);
				break;
			}
			// 接收: 受到冲击力
			case PacketType.PlayerAddForce: {
				ProcessPlayerAddForce(senderId, payload);
				break;
			}
			// 接收: 玩家死亡
			case PacketType.PlayerDeath: {
				ProcessPlayerDeath(senderId, payload);
				break;
			}

			// 接收: 请求传送
			case PacketType.PlayerTeleport: {
				ProcessPlayerTeleport(senderId, payload);
				break;
			}
			// 接收: 响应传送
			case PacketType.RespondPlayerTeleport: {
				ProcessRespondPlayerTeleport(senderId, payload);
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
	private void ProcessForwardToPeer(ulong targetId, ArraySegment<byte> data) {
		// 直接从 segment 获取偏移和长度
		int offset = data.Offset;
		int count = data.Count;
		// 解析类型
		PacketType type = (PacketType)BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(16, 4));
		SendType st = (type == PacketType.PlayerDataUpdate)
			? SendType.Unreliable : SendType.Reliable;

		Steamworks.SendToPeer(targetId, data.Array, offset, count, st);
	}

	/// <summary>
	/// 广播数据包到所有客户端
	/// </summary>
	public void ProcessBroadcast(ArraySegment<byte> data) {
		// 直接从 segment 获取偏移和长度
		int offset = data.Offset;
		int count = data.Count;

		// 解析类型
		PacketType type = (PacketType)BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(16, 4));

		SendType st = (type == PacketType.PlayerDataUpdate)
			? SendType.Unreliable : SendType.Reliable;

		// 将全套参数传给底层
		Steamworks.Broadcast(data.Array, offset, count, st);
	}

	/// <summary>
	/// 广播数据包到所有客户端 (除了发送者)
	/// </summary>
	/// <param name="senderId">发送方ID</param>
	public void ProcessBroadcastExcept(ulong senderId, ArraySegment<byte> data) {
		// 直接从 segment 获取偏移和长度
		int offset = data.Offset;
		int count = data.Count;

		// 解析类型
		PacketType type = (PacketType)BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(16, 4));

		SendType st = (type == PacketType.PlayerDataUpdate)
			? SendType.Unreliable : SendType.Reliable;

		// 将全套参数传给底层
		Steamworks.BroadcastExcept(senderId, data.Array, offset, count, st);
	}
	#endregion
}
