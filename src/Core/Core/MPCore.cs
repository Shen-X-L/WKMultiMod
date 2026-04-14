using Steamworks;
using Steamworks.Data;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using WKMPMod.Asset;
using WKMPMod.Component;
using WKMPMod.Data;
using WKMPMod.NetWork;
using WKMPMod.RemotePlayer;
using WKMPMod.UI;
using WKMPMod.Util;
using static WKMPMod.Data.MPWriterPool;
using static WKMPMod.UI.UI_Manager;

namespace WKMPMod.Core;

#region[多人模式状态枚举]
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
		// 清除原有值,设置新值
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
#endregion
public class MPCore : MonoSingleton<MPCore> {

	// Debug日志输出间隔
	private TickTimer _debugTick = new TickTimer(5f);
	// 玩家数量同步间隔
	private TickTimer _syncTick = new TickTimer(3f);

	// Steam网络管理器 本地数据获取类
	private MPSteamworks _MPsteamworks;
	private RPManager _RPManager;
	private LocalPlayer _LocalPlayer;
	private MPAssetManager _MPAssetManager;
	private UI_Manager _UIManager;

	// 多人模式状态
	public static MPStatus MultiPlayerStatus = MPStatus.NotInitialized;
	// 是否处于大厅中
	public static bool IsInLobby => MultiPlayerStatus.IsInLobby();
	public static bool IsInitialized => MultiPlayerStatus.IsInitialized();

	// 手部皮肤 -> 玩家模型ID 映射字典
	public static readonly Dictionary<string, string> HandSkinToModelId = new() {
		{ "default","default"},
		{ MPMain.SLUGCAT_HAND_ID, MPMain.SLUGCAT_BODY_FACTORY_ID },
		// 可在此添加更多映射
	};

	// 注意:日志通过 MultiPlayerMain.Logger 访问
	#region[Unity组件生命周期函数]
	protected override void Awake() {
		base.Awake();
		// Debug
		MPMain.LogInfo(Localization.Get("MPCore.Awake"));
	}

	void Start() {
		// 订阅场景切换
		SceneManager.sceneLoaded += OnSceneLoaded;

		// 初始化网络监听器和远程玩家管理器
		InitializeAllManagers();
	}

	void Update() {
		// 如果在大厅且已初始化且有连接,允许发送数据
		LocalPlayer.Instance.ShouldSendData = IsInLobby && IsInitialized && MPSteamworks.Instance.HasConnections;

		// 定期检查玩家数量和连接状态,修复异常状态
		CheckAndRepairPlayers();
	}

	/// <summary>
	/// 当核心对象被销毁时调用
	/// </summary>
	protected override void OnDestroy() {
		// 订阅场景切换
		SceneManager.sceneLoaded -= OnSceneLoaded;

		// 取消所有事件订阅
		UnsubscribeFromEvents();

		// 重置状态
		ResetStateVariables();

		// Debug
		MPMain.LogInfo(Localization.Get("MPCore.Destroy"));

		base.OnDestroy();
	}

	#endregion

	#region[RAII函数]

	/// <summary>
	/// 初始化所有管理器
	/// </summary>
	private void InitializeAllManagers() {
		try {
			// 创建Steamworks组件(无状态)
			_MPsteamworks = MPSteamworks.Instance;

			// 创建远程玩家管理器
			_RPManager = RPManager.Instance;
			_RPManager.Initialize(transform);

			// 创建本地信息获取发送管理器
			_LocalPlayer = LocalPlayer.Instance;
			_LocalPlayer.Initialize(MPSteamworks.Instance.UserSteamId, MPConfig.RemotePlayerModel);

			// 创建UI管理器
			_UIManager = UI_Manager.Instance;

			// 初始化资源管理器
			_MPAssetManager = MPAssetManager.Instance;
			// 必须在游戏资源加载完成后初始化
			//_MPAssetManager.Initialize();

			// 初始化网络数据包路由器
			MPPacketRouter.Initialize();

			// 订阅网络事件
			SubscribeToEvents();
			// Debug
			MPMain.LogInfo(Localization.Get("MPCore.AllManagersInitialized"));
		} catch (Exception e) {
			MPMain.LogError(Localization.Get("MPCore.ManagerInitializationFailed", e.Message));
		}
	}

