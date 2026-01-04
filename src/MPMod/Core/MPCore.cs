
using LiteNetLib.Utils;
using Steamworks;
using Steamworks.Data;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.SceneManagement;
using WKMultiMod.src.Data;
using WKMultiMod.src.NetWork;
using WKMultiMod.src.Util;
using WKMultiMod.src.Test;
using static MonoMod.Cil.RuntimeILReferenceBag.FastDelegateInvokers;
using static WKMultiMod.src.Core.MPEventBus;
using static WKMultiMod.src.Util.MPReaderPool;
using static WKMultiMod.src.Util.MPWriterPool;

using static Steamworks.InventoryItem;

namespace WKMultiMod.src.Core;

public class MPCore : MonoBehaviour {

	// Debug日志输出间隔
	private TickTimer _debugTick = new TickTimer(5f);

	// 单例实例
	public static MPCore Instance { get; private set; }
	// 标识这是否是"有效"实例(防止使用游戏初期被销毁的实例)
	public static bool HasValidInstance => Instance != null && Instance.isActiveAndEnabled;

	// Steam网络管理器 远程玩家管理器 本地数据获取类
	internal MPSteamworks Steamworks { get; private set; }
	internal RemotePlayerManager RPManager { get; private set; }
	// 本地数据获取类已经变成静态类
	//internal LocalPlayerManager LPManager { get; private set; }

	// 玩家数据发送时间 每秒30次
	private TickTimer _playerDataTick = new TickTimer(30);
	private readonly NetDataWriter _playerDataWriter = new NetDataWriter();
	private TickTimer _teleport = new TickTimer(1);

	// 世界种子 - 用于同步游戏世界生成
	public int WorldSeed { get; private set; }
	// 是否在多人模式
	public static bool IsMultiplayerActive { get; private set; } = false;
	// 混乱模式开关
	public static bool IsChaosMod { get; private set; } = false;
	// 是否已初始化
	public static bool HasInitialized { get; private set; } = false;

	// 注意：日志通过 MultiPlayerMain.Logger 访问
	#region[生命周期/状态设置 函数]
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
		if (MPCore.IsMultiplayerActive == false)
			return;
		// 没有链接时停止更新
		if (!MPCore.Instance.Steamworks.HasConnections)
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
		MPEventBus.Net.OnReceiveData += HandleReceiveData;

		// 订阅大厅事件
		MPEventBus.Net.OnLobbyEntered += HandleLobbyEntered;
		MPEventBus.Net.OnLobbyMemberJoined += HandleLobbyMemberJoined;
		MPEventBus.Net.OnLobbyMemberLeft += HandleLobbyMemberLeft;

		// 订阅玩家连接事件
		MPEventBus.Net.OnPlayerConnected += HandlePlayerConnected;
		MPEventBus.Net.OnPlayerDisconnected += HandlePlayerDisconnected;

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
		MPEventBus.Net.OnLobbyEntered -= HandleLobbyEntered;
		MPEventBus.Net.OnLobbyMemberJoined -= HandleLobbyMemberJoined;
		MPEventBus.Net.OnLobbyMemberLeft -= HandleLobbyMemberLeft;

		// 退订玩家连接事件
		MPEventBus.Net.OnPlayerConnected -= HandlePlayerConnected;
		MPEventBus.Net.OnPlayerDisconnected -= HandlePlayerDisconnected;

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
	#endregion

	#region[游戏数据收集处理]

	/// <summary>
	/// 发送本地玩家数据
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
		_playerDataWriter.Put((int)PacketType.PlayerDataUpdate);
		MPDataSerializer.WriteToNetData(_playerDataWriter, playerData);
		// 触发Steam数据发送
		// 转为byte[]
		// 使用不可靠+立即发送
		// 广播所有人
		Steamworks.HandleBroadcast(
			MPDataSerializer.WriterToBytes(_playerDataWriter),
			SendType.Unreliable | SendType.NoNagle);