	/// <summary>
	/// 初始化网络事件订阅
	/// </summary>
	private void SubscribeToEvents() {
		// 订阅大厅事件
		MPEventBusNet.OnLobbyEntered += HandleLobbyEntered;
		MPEventBusNet.OnLobbyMemberJoined += HandleLobbyMemberJoined;
		MPEventBusNet.OnLobbyMemberLeave += HandleLobbyMemberLeft;

		// 订阅玩家连接事件
		MPEventBusNet.OnPlayerConnected += HandlePlayerConnected;
		MPEventBusNet.OnPlayerDisconnected += HandlePlayerDisconnected;

		// 订阅接收邀请事件
		MPEventBusNet.OnGameLobbyJoinRequested += Join;

		// 订阅接受邀请事件
		MPEventBusNet.OnLobbyInvite += HandleLobbyInvite;

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
		// 退订大厅事件
		MPEventBusNet.OnLobbyEntered -= HandleLobbyEntered;
		MPEventBusNet.OnLobbyMemberJoined -= HandleLobbyMemberJoined;
		MPEventBusNet.OnLobbyMemberLeave -= HandleLobbyMemberLeft;

		// 退订玩家连接事件
		MPEventBusNet.OnPlayerConnected -= HandlePlayerConnected;
		MPEventBusNet.OnPlayerDisconnected -= HandlePlayerDisconnected;

		// 退订接收邀请事件
		MPEventBusNet.OnGameLobbyJoinRequested -= Join;

		// 退订接受邀请事件
		MPEventBusNet.OnLobbyInvite -= HandleLobbyInvite;

		// 退订游戏事件
		MPEventBusGame.OnPlayerMove -= SeedLocalPlayerData;
		MPEventBusGame.OnPlayerDamage -= HandlePlayerDamage;
		MPEventBusGame.OnPlayerAddForce -= HandlePlayerAddForce;
		MPEventBusGame.OnPlayerDeath -= HandlePlayerDeath;

	}

	#endregion

	#region[玩家数量同步]
	private void CheckAndRepairPlayers() {

		if (!IsInitialized || !IsInLobby) return;
		if (!_syncTick.TryTick()) return;
		// 在大厅但没有连接
		foreach (var member in _MPsteamworks.Members) {
			if (member.Id == _MPsteamworks.UserSteamId) continue;
			if (!_MPsteamworks._allConnections.ContainsKey(member.Id)) {
				_MPsteamworks.ConnectionController(member.Id, true);
			}
		}
		// 有连接但没有创建对象
		foreach (var (steamId, connection) in _MPsteamworks._allConnections) {
			if (!_RPManager.Players.ContainsKey(steamId)) {
				MPMain.LogWarning(Localization.Get("MPCore.PlayerDataMissing", steamId));
				// 发送请求玩家创建包
				var writer = GetWriter(_MPsteamworks.UserSteamId, steamId, PacketType.PlayerCreateRequest);
				_MPsteamworks.SendToPeer(steamId, writer);
			}
		}
	}

	#endregion

	#region[场景切换回调]

	/// <summary>
	/// 场景加载完成时调用
	/// </summary>
	private void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
		// Debug
		MPMain.LogInfo(Localization.Get("MPCore.SceneLoadingCompleted", scene.name));

		switch (scene.name) {
			case "Game-Main": {
				// 注册命令和初始化世界数据
				if (CommandConsole.instance != null) {
					RegisterCommands();
					ChangeRPFactoryId();
				} else {
					// Debug
					MPMain.LogError(Localization.Get("MPCore.CommandConsoleNullAfterSceneLoad"));
				}
				// 如果是主游戏场景且是房主,抓取当前模式数据并广播给其他人
				if (_MPsteamworks.IsHost) {
					// 设置当前游戏模式数据
					MPGameModeManager.CaptureCurrentData();
					// 以后会在这里广播模式数据

					SetStatus(MPStatus.INIT_MASK, MPStatus.Initialized);
				}
				if (IsInLobby && !IsInitialized) {
					StartCoroutine(InitHandshakeRoutine());
				}
				break;
			}
			case "Playground": {
				// 注册命令和初始化世界数据
				if (CommandConsole.instance != null) {
					RegisterCommands();
					ChangeRPFactoryId();
					SetStatus(MPStatus.INIT_MASK, MPStatus.Initialized);
				} else {
					// Debug
					MPMain.LogError(Localization.Get("MPCore.CommandConsoleNullAfterSceneLoad"));
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
	public void ResetStateVariables() {
		//MPMain.LogWarning("[MP Debug] 退出联机模式");
		SetStatus(MPStatus.INIT_MASK, MPStatus.NotInitialized);
		SetStatus(MPStatus.LOBBY_MASK, MPStatus.NotInLobby);
		MPGameModeManager.ClearCurrentData();
		_MPsteamworks.DisconnectAll();
		_RPManager.ResetAll();
	}

	/// <summary>
	/// 根据手部皮肤选择玩家模型创建ID
	/// </summary>
	private void ChangeRPFactoryId() {
		// 左右手皮肤相同,尝试映射
		if (CL_CosmeticManager.GetCosmeticInHand(0).cosmeticData.id
			== CL_CosmeticManager.GetCosmeticInHand(1).cosmeticData.id) {
			// 尝试从映射字典中获取对应的玩家模型ID
			if (HandSkinToModelId.TryGetValue(
				CL_CosmeticManager.GetCosmeticInHand(0).cosmeticData.id, out string factoryId)) {
				_LocalPlayer.FactoryId = factoryId;
			}
		}
	}
	#endregion

	#region[游戏数据收集处理]

	/// <summary>
	/// 发送本地玩家数据
	/// </summary>
	private void SeedLocalPlayerData(PlayerData data) {
		var writer = GetWriter(_MPsteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.PlayerDataUpdate);

		// 进行数据写入
		writer.Put(data);
		// 触发Steam数据发送
		// 转为byte[]
		// 使用不可靠+立即发送
		// 广播所有人
		_MPsteamworks.Broadcast(writer, SendType.Unreliable | SendType.NoNagle);
		return;
	}

	/// <summary>
	/// 发送伤害其他玩家数据
	/// </summary>
	private void HandlePlayerDamage(ulong steamId, float amount, string type) {
		var writer = GetWriter(_MPsteamworks.UserSteamId, steamId, PacketType.PlayerDamage);
		writer.Put(amount);
		writer.Put(type);
		_MPsteamworks.SendToPeer(steamId, writer);
	}

	/// <summary>
	/// 发送给予其他玩家冲击力数据
	/// </summary>
	private void HandlePlayerAddForce(ulong steamId, Vector3 force, string source) {
		var writer = GetWriter(_MPsteamworks.UserSteamId, steamId, PacketType.PlayerAddForce);
		writer.Put(force.x);
		writer.Put(force.y);
		writer.Put(force.z);
		writer.Put(source);
		_MPsteamworks.SendToPeer(steamId, writer);
	}

	/// <summary>
	/// 发送玩家死亡信息 死因 string 库存物品 Dictionary<string, ushort>
	/// </summary>
	private void HandlePlayerDeath(string type) {
		var writer = GetWriter(_MPsteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.PlayerDeath);
		// 死因
		writer.Put(type);

		// 库存物品字典
		writer.Put(GetGetInventoryItems());

		_MPsteamworks.Broadcast(writer);

		switch (SceneManager.GetActiveScene().name) {
			case "Game-Main": {
				// 断开网络连接
				// StartCoroutine(OnDeathSequence());
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
		CommandConsole.AddCommand("host", Host, false);
		CommandConsole.AddCommand("join", Join, false);
		CommandConsole.AddCommand("leave", Leave, false);
		CommandConsole.AddCommand("getlobbyid", GetLobbyId, false);
		CommandConsole.AddCommand("getallplayer", GetAllPlayer, false);
		CommandConsole.AddCommand("talk", Talk, false);
		CommandConsole.AddCommand("tpto", TpToPlayer);
		CommandConsole.AddCommand("changemodel", (str) => {
			_LocalPlayer.DefaulFactoryId = str[0];
			MPConfig.RemotePlayerModel = str[0];
		}, false);
		CommandConsole.AddCommand("getalllobby", GetAllLobby, false);
		CommandConsole.AddCommand("test", Test.Test.Main, false);
		CommandConsole.AddCommand("invite", OpenSteamInviteUI);
		CommandConsole.AddCommand("lobbytype", SetLobbyVisibility, false);
		CommandConsole.AddCommand("cheatstest", Test.CheatsTest.Main);
		CommandConsole.AddCommand("changename", (str) => {
			_MPsteamworks.SetLobbyData("name", str[0]);
		}, false);
	}

	/// <summary>
	/// 创建大厅
	/// </summary>
	public async void Host(string[] args) {
		// 基础状态检查
		if (IsInLobby) {
			CommandConsole.LogError(Localization.Get("CommandConsole.AlreadyInOnlineMode"));
			return;
		}
		if (args.Length < 1) {
			CommandConsole.LogError(Localization.Get("CommandConsole.HostUsage"));
			return;
		}

		string roomName = args[0];
		int maxPlayers = args.Length >= 2 ? int.Parse(args[1]) : 6;
		// Debug
		MPMain.LogInfo(Localization.Get("MPCore.CreatingLobby", roomName));

		// 设置状态为正在连接
		SetStatus(MPStatus.LOBBY_MASK, MPStatus.JoiningLobby);

		// 预设置大厅数据
		var lobbyData = new Dictionary<string, string>() {
			{ "name", roomName },         // 大厅名称
			{ "gamemode", CL_GameManager.gamemode.gamemodeName }, // 游戏模式
		};

		try {
			// 直接 await 异步版本
			bool success = await _MPsteamworks.CreateRoomAsync(maxPlayers, lobbyData);

			if (success) {
				// 连接成功后的逻辑处理
				SetStatus(MPStatus.LOBBY_MASK, MPStatus.InLobby);
				//SetStatus(MPStatus.INIT_MASK, MPStatus.Initialized);

				switch (SceneManager.GetActiveScene().name) {
					// 主游戏需要相同种子的重载地图
					case "Game-Main": {
						WorldLoader.ReloadWithSeed(new string[] { WorldLoader.instance.seed.ToString() });
						break;
					}
					// 其他模式不需要重载地图
					default: {
						break;
					}
				}
				string lobby_id = _MPsteamworks.LobbyId.ToString();
				CopyToClipboard(lobby_id);
				CommandConsole.Log(Localization.Get("MPSteamworks.HostSuccess"));
			} else {
				// 失败处理
				SetStatus(MPStatus.LOBBY_MASK, MPStatus.LobbyConnectionError);
				CommandConsole.LogError(Localization.Get("CommandConsole.CreateLobbyFailed"));
			}
		} catch (Exception ex) {
			// 捕获任何未预料的崩溃
			SetStatus(MPStatus.LOBBY_MASK, MPStatus.LobbyConnectionError);
			MPMain.LogError(Localization.Get("CommandConsole.CriticalErrorDuringCreate",ex.Message));
		}
	}

	/// <summary>
	/// 加入大厅
	/// </summary>
	public async void Join(string[] args) {
		// 已经在大厅
		if (IsInLobby) {
			CommandConsole.LogError(Localization.Get("CommandConsole.AlreadyInOnlineMode"));
			return;
		}
		// 缺失名称/Id参数
		if (args.Length < 1) {
			CommandConsole.LogError(Localization.Get("CommandConsole.JoinUsage"));
			return;
		}

		string input = args[0];
		Lobby? targetLobby = null;

		CommandConsole.Log(Localization.Get("CommandConsole.SearchingLobbyByName", input));

		// 创建查询对象 设置过滤条件
		var query = new LobbyQuery()
			.FilterDistanceWorldwide()
			.WithKeyValue("game", "White Knuckle") // 确保是同一款游戏的
			.WithKeyValue("name", input)           // 匹配名称
			.WithMaxResults(5);
		try {
			// 进行异步查询
			var searchResults = await query.RequestAsync();

			if (searchResults != null && searchResults.Length == 1) {
				// 找到唯一名称大厅
				targetLobby = searchResults[0];
				CommandConsole.Log(Localization.Get("CommandConsole.FoundLobbyByName", targetLobby.Value.Id));
				if (targetLobby.HasValue) {
					await ExecuteJoinProcess(targetLobby.Value.Id);
					return;
				}
			} else if (searchResults != null && searchResults.Length > 1) {
				// 找到多个同名大厅
				foreach (var lobby in searchResults) {
					CommandConsole.Log(Localization.Get(
						"CommandConsole.LobbyInfo", lobby.Id, lobby.GetData("name"),
						lobby.GetData("owner"), lobby.GetData("gamemode")));
				}
				return;
			} else {
				// 通过数字寻找大厅并加入
				CommandConsole.Log(Localization.Get("CommandConsole.NoLobbyByNameTryId"));

				if (ulong.TryParse(input, out ulong lobbyId)) {
					await ExecuteJoinProcess(lobbyId);
					return;
				} else {
					CommandConsole.LogError(Localization.Get("CommandConsole.InvalidLobbyNameOrId"));
					return;
				}
			}
		} catch (Exception e) {
			MPMain.LogError(Localization.Get("MPCore.JoinLobbyException", e.Message));
		}
	}

	/// <summary>
	/// 通用的加入大厅流程函数,处理连接逻辑和错误管理
	/// </summary>
	public async void Join(Lobby lobby, SteamId steamId) {
		// 设置初始状态
		SetStatus(MPStatus.LOBBY_MASK, MPStatus.JoiningLobby);
		try {
			bool success = await _MPsteamworks.JoinRoomAsync(lobby);

			// 处理结果
			if (success) {
				SetStatus(MPStatus.LOBBY_MASK, MPStatus.InLobby);
			} else {
				SetStatus(MPStatus.LOBBY_MASK, MPStatus.LobbyConnectionError);
				MPMain.LogError(Localization.Get("MPCore.JoinLobbyFailed"));
			}
		} catch (Exception ex) {
			// 捕获任何未预料的异常 (网络崩溃、Steam客户端断开等)
			SetStatus(MPStatus.LOBBY_MASK, MPStatus.LobbyConnectionError);
			MPMain.LogError(Localization.Get("MPCore.CriticalErrorDuringJoin", ex.Message));
		}
	}

	/// <summary>
	/// 连接到特定Id的大厅
	/// </summary>
	private async Task ExecuteJoinProcess(ulong lobbyId) {
		MPMain.LogInfo(Localization.Get("MPCore.JoiningLobby", lobbyId.ToString()));

		// 设置初始状态
		SetStatus(MPStatus.LOBBY_MASK, MPStatus.JoiningLobby);

		try {
			// 直接 await 异步结果，代码逻辑变为线性
			bool success = await _MPsteamworks.JoinRoomAsync(new Lobby(lobbyId));

			// 处理结果
			if (success) {
				SetStatus(MPStatus.LOBBY_MASK, MPStatus.InLobby);
			} else {
				SetStatus(MPStatus.LOBBY_MASK, MPStatus.LobbyConnectionError);
				CommandConsole.LogError(Localization.Get("CommandConsole.JoinLobbyFailed"));
			}
		} catch (Exception ex) {
			// 捕获任何未预料的异常 (网络崩溃、Steam客户端断开等)
			SetStatus(MPStatus.LOBBY_MASK, MPStatus.LobbyConnectionError);
			MPMain.LogError(Localization.Get("CommandConsole.CriticalErrorDuringJoin", ex.Message));
		}
	}

	/// <summary>
	/// 离开大厅
	/// </summary>
	public void Leave(string[] args) {
		ResetStateVariables();
		// Debug
		MPMain.LogInfo(Localization.Get("MPCore.DisconnectedAndCleaned"));
	}

	/// <summary>
	/// 获取大厅ID
	/// </summary>
	public void GetLobbyId(string[] args) {
		if (!IsInLobby) {
			CommandConsole.LogError(Localization.Get("CommandConsole.NeedToBeOnline"));
			return;
		}
		string lobby_id = _MPsteamworks.LobbyId.ToString();
		CopyToClipboard(lobby_id);
		CommandConsole.Log(Localization.Get(
			"CommandConsole.LobbyIdOutput", lobby_id));
	}

	/// <summary>
	/// 发送信息到他人控制台
	/// </summary>
	public void Talk(string[] args) {
		if (!IsInLobby) {
			CommandConsole.LogError(Localization.Get("CommandConsole.NeedToBeOnline"));
			return;
		}
		// 将参数数组组合成一个字符串
		string message = string.Join(" ", args);

		var writer = GetWriter(_MPsteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.BroadcastMessage);
		writer.Put(message); // 自动处理长度和编码

		// 发送给所有人
		_MPsteamworks.Broadcast(writer);
	}

	/// <summary>
	/// 向某人TP
	/// </summary>
	public void TpToPlayer(string[] args) {
		if (!IsInLobby) {
			CommandConsole.LogError(Localization.Get("CommandConsole.NeedToBeOnline"));
			return;
		}
		if (!IsInitialized) {
			CommandConsole.LogError(Localization.Get("CommandConsole.WorldNotInitialized"));
			return;
		}
		if (ulong.TryParse(args[0], out ulong playerId)) {
			var ids = DictionaryExtensions.FindByKeySuffix(_RPManager.Players, playerId);
			// 未找到对应id
			if (ids.Count == 0) {
				CommandConsole.LogError(Localization.Get("CommandConsole.TargetIdNotFound"));
				return;
			}
			// 找到多个对应id
			if (ids.Count > 1) {
				string idStr = string.Join("\n", ids);
				CommandConsole.LogError(Localization.Get(
					"CommandConsole.MultipleMatchingIds", idStr));
				return;
			}
			// 找到对应id,发出传送请求
			var writer = GetWriter(_MPsteamworks.UserSteamId, ids[0], PacketType.PlayerTeleportRequest);
			_MPsteamworks.SendToPeer(ids[0], writer);
		}
	}

	/// <summary>
	/// 调试用,获取所有链接
	/// </summary>
	public void GetAllConnections(string[] args) {
		if (!IsInLobby) {
			CommandConsole.LogError(Localization.Get("CommandConsole.NeedToBeOnline"));
			return;
		}
		foreach (var (steamid, connection) in _MPsteamworks._outgoingConnections) {
			MPMain.LogInfo(Localization.Get(
				"MPCore.OutgoingConnectionLog", steamid.ToString(), connection.ToString()));
		}
		foreach (var (steamid, connection) in _MPsteamworks._allConnections) {
			MPMain.LogInfo(Localization.Get(
				"MPCore.AllConnectionLog", steamid.ToString(), connection.ToString()));
		}
	}

	/// <summary>
	/// 获取全部玩家
	/// </summary>
	public void GetAllPlayer(string[] args) {
		foreach (var friend in _MPsteamworks.Members) {
			CommandConsole.Log(Localization.Get(
				"CommandConsole.AllPlayer", friend.Name, friend.Id));
		}
	}

	/// <summary>
	/// 获取全部本mod开启的大厅
	/// </summary>
	public void GetAllLobby(string[] args) {
		StartLobbySearchAsync();
	}

	/// <summary>
	/// 通过指令获取全部大厅信息,包含Id/名称/房主/游戏模式等
	/// </summary>
	private async void StartLobbySearchAsync() {
		var query = new Steamworks.Data.LobbyQuery()
			.FilterDistanceWorldwide()
			.WithKeyValue("game", "White Knuckle")
			.WithMaxResults(20);
		var lobbies = await query.RequestAsync();
		if (lobbies != null) {
			foreach (var lobby in lobbies) {
				CommandConsole.Log(Localization.Get("CommandConsole.LobbyInfo", lobby.Id, lobby.GetData("name"),
					lobby.GetData("owner"), lobby.GetData("gamemode")));
			}
		}
	}

	/// <summary>
	/// 设置大厅可见性 参数: public/friends/private
	/// </summary>
	public void SetLobbyVisibility(string[] args) {
		bool success = args[0].ToLower() switch {
			"public" => _MPsteamworks._currentLobby.SetPublic(),
			"friends" => _MPsteamworks._currentLobby.SetFriendsOnly(),
			"private" => _MPsteamworks._currentLobby.SetPrivate(),
			_ => false
		};

		if (success) {
			CommandConsole.Log(Localization.Get("CommandConsole.LobbyVisibilitySet", args[0]));
		} else {
			CommandConsole.LogError(Localization.Get("CommandConsole.LobbyVisibilitySetFailed"));
		}
	}

	/// <summary>
	/// 通过指令邀请好友,会打开Steam邀请界面
	/// </summary>
	public void OpenSteamInviteUI(string[] args) {
		if (!IsInLobby) {
			CommandConsole.LogError(Localization.Get("CommandConsole.NeedToBeOnline"));
			return;
		}
		ulong lobby_id = _MPsteamworks.LobbyId;
		SteamFriends.OpenGameInviteOverlay(lobby_id);
	}
	#endregion

	#region[大厅/连接事件触发函数]

	/// <summary>
	/// 处理加入大厅事件
	/// </summary>
	/// <param name="lobby"></param>
	private void HandleLobbyEntered(Lobby lobby) {
		// Debug
		MPMain.LogInfo(Localization.Get("MPCore.EnteringLobby", lobby.Id.ToString()));

		// 启动协程发送请求初始化数据
		StartCoroutine(InitHandshakeRoutine());

		// 显示加入大厅信息
		StartCoroutine(ShowLobbyData());

		// 显示加入大厅信息
		IEnumerator ShowLobbyData() {
			while (IsInLobby && !IsInitialized) {
				yield return null;
			}
			if (WorldLoader.initialized) {
				while (!WorldLoader.isLoaded) {
					yield return null;
				}
			}
			yield return new WaitForSecondsRealtime(0.5f);
			var message = Localization.GetRandom("DisplayMessage.EnteredMessages", 
				lobby.GetData("name"), lobby.MemberCount, lobby.MaxMembers, lobby.Id.Value);
			SystemMessage(message, UIDisplayType.AscentHeader);
		}
	}

	/// <summary>
	/// 处理大厅成员加入
	/// </summary> 
	private void HandleLobbyMemberJoined(Friend friend) {
		if (friend.Id == _MPsteamworks.UserSteamId) return;
		var message = Localization.GetRandom("DisplayMessage.JoinedMessages", friend.Name);
		SystemMessage(message, UIDisplayType.AscentHeader);
	}

	/// <summary>
	/// 处理离开大厅事件
	/// </summary>
	/// <param name="friend"></param>
	private void HandleLobbyMemberLeft(Friend friend) {
		var message = Localization.GetRandom("DisplayMessage.LeaveMessages", friend.Name);
		SystemMessage(message, UIDisplayType.AscentHeader);
	}

	/// <summary>
	/// 处理事件总线 玩家连接OnPlayerConnected 发送PlayerCreateResponse
	/// </summary>
	private void HandlePlayerConnected(SteamId steamId) {
		// 创建玩家发包
		var writer = GetWriter(_MPsteamworks.UserSteamId, steamId, PacketType.PlayerCreateResponse);
		writer.Put(_LocalPlayer.FactoryId);
		_MPsteamworks.SendToPeer(steamId, writer);
	}

	/// <summary>
	/// 处理事件总线 玩家断连OnPlayerDisconnected
	/// </summary>
	private void HandlePlayerDisconnected(SteamId steamId) {
		// Debug
		MPMain.LogInfo(Localization.Get("MPCore.PlayerDisconnected", steamId.ToString()));
		_RPManager.PlayerRemove(steamId);
	}

	private void HandleLobbyInvite(Friend friend, Lobby lobby) { 
		var message = Localization.GetRandom("DisplayMessage.InviteReceivedMessages", friend.Name, lobby.GetData("name"));
		SystemMessage(message, UIDisplayType.AscentHeader);
	}

	#endregion

	#region [网络数据处理]

	/// <summary>
	/// 客户端发送WorldInitRequest: 协程请求初始化数据
	/// </summary>
	public IEnumerator InitHandshakeRoutine() {
		yield return new WaitForSeconds(0.5f);
		// 在大厅并且未加载
		while (IsInLobby && !IsInitialized) {
			MPMain.LogInfo(Localization.Get("MPCore.RequestedInitData"));
			var writer = GetWriter(_MPsteamworks.UserSteamId, _MPsteamworks.HostSteamId, PacketType.WorldInitRequest);
			_MPsteamworks.SendToHost(writer);
			yield return new WaitForSeconds(3.0f);
		}
	}

	#endregion

	#region[联机状态设置函数]

	//public MPCore SetStatus(MPStatus mask, MPStatus value) {
	//	MultiPlayerStatus.SetField(mask, value);
	//	return this;
	//}

	public static void SetStatus(MPStatus mask, MPStatus value) {
		MultiPlayerStatus.SetField(mask, value);
	}

	#endregion

	#region[工具函数]

	/// <summary>
	/// 获取物品清单字典
	/// </summary>
	public static Dictionary<string, byte> GetGetInventoryItems() {
		var inventory = Inventory.instance;
		var itemsDict = new Dictionary<string, byte>();

		if (inventory == null)
			MPMain.LogWarning(Localization.Get("MPCore.InventoryDoesNotExist"));
		else {
			// 获取库存中的物品列表
			var items = inventory.GetItems();
			foreach (var item in items) {
				itemsDict.TryAdd(item.prefabName, 0);
				itemsDict[item.prefabName]++;
			}
		}
		return itemsDict;
	}

	/// <summary>
	/// 在主要UI显示系统消息,并在控制台输出
	/// </summary>
	public static void SystemMessage(string message, UIDisplayType displayType) {
		UI_Manager.Instance.DisplayMessage(message, displayType);
		CommandConsole.Log($"[SYSTEM] {message}");
	}

	/// <summary>
	/// 将内容复制到系统剪贴板
	/// </summary>
	private static void CopyToClipboard(string text) {
		GUIUtility.systemCopyBuffer = text;
	}
	#endregion
}