		_playerDataWriter.Reset();
		return;
	}

	/// <summary>
	/// 发送伤害其他玩家数据
	/// </summary>
	private void ProcessPlayerDamage(ulong steamId, float amount, string type) {
		var writer = GetWriter();
		writer.Put((int)PacketType.PlayerDamage);
		writer.Put(amount);
		writer.Put(type);
		var data = MPDataSerializer.WriterToBytes(writer);
		Steamworks.HandleSendToPeer(steamId, data, SendType.Reliable);
	}

	/// <summary>
	/// 发送给予其他玩家冲击力数据
	/// </summary>
	private void ProcessPlayerAddForce(ulong steamId, Vector3 force, string source) {
		var writer = GetWriter();
		writer.Put((int)PacketType.PlayerAddForce);
		writer.Put(force.x);
		writer.Put(force.y);
		writer.Put(force.z);
		writer.Put(source);
		var data = MPDataSerializer.WriterToBytes(writer);
		Steamworks.HandleSendToPeer(steamId, data, SendType.Reliable);
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
		CommandConsole.AddCommand("allconnections", GetAllConnections);
		CommandConsole.AddCommand("talk", Talk);
		CommandConsole.AddCommand("tpto", TpToPlayer);
		CommandConsole.AddCommand("test", Test.Test.Main);
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
		int maxPlayers = args.Length >= 2 ? int.Parse(args[1]) : 6;
		// Debug
		MPMain.LogInfo(
			$"[MPCore] 正在创建大厅: {roomName}...",
			$"[MPCore] Creating lobby: {roomName}...");

		// 使用协程版本(内部已改为异步)
		Steamworks.CreateRoom(roomName, maxPlayers, (success) => {
			if (success) {
				HandleWorldInitRequest(WorldLoader.instance.seed);
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
					//设置为联机模式启动
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
	/// 获取大厅ID
	/// </summary>
	public void GetLobbyId(string[] args) {
		if (!IsMultiplayerActive) {
			CommandConsole.LogError("Please use this command after online");
			return;
		}
		CommandConsole.Log($"Lobby Id: {Steamworks.GetLobbyId().ToString()}");
	}

	/// <summary>
	/// 调试用,获取所有链接
	/// </summary>
	public void GetAllConnections(string[] args) {
		if (!IsMultiplayerActive) {
			CommandConsole.LogError("You need in online mode, \n" +
				"please use the host or join");
			return;
		}
		CommandConsole.Log("View BepInEx Console");
		foreach (var (steamid, connection) in Steamworks._outgoingConnections) {
			MPMain.LogInfo(
				$"[MPCore] 出站连接 SteamId: {steamid.ToString()} 连接Id: {connection.ToString()}",
				$"[MPCore] Outgoing connections SteamId: {steamid.ToString()} connections Id: {connection.ToString()}");
		}
		foreach (var (steamid, connection) in Steamworks._allConnections) {
			MPMain.LogInfo(
				$"[MPCore] 全部连接 SteamId: {steamid.ToString()} 连接Id: {connection.ToString()}",
				$"[MPCore] All connections SteamId: {steamid.ToString()} connections Id: {connection.ToString()}");
		}
	}

	/// <summary>
	/// 发送信息到他人控制台
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
		writer.Put((int)PacketType.BroadcastMessage);
		writer.Put(message); // 自动处理长度和编码

		// 发送给所有人
		var data = MPDataSerializer.WriterToBytes(writer);
		Steamworks.HandleBroadcast(data, SendType.Reliable);
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
			writer.Put((int)PacketType.PlayerTeleport);

			var seedData = MPDataSerializer.WriterToBytes(writer);
			Steamworks.HandleSendToPeer(ids[0], seedData, SendType.Reliable);
		}
	}
	#endregion

	#region[大厅/连接事件触发函数]
	/// <summary>
	/// 处理大厅成员加入 连接新成员
	/// </summary> 
	private void HandleLobbyMemberJoined(SteamId steamId) {
		if (steamId == Steamworks.UserSteamId) return;
		// Debug
		MPMain.LogInfo(
			$"[MPCore] 玩家加入大厅: {steamId.ToString()}",
			$"[MPCore] New player joined the lobby: {steamId.ToString()}");
		Steamworks.ConnectToPlayer(steamId);
	}

	/// <summary>
	/// 处理加入大厅事件
	/// </summary>
	/// <param name="lobby"></param>
	private void HandleLobbyEntered(Lobby lobby) {
		// Debug
		MPMain.LogInfo(
			$"[MPCore] 正在加入大厅,ID: {lobby.Id.ToString()}",
			$"[MPCore] Joining the lobby, ID: {lobby.Id.ToString()}");

		// 这个触发可能比Join回调更快
		// 启动多人模式标准, 这里的触发可能先于join回调
		IsMultiplayerActive = true;

		// 启动协程发送请求初始化数据
		StartCoroutine(InitHandshakeRoutine());
	}

	/// <summary>
	/// 处理离开大厅事件
	/// </summary>
	/// <param name="steamId"></param>
	private void HandleLobbyMemberLeft(SteamId steamId) {
		// Debug
		MPMain.LogInfo(
			$"[MPCore] 玩家离开大厅: {steamId.ToString()}",
			$"[MPCore] Player left the lobby: {steamId.ToString()}");
	}

	/// <summary>
	/// 处理玩家接入事件
	/// </summary>
	private void HandlePlayerConnected(SteamId steamId) {
		// 创建玩家
		RPManager.PlayerCreate(steamId);
	}

	/// <summary>
	/// 处理玩家断连
	/// </summary>
	private void HandlePlayerDisconnected(SteamId steamId) {
		// Debug
		MPMain.LogInfo(
			$"[MPCore] 玩家断连: {steamId.ToString()}",
			$"[MPCore] Player disconnected: {steamId.ToString()}");
		RPManager.PlayerRemove(steamId.Value);
	}
	#endregion

	#region [网络数据处理]
	/// <summary>
	/// 协程请求种子
	/// </summary>
	public IEnumerator InitHandshakeRoutine() {
		// 未加载并且处于联机模式
		while (!HasInitialized && IsMultiplayerActive) {
			MPMain.LogInfo(
				"[MPCore] 已向主机请求初始化数据",
				"[MPCore] Requested initialization data from the host.");
			var writer = GetWriter();
			writer.Put((int)PacketType.WorldInitRequest);
			var requestData = MPDataSerializer.WriterToBytes(writer);
			Steamworks.HandleSendToHost(requestData);
			yield return new WaitForSeconds(2.0f);
		}
	}

	/// <summary>
	/// 加载世界种子
	/// </summary>
	/// <param name="seed"></param>
	private void HandleWorldInitRequest(int seed) {
		StartMultiPlayerMode();
		// Debug
		MPMain.LogInfo(
			$"[MPCore] 加载世界, 种子号: {seed.ToString()}",
			$"[MPCore] Loaging world, seed: {seed.ToString()}");
		WorldLoader.ReloadWithSeed(new string[] { seed.ToString() });
	}

	/// <summary>
	/// 发送初始化数据给新玩家
	/// </summary>
	private void HandleWorldInit(SteamId steamId) {
		// 发送世界种子
		var writer = GetWriter();
		writer.Put((int)PacketType.WorldInitData);
		writer.Put(WorldLoader.instance.seed);

		var seedData = MPDataSerializer.WriterToBytes(writer);
		Steamworks.HandleSendToPeer(steamId, seedData, SendType.Reliable);

		// 可以添加其他初始化数据,如游戏状态、物品状态等

		// Debug
		MPMain.LogInfo(
			"[MPCore] 已向新玩家发送初始化数据",
			"[MPCore] Initialization data has been sent to the new player.");
	}

	/// <summary>
	/// 处理玩家传送请求
	/// </summary>
	/// <param name="senderId">发送方ID</param>
	private void HandleRequestTeleport(SteamId senderId) {
		// 获取数据
		var deathFloorData = DEN_DeathFloor.instance.GetSaveData();
		var positionData = ENT_Player.GetPlayer().transform.position;
		var writer = GetWriter();
		writer.Put((int)PacketType.RespondPlayerTeleport);
		writer.Put(deathFloorData.relativeHeight);
		writer.Put(deathFloorData.active);
		writer.Put(deathFloorData.speed);
		writer.Put(deathFloorData.speedMult);
		writer.Put(positionData.x);
		writer.Put(positionData.y);
		writer.Put(positionData.z);
		var data = MPDataSerializer.WriterToBytes(writer);
		Steamworks.HandleSendToPeer(senderId, data, SendType.Reliable);
	}

	/// <summary>
	/// 处理玩家传送响应
	/// </summary>
	/// <param name="senderId">发送ID</param>
	private void HandleRespondTeleport(SteamId senderId, NetDataReader reader) {
		var deathFloorData = new DEN_DeathFloor.SaveData {
			relativeHeight = reader.GetFloat(),
			active = reader.GetBool(),
			speed = reader.GetFloat(),
			speedMult = reader.GetFloat(),
		};
		var posX = reader.GetFloat();
		var posY = reader.GetFloat();
		var posZ = reader.GetFloat();
		// 关闭可击杀效果
		DEN_DeathFloor.instance.SetCanKill(new string[] { "false" });
		// 重设计数器,期间位移视为传送
		_teleport.Reset();
		ENT_Player.GetPlayer().Teleport(new Vector3(posX, posY, posZ));
		DEN_DeathFloor.instance.LoadDataFromSave(deathFloorData);
		DEN_DeathFloor.instance.SetCanKill(new string[] { "true" });
	}

	/// <summary>
	/// 主机/客户端接收PlayerDamage: 受到伤害
	/// </summary>
	private void HandlePlayerDamage(NetDataReader reader) {
		float amount = reader.GetFloat();
		string type = reader.GetString();
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
	private void HandlePlayerAddForce(NetDataReader reader) {
		Vector3 force = new Vector3 {
			x = reader.GetFloat(),
			y = reader.GetFloat(),
			z = reader.GetFloat(),
		};
		string source = reader.GetString();
		ENT_Player.GetPlayer().AddForce(force, source);
	}

	/// <summary>
	/// 处理网络接收数据
	/// </summary>
	private void HandleReceiveData(ulong senderId, ArraySegment<byte> data) {

		// 基本验证：确保数据足够读取一个整数(数据包类型)
		var reader = GetReader(data);
		PacketType packetType = (PacketType)reader.GetInt();

		switch (packetType) {
			// 接收种子加载
			case PacketType.WorldInitData:
				HandleWorldInitRequest(reader.GetInt());
				break;
			// 接收玩家数据更新
			case PacketType.PlayerDataUpdate:
				var playerData = MPDataSerializer.ReadFromNetData(reader);
				RPManager.ProcessPlayerData(senderId, playerData);
				break;
			// 接收世界初始化请求
			case PacketType.WorldInitRequest:
				HandleWorldInit(senderId);
				break;
			// 接收信息
			case PacketType.BroadcastMessage:
				string receivedMsg = reader.GetString();
				CommandConsole.Log($"{senderId.ToString()}: {receivedMsg}");
				// 控制台目前不支持中文
				//string playerName = new Friend(playerId).Name;
				//CommandConsole.Log($"{playerName}: {receivedMsg}");
				RPManager.Players.TryGetValue(senderId, out var RPcontainer);
				RPcontainer?.UpdateNameTag(receivedMsg);
				break;
			// 接收传送请求
			case PacketType.PlayerTeleport:
				HandleRequestTeleport(senderId);
				break;
			// 接收传送响应
			case PacketType.RespondPlayerTeleport:
				HandleRespondTeleport(senderId, reader);
				break;
			// 接收: 受到伤害
			case PacketType.PlayerDamage: {
				HandlePlayerDamage(reader);
				break;
			}
			// 接收: 受到冲击力
			case PacketType.PlayerAddForce: {
				HandlePlayerAddForce(reader);
				break;
			}
		}
	}
	#endregion
}
