using Newtonsoft.Json;
using Steamworks;
using Steamworks.Data;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using WKMPMod.Asset;
using WKMPMod.Component;
using WKMPMod.Data;
using WKMPMod.NetWork;
using WKMPMod.Patch;
using WKMPMod.RemotePlayer;
using WKMPMod.UI;
using WKMPMod.Util;
using WKMPMod.World;
using static WKMPMod.Core.MPGameModeManager;
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
	// 玩家数量同步间隔
	private TickTimer _syncTick = new TickTimer(3f);

	// Steam网络管理器 本地数据获取类
	private MPSteamworks _MPSteamworks;
	private RPManager _RPManager;
	private LocalPlayer _LocalPlayer;
	private MPAssetManager _MPAssetManager;
	private UI_Manager _UIManager;

	// 多人模式状态
	public static MPStatus MultiPlayerStatus = MPStatus.NotInitialized;

	// 多人模式大厅规则
	public static bool IsAllowCheats { get; private set; }

	// 所在队伍
	public static string CurrentTeam { get; private set; } = MPKeys.DEFAULT_TEAM;

	// PVP伤害倍率
	public static DamageRules damageRules { get; private set; }

	// 是否处于大厅中
	public static bool IsInLobby => MultiPlayerStatus.IsInLobby();
	public static bool IsInitialized => MultiPlayerStatus.IsInitialized();
	// 是否满足同步条件(处于大厅中且已初始化且有连接)
	public static bool CanSync => IsInLobby && IsInitialized && MPSteamworks.Instance.HasConnections;

	// 防止没有解锁选项时被设定了饰品/绑定 在这里重置
	public static bool NeedResetTrinkets = false;
	public static string NeedResetGamemodeName = null;

	// 手部皮肤 -> 玩家模型ID 映射字典
	public static readonly Dictionary<string, string> HandSkinToModelId = new() {
		{ "default","default"},
		{ MPMain.SLUGCAT_HAND_ID, MPMain.SLUGCAT_BODY_FACTORY_ID },
		// 可在此添加更多映射
	};

	private InputAction _toggleAction;
	public static ENT_Player.InteractType IsGrabOrHangState = ENT_Player.InteractType.hanging;

	public static readonly List<string> checkOptions = new List<string> { "inventory", "perk", "stamina", "health", "cheats" };

	#region[Unity生命周期函数]
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

		// 初始化切换按键
		_toggleAction = new InputAction(name: "ToggleAction", binding: $"<Keyboard>/{MPConfig.ToggleKey}");
		_toggleAction.Enable();
	}

	void Update() {
		// 如果在大厅且已初始化且有连接,允许发送数据
		LocalPlayer.Instance.ShouldSendData = IsInLobby && IsInitialized && MPSteamworks.Instance.HasConnections;

		if (!IsInitialized || !IsInLobby) return;

		// 定期检查玩家数量和连接状态,修复异常状态
		CheckAndRepairPlayers();

		if (_toggleAction.triggered) {
			MPMain.LogInfo(Localization.Get("MPCore.UpdateDragHangToggle"));
			if (IsGrabOrHangState == ENT_Player.InteractType.grab) {
				IsGrabOrHangState = ENT_Player.InteractType.hanging;
				_RPManager.ChangeAllPlayerGrabOrHang(ENT_Player.InteractType.hanging);
			} else if (IsGrabOrHangState == ENT_Player.InteractType.hanging) {
				IsGrabOrHangState = ENT_Player.InteractType.grab;
				_RPManager.ChangeAllPlayerGrabOrHang(ENT_Player.InteractType.grab);
			}
		}
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

		// 关闭输入监听
		_toggleAction.Dispose();

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
			_MPSteamworks = MPSteamworks.Instance;

			// 创建远程玩家管理器
			_RPManager = RPManager.Instance;
			_RPManager.Initialize(transform);

			// 创建本地信息获取发送管理器
			_LocalPlayer = LocalPlayer.Instance;
			_LocalPlayer.Initialize(MPSteamworks.UserSteamId, MPConfig.RemotePlayerModel);

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

			// 初始化大厅共用数据
			damageRules = MPConfig.DamageRules;
			IsAllowCheats = MPConfig.AllowCheats;

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
		MPEventBusNet.OnLobbyDataChanged += HandleLobbyDataChanged;

		// 订阅玩家事件
		MPEventBusNet.OnPlayerConnected += HandlePlayerConnected;
		MPEventBusNet.OnPlayerDisconnected += HandlePlayerDisconnected;
		MPEventBusNet.OnMemberDataChanged += HandleMemberDataChanged;

		// 订阅接收邀请事件
		MPEventBusNet.OnGameLobbyJoinRequested += Join;

		// 订阅接受邀请事件
		MPEventBusNet.OnLobbyInvite += HandleLobbyInvite;

		// 订阅游戏事件
		MPEventBusGame.OnPlayerDamage += HandlePlayerDamage;
		MPEventBusGame.OnPlayerAddForce += HandlePlayerAddForce;
		MPEventBusGame.OnPlayerDeath += HandlePlayerDeath;
		MPEventBusGame.OnPlayerWin += HandlePlayerWin;
		MPEventBusGame.OnPlayerStopInteraction += HandlePlayerStopInteraction;
	}

	/// <summary>
	/// 取消所有网络事件订阅
	/// </summary>
	private void UnsubscribeFromEvents() {
		// 退订大厅事件
		MPEventBusNet.OnLobbyEntered -= HandleLobbyEntered;
		MPEventBusNet.OnLobbyMemberJoined -= HandleLobbyMemberJoined;
		MPEventBusNet.OnLobbyMemberLeave -= HandleLobbyMemberLeft;
		MPEventBusNet.OnLobbyDataChanged -= HandleLobbyDataChanged;

		// 退订玩家连接事件
		MPEventBusNet.OnPlayerConnected -= HandlePlayerConnected;
		MPEventBusNet.OnPlayerDisconnected -= HandlePlayerDisconnected;
		MPEventBusNet.OnMemberDataChanged -= HandleMemberDataChanged;

		// 退订接收邀请事件
		MPEventBusNet.OnGameLobbyJoinRequested -= Join;

		// 退订接受邀请事件
		MPEventBusNet.OnLobbyInvite -= HandleLobbyInvite;

		// 退订游戏事件
		MPEventBusGame.OnPlayerDamage -= HandlePlayerDamage;
		MPEventBusGame.OnPlayerAddForce -= HandlePlayerAddForce;
		MPEventBusGame.OnPlayerDeath -= HandlePlayerDeath;
		MPEventBusGame.OnPlayerWin -= HandlePlayerWin;
		MPEventBusGame.OnPlayerStopInteraction -= HandlePlayerStopInteraction;
	}

	#endregion
	#region[玩家数量同步]

	private void CheckAndRepairPlayers() {
		if (!_syncTick.TryTick()) return;
		// 在大厅但没有连接
		foreach (var member in _MPSteamworks.Members) {
			if (member.Id == MPSteamworks.UserSteamId) continue;
			if (!_MPSteamworks._allConnections.ContainsKey(member.Id)) {
				_MPSteamworks.ConnectionController(member.Id, true);
			}
		}
		// 有连接但没有创建对象
		foreach (var (steamId, connection) in _MPSteamworks._allConnections) {
			if (!_RPManager.Players.ContainsKey(steamId)) {
				MPMain.LogWarning(Localization.Get("MPCore.PlayerDataMissing", steamId));
				// 从MemberData获取模型数据
				var data = _MPSteamworks.GetAllMemberData(new Friend(steamId));
				//MPMain.Debug($"[MPCore] member data: {string.Join(",", data.Select(kvp => kvp.Key + ": " + kvp.Value))}");
				_RPManager.ProcessMemberData(steamId, data);
				//if (data.Count == 0)
				//	_MPSteamworks.SendToPeer(steamId,
				//	GetWriter(MPSteamworks.UserSteamId, steamId, PacketType.RequestMemberData));
			}
		}
	}

	#endregion
	#region[场景切换回调]

	/// <summary>
	/// 场景加载完成时调用
	/// </summary>
	private void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
		// 重置偏移高度
		Patch_CL_GameManager.RestartHeightOffset();
		IsGrabOrHangState = ENT_Player.InteractType.hanging;
		switch (scene.name) {
			case "Game-Main": {
				// 注册命令和初始化世界数据
				ChangeRPFactoryId();
				// 如果是主游戏场景且是房主,抓取当前模式数据并广播给其他人
				if (_MPSteamworks.IsHost) {
					// 设置当前游戏模式数据
					var currentModeData = MPGameModeManager.CaptureCurrentModeData();
					if (string.IsNullOrWhiteSpace(_MPSteamworks.LobbyData?.GetValueOrDefault(MPKeys.GAMEMODE_JSON))) {
						_MPSteamworks.SetLobbyData(MPKeys.GAMEMODE_JSON, JsonConvert.SerializeObject(currentModeData));
					}
					// 以后会在这里广播模式数据,用于房主切换游戏模式
					SetStatus(MPStatus.INIT_MASK, MPStatus.Initialized);
				}
				break;
			}
			case "Playground": {
				// 游乐场场景不需要重载地图,但需要初始化玩家模型ID
				SetStatus(MPStatus.INIT_MASK, MPStatus.Initialized);
				ChangeRPFactoryId();
				if (_MPSteamworks.IsHost) {
					// 设置当前游戏模式数据
					var currentModeData = MPGameModeManager.CaptureCurrentModeData();
					if (string.IsNullOrWhiteSpace(_MPSteamworks.LobbyData?.GetValueOrDefault(MPKeys.GAMEMODE_JSON))) {
						_MPSteamworks.SetLobbyData(MPKeys.GAMEMODE_JSON, JsonConvert.SerializeObject(currentModeData));
					}
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
		SetStatus(MPStatus.INIT_MASK, MPStatus.NotInitialized);
		SetStatus(MPStatus.LOBBY_MASK, MPStatus.NotInLobby);
		ClearCurrentData();
		_MPSteamworks.DisconnectAll();
		_RPManager.ResetAll();
		TeamRuleManager.ClearCache();
		ItemSyncManager.ResetState();
		// 是否需要重置饰品/绑定
		if (NeedResetTrinkets && NeedResetGamemodeName != null) {
			StatManager.saveData.SetGamemodeTrinkets(NeedResetGamemodeName, new List<string>());
			NeedResetTrinkets = false;
		}
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
	/// 发送伤害其他玩家数据<br/>
	/// 接受路由函数: <see cref="MPPacketHandlers.HandlePlayerDamage"/>
	/// </summary>
	private void HandlePlayerDamage(IDType steamId, Damageable.DamageInfo info) {
		var writer = GetWriter(MPSteamworks.UserSteamId, steamId, PacketType.PlayerDamage);
		writer.Put(info.amount);
		writer.Put(info.type);
		writer.Put(info.tags);

		_MPSteamworks.SendToPeer(steamId, writer);
	}

	/// <summary>
	/// 发送给予其他玩家冲击力数据<br/>
	/// 接受路由函数: <see cref="MPPacketHandlers.HandlePlayerAddForce"/><br/>
	/// </summary>
	private void HandlePlayerAddForce(IDType steamId, Vector3 force, string source) {
		var writer = GetWriter(MPSteamworks.UserSteamId, steamId, PacketType.PlayerAddForce);
		writer.Put(force.x);
		writer.Put(force.y);
		writer.Put(force.z);
		writer.Put(source);
		_MPSteamworks.SendToPeer(steamId, writer);
	}

	/// <summary>
	/// 发送玩家死亡信息<br/>
	/// 发送函数: <see cref="Patch_ENT_Player.Kill_NotifyPlayerDeath"/><br/>
	/// 发送PacketType.PlayerDeath: 库存物品 Dictionary&lt;string, short&gt;<br/>
	/// 接受路由函数: <see cref="MPPacketHandlers.HandlePlayerDeath"/><br/>
	/// 发送PacketType.GameUIMessage: 死因 string,UI类型 byte,持续时间 float,是否在控制台显示 bool<br/>
	/// 接受路由函数: <see cref="MPPacketHandlers.HandleSystemUIMessage"/><br/>
	/// </summary>
	private void HandlePlayerDeath(string type) {
		var writerDeath = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.PlayerDeath);

		// 库存物品字典
		writerDeath.Put(GetInventoryItems());

		// 发送背包道具
		_MPSteamworks.Broadcast(writerDeath);

		// 死亡信息获取
		var name = new Friend(MPSteamworks.UserSteamId).Name;
		var message = Localization.HasKey("0_DeathMessage", type)
			? Localization.GetRandomSplit("0_DeathMessage", type, name)
			: Localization.GetRandom("0_DeathMessage.default", type, name);

		var writerMessage = BuildingMessage(message, UIDisplayType.HighscoreHeader, logToConsole: true);

		if (writerMessage != null)
			// 发送死亡信息
			_MPSteamworks.Broadcast(writerMessage);

		// 顺便显示在自己的界面
		SystemMessage(message, UIDisplayType.HighscoreHeader);
	}

	/// <summary>
	/// 发送玩家胜利信息<br/>
	/// </summary>
	private void HandlePlayerWin() {
		var writerMessage = BuildingMessage(Localization.GetRandom("0_DisplayMessage.WinMessages"), UIDisplayType.TipHeader, logToConsole: true);
		if (writerMessage != null)
			// 发送胜利信息
			_MPSteamworks.Broadcast(writerMessage);
	}

	/// <summary>
	/// 发送玩家停止交互信息<br/>
	/// </summary>
	/// <param name="steamId"></param>
	private void HandlePlayerStopInteraction(IDType steamId) {
		MPSteamworks.Instance.SendToPeer(steamId, GetWriter(MPSteamworks.UserSteamId, steamId, PacketType.PlayerStopInteraction), SendType.Reliable);
	}

	#endregion
	#region[命令注册]

	/// <summary>
	/// 命令注册
	/// </summary>
	public void RegisterCommands() {
		// 将命令注册到 CommandConsole
		RegisterLobbyCommands();
		RegisterRuleCommands();
		RegisterPlayerCommands();
		RegisterRconCommand();

		// 获取大厅全部玩家
		CommandConsole.BuildCommand("allplayer", (args) => {
			foreach (var friend in _MPSteamworks.Members) {
				Vector3 position = friend.Id == MPSteamworks.UserSteamId ? Vector3.zero : _RPManager.GetPlayerObject(friend.Id)?.transform.position ?? Vector3.zero;
				float distance = position == Vector3.zero ? 0 : Vector3.Distance(LocalPlayer.Instance.transform.position, position);
				CommandConsole.Log(Localization.Get(
					"CommandConsole.AllPlayer", friend.Name, friend.Id, distance, position));
			}
		})
			.NotCheat().Description(Localization.Get("CommandHelp.AllPlayer"))
			.OverValue(() => _MPSteamworks.IsInLobby
				? $"Player: {_MPSteamworks.Members.Count()}/{_MPSteamworks.LobbySize}"
				: "Not In Lobby")
			.AutocompleteValidator(validator => { if (!_MPSteamworks.IsInLobby) validator.Reject(); });


		// 邀请其他好友
		CommandConsole.BuildCommand("invite", (args) => {
			if (!EnsureInLobby()) return;
			ulong lobby_id = _MPSteamworks.LobbyId;
			SteamFriends.OpenGameInviteOverlay(lobby_id);

		})
			.NotCheat().Description(Localization.Get("CommandHelp.Invite"))
			.OverValue(() => _MPSteamworks.IsInLobby ? _MPSteamworks.LobbyId : "Not In Lobby");


	}

	/// <summary>
	/// 大厅相关命令注册
	/// </summary>
	public void RegisterLobbyCommands() {
		// 创建大厅
		CommandConsole.BuildCommand("host", Host)
			.NotCheat().Description(Localization.Get("CommandHelp.Host"))
			.AutocompleteCustom(HostAutocomplete)
			.AutocompleteValidator(HostValidator);

		// 加入大厅
		CommandConsole.BuildCommand("join", Join)
			.NotCheat().Description(Localization.Get("CommandHelp.Join"))
			.AutocompleteCustom(JoinAutocomplete);

		// 离开大厅
		CommandConsole.BuildCommand("leave", Leave)
			.NotCheat().Description(Localization.Get("CommandHelp.Leave"))
			.OverValue(() => _MPSteamworks.IsInLobby ? "In Lobby" : "Not In Lobby")// 显示默认值																	   
			.AutocompleteValidator(validator => { if (!_MPSteamworks.IsInLobby) validator.Reject(); });// 不在大厅则变红

		// 获取大厅ID
		CommandConsole.BuildCommand("lobbyid", (args) => {
			if (!EnsureInLobby()) return;
			string lobby_id = _MPSteamworks.LobbyId.ToString();
			CopyToClipboard(lobby_id);
			CommandConsole.Log(Localization.Get("CommandConsole.LobbyIdOutput", lobby_id));
		})
			.NotCheat().Description(Localization.Get("CommandHelp.LobbyId"))
			.OverValue(() => _MPSteamworks.IsInLobby ? _MPSteamworks.LobbyId : "Not In Lobby")
			.AutocompleteValidator(validator => { if (!_MPSteamworks.IsInLobby) validator.Reject(); });

		// 获取全部大厅
		CommandConsole.BuildCommand("lobbylist", GetAllLobby)
			.NotCheat().Description(Localization.Get("CommandHelp.LobbyList"));

		// 设置大厅可见度
		CommandConsole.BuildCommand("lobbytype", SetLobbyVisibility)
			.NotCheat().Description(Localization.Get("CommandHelp.LobbyType"))
			.OverValue(() => _MPSteamworks.IsInLobby
				? (_MPSteamworks.LobbyData?.GetValueOrDefault(MPKeys.LOBBY_VISIBILITY) ?? "unknown value")
				: "Not In Lobby")
			.AutocompleteCustom(autocomplete => {
				if (autocomplete.activeArg == 0) autocomplete.FromArray(new[] { "public", "friends", "private" });
			})
			.AutocompleteValidator(validator => {
				if (validator.activeArg == 1) {
					string vis = validator.ArgumentAt(1).ToLower();
					if (vis != "public" && vis != "friends" && vis != "private")
						validator.Reject();
				}
			});

		// 设置大厅名称
		CommandConsole.BuildCommand("setlobbyname", (args) => {
			if (!EnsureHostPrivileges()) return;
			_MPSteamworks.SetLobbyData(MPKeys.LOBBY_NAME, string.Join(" ", args));
		})
			.NotCheat().Description(Localization.Get("CommandHelp.SetLobbyName"));
	}

	/// <summary>
	/// 规则相关命令注册
	/// </summary>
	public void RegisterRuleCommands() {

		// 设置是否可开启作弊模式
		CommandConsole.BuildCommand("allowcheats", (args) => {
			if (!EnsureHostPrivileges()) return;
			bool enabled = false;
			if (args.Length == 0 && bool.TryParse(_MPSteamworks.LobbyData?.GetValueOrDefault(MPKeys.ALLOW_CHEATS), out bool result1)) {
				enabled = !result1;     // 如果没有参数 获取大厅数据并取反 || 取否
			} else if (bool.TryParse(args[0], out bool result2))
				enabled = result2;      // 有参数直接使用参数
			MPConfig.AllowCheats = IsAllowCheats = enabled;
			_MPSteamworks.SetLobbyData(MPKeys.ALLOW_CHEATS, enabled.ToString());
		})
			.NotCheat().Description(Localization.Get("CommandHelp.AllowCheats"))
			.OverValue(() => _MPSteamworks.IsInLobby
				? (_MPSteamworks.LobbyData?.GetValueOrDefault(MPKeys.ALLOW_CHEATS) ?? "unknown value")
				: "Not In Lobby")
			.AutocompleteCustom(autocomplete => {
				if (autocomplete.activeArg == 0 && _MPSteamworks.IsHost)
					autocomplete.FromArray(new[] { "True", "False" });
				if (autocomplete.activeArg == 0 && !_MPSteamworks.IsHost)
					autocomplete.FromArray(new[] { "You Are Not Host" });
			})
			.AutocompleteValidator(validator => {
				if (validator.activeArg == 1) {
					string vis = validator.ArgumentAt(1).ToLower();
					if (vis != "True" && vis != "False")
						validator.Reject(); // 不匹配则高亮红色
				}
			});

		// 设置是否需要饰品/绑定同步
		CommandConsole.BuildCommand("bindsync", (args) => {
			if (!EnsureHostPrivileges()) return;
			bool enabled = false;
			if (args.Length == 0 && bool.TryParse(_MPSteamworks.LobbyData?.GetValueOrDefault(MPKeys.BIND_SYNC), out bool result1))
				enabled = !result1; // 如果没有参数 获取大厅数据并取反 || 取否
			else if (bool.TryParse(args[0], out bool result2))
				enabled = result2;  // 有参数直接使用参数
			MPConfig.BindSync = enabled;
		})
			.NotCheat().Description(Localization.Get("CommandHelp.BindSync"))
			.OverValue(() => _MPSteamworks.IsInLobby ? (MPConfig.BindSync.ToString()) : "Not In Lobby")
			.AutocompleteCustom(autocomplete => {
				if (autocomplete.activeArg == 0 && _MPSteamworks.IsHost)
					autocomplete.FromArray(new[] { "True", "False" });
				if (autocomplete.activeArg == 0 && !_MPSteamworks.IsHost)
					autocomplete.FromArray(new[] { "You Are Not Host" });
			})
			.AutocompleteValidator(validator => {
				if (validator.activeArg == 1) {
					string vis = validator.ArgumentAt(1).ToLower();
					if (vis != "True" && vis != "False")
						validator.Reject(); // 不匹配则高亮红色
				}
			});

		CommandConsole.BuildCommand("teamrule", SetTeamRule)
			.NotCheat().Description(Localization.Get("CommandHelp.TeamRule"))
			.AutocompleteCustom(TeamRuleAutocomplete)
			.AutocompleteValidator(TeamRuleValidator);

		// 注册 addteam 指令
		CommandConsole.BuildCommand("addteam", AddTeamCommand)
			.NotCheat().Description(Localization.Get("CommandHelp.AddTeam"))
			.OverValue(() => _MPSteamworks.IsInLobby ? (TeamRuleManager.activeTeams) : "Not In Lobby")
			.AutocompleteCustom(autocomplete => { if (!_MPSteamworks.IsHost) autocomplete.FromArray(new[] { "You Are Not Host" }); });

		// 注册 removeteam 指令
		CommandConsole.BuildCommand("removeteam", RemoveTeamCommand)
			.NotCheat().Description(Localization.Get("CommandHelp.RemoveTeam"))
			.OverValue(() => _MPSteamworks.IsInLobby ? (TeamRuleManager.activeTeams) : "Not In Lobby")
			.AutocompleteCustom(autocomplete => {
				if (!_MPSteamworks.IsHost) {
					autocomplete.FromArray(new[] { "You Are Not Host" });
					return;
				}
				// 只能删除当前存在的队伍, 且排除 default
				autocomplete.FromArray(TeamRuleManager.activeTeams
					.Where(t => t != MPKeys.DEFAULT_TEAM.ToLower()).ToList());
			})
			.AutocompleteValidator(validator => {
				// 强行拦截 default
				if (validator.ArgumentAt(validator.activeArg).ToLower() == MPKeys.DEFAULT_TEAM.ToLower())
					validator.Reject();
			});

		// 设置是否可PVP
		CommandConsole.BuildCommand("allowpvp", SetPvp)
			.NotCheat().Description(Localization.Get("CommandHelp.AllowPVP"))
			.OverValue(() => _MPSteamworks.IsInLobby
				? TeamRuleManager.GetRule(MPKeys.DEFAULT_TEAM, MPKeys.DEFAULT_TEAM, RuleType.Pvp, false)
				: "Not In Lobby")
			.AutocompleteCustom(autocomplete => {
				if (autocomplete.activeArg == 0 && _MPSteamworks.IsHost)
					autocomplete.FromArray(new[] { "True", "False" });
				if (autocomplete.activeArg == 0 && !_MPSteamworks.IsHost)
					autocomplete.FromArray(new[] { "You Are Not Host" });
			})
			.AutocompleteValidator(validator => {
				if (validator.activeArg == 1) {
					string vis = validator.ArgumentAt(1).ToLower();
					if (vis != "True" && vis != "False")
						validator.Reject();
				}
			});

	}

	/// <summary>
	/// 玩家相关命令注册
	/// </summary>
	public void RegisterPlayerCommands() {

		// 向大厅广播
		CommandConsole.BuildCommand("talk", Talk)
			.NotCheat().Description(Localization.Get("CommandHelp.Talk"));

		// tp到某人(同步背包物品)
		CommandConsole.BuildCommand("tpto", TpToPlayer)
			.Description(Localization.Get("CommandHelp.TpTo"))
			.AutocompleteCustom(autocomplete => {
				if (autocomplete.activeArg == 0)
					autocomplete.FromArrayWithDesc(_RPManager.Players.Values.Select(container => (
								id: container.PlayerId.ToString(), name: container.PlayerName)).ToList());
			})
			.AutocompleteValidator(validator => {
				// 参数0 是 可以转为ulong 是 其他玩家Id不报红
				if (validator.activeArg == 0
					&& ulong.TryParse(validator.ArgumentAt(0), out var playerId)
					&& _RPManager.Players.ContainsKey(playerId))
					return;
				validator.Reject();
			});

		// 修改玩家模型
		CommandConsole.BuildCommand("changemodel", (args) => {
			_LocalPlayer.DefaulFactoryId = args[0];
			MPConfig.RemotePlayerModel = args[0];
			_MPSteamworks.SetMemberData(MPKeys.PREFAB_ID, args[0]);
			//_MPSteamworks.SendAllMemberData();
		})
			.NotCheat().Description(Localization.Get("CommandHelp.ChangeModel"))
			.AutocompleteCustom(autocomplete => {
				if (autocomplete.activeArg == 0)
					autocomplete.FromArray(RPFactoryManager.ModelIDs);
			});

		// 加入队伍
		CommandConsole.BuildCommand("jointeam", JoinTeam)
			.NotCheat().Description(Localization.Get("CommandHelp.JoinTeam"))
			.OverValue(() => _MPSteamworks.IsInLobby ? CurrentTeam : "Not In Lobby")
			.AutocompleteCustom(autocomplete => {
				if (autocomplete.activeArg == 0) autocomplete.FromArray(TeamRuleManager.activeTeams.ToList());
			});


		// 设置名称
		CommandConsole.BuildCommand("setname", (args) => {
			string name = string.Join(", ", args);
			MPConfig.RemotePlayerName = name;
			_MPSteamworks.SetMemberData(MPKeys.PLAYER_NAME, name);
			//_MPSteamworks.SendAllMemberData();
		}).NotCheat().Description(Localization.Get("CommandHelp.SetName"));

		// 检查其他玩家数据
		CommandConsole.BuildCommand("check", Check)
			.NotCheat().Description(Localization.Get("CommandHelp.Check"))
			.AutocompleteCustom(autocomplete => {
				if (autocomplete.activeArg == 0) autocomplete.FromArray(checkOptions);
				else autocomplete.FromArrayWithDesc(_RPManager.Players.Values.Select(container => (
								id: container.PlayerId.ToString(), name: container.PlayerName)).ToList());
			})
			.AutocompleteValidator(validator => {
				// 参数是0 是 选项中的一项
				if (validator.activeArg == 0 && checkOptions.Contains(validator.ArgumentAt(0).ToLower()))
					return;
				// 参数大于0 是 可以转为ulong 是 其他玩家Id不报红
				if (validator.activeArg > 0
					&& ulong.TryParse(validator.ArgumentAt(validator.activeArg), out var playerId)
					&& _RPManager.Players.ContainsKey(playerId))
					return;
				validator.Reject();
			});
	}

	/// <summary>
	/// 控制执行远程命令相关命令注册
	/// </summary>
	public void RegisterRconCommand() {
		CommandConsole.BuildCommand("pcmd", ExecutePcmd)
			.Description(Localization.Get("CommandHelp.PCMD"))
			.AutocompleteCustom(PcmdAutocomplete)
			.AutocompleteValidator(validator => NestedCommandEngine.ForwardValidator(validator, defaultStartIndex: 1));
		CommandConsole.BuildCommand("tcmd", ExecuteTcmd)
			.Description(Localization.Get("CommandHelp.TCMD"))
			.AutocompleteCustom(TcmdAutocomplete)
			.AutocompleteValidator(validator => NestedCommandEngine.ForwardValidator(validator, defaultStartIndex: 1));
		CommandConsole.BuildCommand("acmd", ExecuteAcmd)
			.Description(Localization.Get("CommandHelp.ACMD"))
			.AutocompleteCustom(AcmdAutocomplete)
			.AutocompleteValidator(AcmdValidator);
	}

	#endregion
	#region[大厅操作]

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

		string lobbyName = args.Length > 1 ? args[0] : "New Lobby";
		string visibility = args.Length >= 2 ? args[1] : "public";
		int maxPlayers = 8;
		if (args.Length > 2 && int.TryParse(args[2], out int parsedMax)) {
			maxPlayers = parsedMax;
		}
		// Debug
		MPMain.LogInfo(Localization.Get("MPCore.CreatingLobby", lobbyName));

		// 设置状态为正在连接
		SetStatus(MPStatus.LOBBY_MASK, MPStatus.JoiningLobby);

		// 预设置大厅数据
		var lobbyData = new Dictionary<string, string>() {
			{ MPKeys.LOBBY_NAME, lobbyName },         // 大厅名称
		};

		try {
			// 直接 await 异步版本
			bool success = await _MPSteamworks.CreateRoomAsync(maxPlayers, lobbyData);

			if (success) {
				// 连接成功后的逻辑处理
				SetStatus(MPStatus.LOBBY_MASK, MPStatus.InLobby);

				// 设置大厅可见性
				SetLobbyVisibility(new string[] { visibility });

				switch (SceneManager.GetActiveScene().name) {
					// 主游戏需要相同种子的重载地图
					case "Game-Main": {
						WorldLoader.ReloadWithSeed(new string[] { WorldLoader.instance.seed.ToString() });
						break;
					}
					case "Playground": {
						// 设置当前游戏模式数据
						var currentModeData = MPGameModeManager.CaptureCurrentModeData();
						if (string.IsNullOrWhiteSpace(_MPSteamworks.LobbyData?.GetValueOrDefault(MPKeys.GAMEMODE_JSON)))
							_MPSteamworks.SetLobbyData(MPKeys.GAMEMODE_JSON, JsonConvert.SerializeObject(currentModeData));
						break;
					}
					// 其他模式不需要重载地图
					default: {
						break;
					}
				}
				string lobby_id = _MPSteamworks.LobbyId.ToString();
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
			MPMain.LogError(Localization.Get("CommandConsole.CriticalErrorDuringCreate", ex.Message));
		}
	}

	public void HostAutocomplete(CommandConsole.CommandAutocomplete autocomplete) {
		// activeArg 表示当前正在输入的参数位置
		if (autocomplete.activeArg == 1)// 第二参数
			autocomplete.FromArray(new[] { "public", "friends", "private" });
		else if (autocomplete.activeArg == 2)
			autocomplete.FromArray(new[] { "2", "4", "8", "16" });
	}

	public void HostValidator(CommandConsole.CommandValidator validator) {
		if (validator.activeArg == 1) {
			string vis = validator.ArgumentAt(1).ToLower();
			if (vis != "public" && vis != "friends" && vis != "private")
				validator.Reject(); // 不匹配则高亮红色
		} else if (validator.activeArg == 2 && !int.TryParse(validator.ArgumentAt(2), out _))
			validator.Reject();
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

		string input = string.Join(" ", args);
		Lobby? targetLobby = null;

		CommandConsole.Log(Localization.Get("CommandConsole.SearchingLobbyByName", input));

		try {
			// 进行异步查询
			var LobbyList = await _MPSteamworks.RefreshLobbyListAsync();

			var searchResults = LobbyList.Where(lobby => lobby.GetData(MPKeys.LOBBY_NAME) == input).ToList();

			if (searchResults != null && searchResults.Count == 1) {
				// 找到唯一名称大厅
				targetLobby = searchResults[0];
				CommandConsole.Log(Localization.Get("CommandConsole.FoundLobbyByName", targetLobby.Value.Id));
				if (targetLobby.HasValue) {
					await ExecuteJoinProcess(targetLobby.Value.Id);
					return;
				}
			} else if (searchResults != null && searchResults.Count > 1) {
				// 找到多个同名大厅
				foreach (var lobby in searchResults) {
					var gamemode = lobby.GetData(MPKeys.GAMEMODE_JSON);
					try {
						GameModeData gameModeData = JsonConvert.DeserializeObject<GameModeData>(gamemode);
						if (gameModeData != null) gamemode = gameModeData.gameModeName;
					} catch (Exception ex) {
						MPMain.LogError(Localization.Get("MPCore.GamemodeParseError", gamemode, ex.Message));
					}

					CommandConsole.Log(Localization.Get(
						"CommandConsole.LobbyInfo", lobby.Id, lobby.GetData(MPKeys.LOBBY_NAME),
						lobby.GetData(MPKeys.OWNER_NAME), gamemode));
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
		SetStatus(MPStatus.INIT_MASK, MPStatus.NotInitialized);
		try {
			bool success = await _MPSteamworks.JoinRoomAsync(lobby);

			// 处理结果
			if (success) {
				SetStatus(MPStatus.LOBBY_MASK, MPStatus.InLobby);
			} else {
				SetStatus(MPStatus.LOBBY_MASK, MPStatus.LobbyConnectionError);
				MPMain.LogError(Localization.Get("MPCore.JoinLobbyFailed"));
			}
		} catch (Exception ex) {
			// 捕获任何未预料的异常 (网络崩溃, Steam客户端断开等)
			SetStatus(MPStatus.LOBBY_MASK, MPStatus.LobbyConnectionError);
			MPMain.LogError(Localization.Get("MPCore.CriticalErrorDuringJoin", ex.Message));
		}
	}

	public void JoinAutocomplete(CommandConsole.CommandAutocomplete autocomplete) {
		// activeArg 表示当前正在输入的参数位置
		_ = _MPSteamworks.RefreshLobbyListAsync();
		if (autocomplete.activeArg == 0) {
			autocomplete.FromArrayWithDesc(_MPSteamworks.LastFetchedLobbies
				.Select(lobby => (desc: lobby.Id.ToString(), name: lobby.GetData(MPKeys.LOBBY_NAME) ?? "Unnamed Lobby"))
				.ToList());
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
			// 直接 await 异步结果, 代码逻辑变为线性
			bool success = await _MPSteamworks.JoinRoomAsync(new Lobby(lobbyId));

			// 处理结果
			if (success) {
				SetStatus(MPStatus.LOBBY_MASK, MPStatus.InLobby);
			} else {
				SetStatus(MPStatus.LOBBY_MASK, MPStatus.LobbyConnectionError);
				CommandConsole.LogError(Localization.Get("CommandConsole.JoinLobbyFailed"));
			}
		} catch (Exception ex) {
			// 捕获任何未预料的异常 (网络崩溃, Steam客户端断开等)
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
	/// 设置大厅可见性 参数: public/friends/private
	/// </summary>
	public void SetLobbyVisibility(string[] args) {

		if (!EnsureHostPrivileges()) return;

		bool success = args[0].ToLower() switch {
			"public" => _MPSteamworks._currentLobby.SetPublic(),
			"friends" => _MPSteamworks._currentLobby.SetFriendsOnly(),
			"private" => _MPSteamworks._currentLobby.SetPrivate(),
			_ => false
		};
		if (success) {
			_MPSteamworks._currentLobby.SetData(MPKeys.LOBBY_VISIBILITY, args[0].ToLower());
			CommandConsole.Log(Localization.Get("CommandConsole.LobbyVisibilitySet", args[0]));
		} else {
			CommandConsole.LogError(Localization.Get("CommandConsole.LobbyVisibilitySetFailed"));
		}


	}

	#endregion
	#region[队伍/规则操作]

	/// <summary>
	/// 设置队伍规则. 支持批量设置, 以逗号分隔不同队伍间的规则块. 每个规则块格式: [队伍A] [队伍B] [规则名] [true|false] ...<br/>
	/// 例 : teamrule Red Blue pvp true grab false , Red Green pvp false hang true<br/>
	/// </summary>
	/// <param name="args"></param>
	public void SetTeamRule(string[] args) {
		if (args == null || args.Length < 4) {
			CommandConsole.LogError(Localization.Get("CommandConsole.TeamRuleInsufficientParams"));
			return;
		}

		string fullInput = string.Join(" ", args);
		string[] chunks = fullInput.Split(',');

		foreach (string chunk in chunks) {
			string[] parts = chunk.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length < 4) continue;

			string attackerTeam = parts[0];
			string targetTeam = parts[1];
			string key = TeamRuleManager.GetRuleKey(attackerTeam, targetTeam);

			// 获取现有规则克隆副本, 或创建新规则
			TeamRule rule = TeamRuleManager.GetAllRules().TryGetValue(key, out var existing)
				? existing.Clone()
				: new TeamRule();

			// 循环让规则对象自己更新自己
			for (int i = 2; i < parts.Length - 1; i += 2) {
				rule.UpdateRule(parts[i].ToLower(), parts[i + 1].ToLower());
			}

			// 序列化, 同步, 持久化
			string compressedData = rule.SerializeTeamRule();
			TeamRuleManager.UpdateRuleCache(key, compressedData);
			_MPSteamworks.SetLobbyData(key, compressedData);
		}

		RuleConfigLoader.SaveCurrentRulesToFile();
	}

	private void TeamRuleAutocomplete(CommandConsole.CommandAutocomplete autocomplete) {
		if (!EnsureHostPrivileges()) {
			autocomplete.FromArray(new[] { "You Are Not Host" });
			return;
		}
		int argIndex = autocomplete.activeArg;
		for (int i = argIndex; i > 0; i--) {
			// 如果在逗号处, 则重置参数索引到逗号后第一个参数的位置
			if (autocomplete.ArgumentAt(i - 1) == ",") {
				argIndex -= i; break;
			}
		}

		// 参数 0 和参数 1 恒定为队伍名称
		if (argIndex == 0 || argIndex == 1) {
			// 获取当前游戏所有队伍名的列表
			autocomplete.FromArray(TeamRuleManager.activeTeams.ToList());
			return;
		}
		// 参数 偶数 则为规则, 参数为 奇数 则为规则值
		if (argIndex % 2 == 0) {
			autocomplete.FromArray(TeamRule.ruleFieldNames);
		} else {
			autocomplete.FromArray(new[] { "true", "false", "default" });
		}
	}

	private void TeamRuleValidator(CommandConsole.CommandValidator validator) {
		int argIndex = validator.activeArg;
		for (int i = argIndex; i > 0; i--) {
			if (validator.ArgumentAt(i - 1) == ",") {
				argIndex -= i; break;
			}
		}
		if (argIndex == 0 || argIndex == 1) {
			string teamName = validator.ArgumentAt(argIndex).Trim().ToLower();
			if (!TeamRuleManager.activeTeams.Contains(teamName)) {
				validator.Reject();
			}
		} else if (argIndex % 2 == 0) {
			string ruleName = validator.ArgumentAt(argIndex).Trim().ToLower();
			if (!TeamRule.ruleFieldNames.Contains(ruleName) && ruleName != ",") {
				validator.Reject();
			}
		} else {
			string val = validator.ArgumentAt(argIndex).Trim().ToLower();
			if (val != "true" && val != "false" && val != "default") {
				validator.Reject();
			}
		}
	}

	/// <summary>
	/// 添加一个活跃队伍, 活跃队伍会出现在规则设置中供选择. 格式: addteam [队伍名]
	/// </summary>
	public void AddTeamCommand(string[] args) {
		if (!EnsureHostPrivileges()) return;
		if (args == null || args.Length < 1) {
			CommandConsole.LogError(Localization.Get("CommandConsole.TeamRuleAddTeamInsufficientParams"));
			return;
		}

		string teamName = args[0].Trim().ToLower();
		if (string.IsNullOrEmpty(teamName)) return;

		TeamRuleManager.AddActiveTeam(teamName);

		// 更新大厅中的队伍列表字符串, 触发客户端同步
		_MPSteamworks.SetLobbyData(MPKeys.ACTIVE_TEAMS, string.Join(",", TeamRuleManager.activeTeams));
	}

	/// <summary>
	/// 删除一个活跃队伍及其相关规则, 格式: removeteam [队伍名]. 注意无法删除系统默认队伍 (default)
	/// </summary>
	public void RemoveTeamCommand(string[] args) {
		if (!EnsureHostPrivileges()) return;
		if (args == null || args.Length < 1) {
			CommandConsole.LogError(Localization.Get("CommandConsole.TeamRuleRemoveTeamInsufficientParams"));
			return;
		}

		string teamName = args[0].Trim().ToLower();
		if (teamName == MPKeys.DEFAULT_TEAM.ToLower()) {
			CommandConsole.LogError(Localization.Get("CommandConsole.TeamRuleCannotRemoveDefaultTeam"));
			return;
		}

		// 该方法内部会同步清理相关规则路由
		TeamRuleManager.RemoveActiveTeam(teamName);

		// 网络同步与持久化
		_MPSteamworks.SetLobbyData(MPKeys.ACTIVE_TEAMS, string.Join(",", TeamRuleManager.activeTeams));
		RuleConfigLoader.SaveCurrentRulesToFile();
	}

	/// <summary>
	/// 设置是否默认队伍间是否允许PVP
	/// </summary>
	public void SetPvp(string[] args) {
		if (!EnsureHostPrivileges()) return;
		bool enabled = false;
		if (args.Length == 0) {
			enabled = !TeamRuleManager.GetRule(MPKeys.DEFAULT_TEAM, MPKeys.DEFAULT_TEAM, RuleType.Pvp, false);
		} else if (bool.TryParse(args[0], out bool result)) {
			enabled = result;
		} else {
			CommandConsole.LogError(Localization.Get("CommandConsole.SetPvpInvalidParams"));
			return;
		}
		MPConfig.AllowPVP = enabled;
		_MPSteamworks.SetLobbyData(
			TeamRuleManager.GetRuleKey(MPKeys.DEFAULT_TEAM, MPKeys.DEFAULT_TEAM),
			TeamRuleManager.SetRule(MPKeys.DEFAULT_TEAM, MPKeys.DEFAULT_TEAM, RuleType.Pvp, enabled));
		RuleConfigLoader.SaveCurrentRulesToFile();
	}

	/// <summary>
	/// 加入一个队伍
	/// </summary>
	public void JoinTeam(string[] args) {
		if (!EnsureInLobby()) return;
		string teamName = (args == null || args.Length < 1)
			? MPKeys.DEFAULT_TEAM
			: args[0].Trim().ToLower();

		if (string.IsNullOrEmpty(teamName)) return;

		if (!TeamRuleManager.activeTeams.Contains(teamName)) {
			CommandConsole.LogError(Localization.Get("CommandConsole.TeamNotExist", teamName));
			return;
		}
		// 更新当前队伍和玩家间规则
		CurrentTeam = teamName;
		TeamRuleManager.UpdateActiveRules(CurrentTeam);
		_RPManager.RefreshAllRule();
		// 使用加入队伍指令
		if (_MPSteamworks.LobbyData.TryGetValue(MPKeys.JOIN_TEAM_COMMAND + "_" + teamName, out string? joinCmd))
			Patch_CommandConsole.ExecuteCommandForcefully(joinCmd);
		// 更新玩家数据, 触发同步
		_MPSteamworks.SetMemberData(MPKeys.TEAM, teamName);
		//_MPSteamworks.SendAllMemberData();
		CommandConsole.Log(Localization.Get("CommandConsole.JoinedTeam", teamName));
	}

	#endregion
	#region[玩家间操作]

	/// <summary>
	/// 发送信息到他人控制台
	/// </summary>
	public void Talk(string[] args) {
		if (!EnsureInLobby()) return;

		// 将参数数组组合成一个字符串
		string message = string.Join(" ", args);

		var writer = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.BroadcastMessage);
		writer.Put(message); // 自动处理长度和编码

		// 发送给所有人
		_MPSteamworks.Broadcast(writer);
	}

	/// <summary>
	/// 向某人TP
	/// </summary>
	public void TpToPlayer(string[] args) {
		if (!EnsureInLobby()) return;

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
			var writer = GetWriter(MPSteamworks.UserSteamId, ids[0], PacketType.PlayerTeleportRequest);
			_MPSteamworks.SendToPeer(ids[0], writer);
		}
	}

	/// <summary>
	/// 通过指令获取全部大厅信息,包含Id/名称/房主/游戏模式等
	/// </summary>
	public async void GetAllLobby(string[] args) {
		await _MPSteamworks.RefreshLobbyListAsync();
		foreach (var lobby in _MPSteamworks.LastFetchedLobbies) {
			var gamemode = lobby.GetData(MPKeys.GAMEMODE_JSON);
			try {
				GameModeData gameModeData = JsonConvert.DeserializeObject<GameModeData>(gamemode);
				if (gameModeData != null) gamemode = gameModeData.gameModeName;
			} catch (Exception ex) {
				MPMain.LogError(Localization.Get("MPCore.GamemodeParseError", gamemode, ex.Message));
			}

			CommandConsole.Log(Localization.Get(
				"CommandConsole.LobbyInfo", lobby.Id, lobby.GetData(MPKeys.LOBBY_NAME),
				lobby.GetData(MPKeys.OWNER_NAME), gamemode));
		}
	}

	public void Check(string[] args) {
		if (!EnsureInLobby()) return;

		if (args.Length == 0 || !checkOptions.Contains(args[0].ToLower())) {
			CommandConsole.LogError("[MP Debug] use parameter");
			return;
		}

		for (int i = 1; i < args.Length; ++i) {
			if (ulong.TryParse(args[i], out ulong playerId)) {
				// 发送请求检查项目
				var writer = GetWriter(MPSteamworks.UserSteamId, playerId, PacketType.PlayerCheckRequest);
				writer.Put(args[0].ToLower());
				_MPSteamworks.SendToPeer(playerId, writer);
			} else {
				CommandConsole.LogError($"[MP Debug] {args[i]} is not a playerId");
			}
		}
	}

	#endregion
	#region[执行远程命令操作]

	/// <summary>
	/// 玩家远程命令执行核心逻辑
	/// </summary>
	private void ExecutePcmd(string[] args) {
		if (!EnsureHostPrivileges()) return; // 只有房主能用
		if (args.Length < 2) {
			CommandConsole.LogError(Localization.Get("CommandConsole.RemoteCommandInsufficientParams"));
			return;
		}

		string targetStr = args[0].ToLower();

		// 1. 将后续所有参数拼接，并将 "::" 替换为原版的 ";"
		// 例: allowpvp true :: addteam zombie -> allowpvp true ; addteam zombie
		string payload = string.Join(" ", args.Skip(1)).Replace("::", ";");

		// 2. 发送网络包
		var writer = MPWriterPool.GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.RemoteCommand);
		writer.Put(payload);

		if (targetStr == "all") {
			MPSteamworks.Instance.Broadcast(writer);
			CommandConsole.Log(Localization.Get("CommandConsole.RemoteCommandSentToAll", payload));
			Patch_CommandConsole.ExecuteCommandForcefully(payload);
		} else if (ulong.TryParse(targetStr, out ulong targetId)) {
			MPSteamworks.Instance.SendToPeer(targetId, writer);
			CommandConsole.Log(Localization.Get("CommandConsole.RemoteCommandSentToPlayer", targetId, payload));
		} else {
			CommandConsole.LogError(Localization.Get("CommandConsole.RemoteCommandInvalidTarget"));
		}
	}

	/// <summary>
	/// rcon 专属的嵌套补全代理
	/// </summary>
	private void PcmdAutocomplete(CommandConsole.CommandAutocomplete autocomplete) {
		if (!_MPSteamworks.IsHost)
			autocomplete.FromArray(new string[] { "You Are Not Host" });
		// 关照自己的第 0 个参数：玩家列表
		if (autocomplete.activeArg == 0) {
			var targets = _RPManager.Players.Values.Select(p => (id: p.PlayerId.ToString(), name: p.PlayerName)).ToList();
			targets.Insert(0, ("all", "all"));
			autocomplete.FromArrayWithDesc(targets);
			return;
		}

		// 其余的参数,丢给引擎,并明确告诉引擎：如果没有 :: 子命令默认从索引 1 开始
		NestedCommandEngine.ForwardAutocomplete(autocomplete, defaultStartIndex: 1);
	}

	/// <summary>
	/// 队伍远程命令执行核心逻辑
	/// </summary>
	private void ExecuteTcmd(string[] args) {
		if (!EnsureHostPrivileges()) return; // 只有房主能用
		if (args.Length < 2) {
			CommandConsole.LogError(Localization.Get("CommandConsole.TeamRemoteCommandInsufficientParams"));
			return;
		}

		string targetStr = args[0].ToLower();

		// 1. 将后续所有参数拼接，并将 "::" 替换为原版的 ";"
		// 例: allowpvp true :: addteam zombie -> allowpvp true ; addteam zombie
		string payload = string.Join(" ", args.Skip(1)).Replace("::", ";");

		// 2. 发送网络包
		var writer = MPWriterPool.GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.RemoteCommand);
		writer.Put(payload);

		if (TeamRuleManager.activeTeams.TryGetValue(targetStr, out var team)) {
			foreach (var playerId in _RPManager.GetPlayerInTeam(targetStr)) {
				MPSteamworks.Instance.SendToPeer(playerId, writer);
			}
			if (CurrentTeam == team)
				Patch_CommandConsole.ExecuteCommandForcefully(payload);
			CommandConsole.Log(Localization.Get("CommandConsole.TeamRemoteCommandSent", team, payload));
		} else {
			CommandConsole.LogError(Localization.Get("CommandConsole.TeamRemoteCommandInvalidTarget"));
		}
	}

	/// <summary>
	/// tcon 专属的嵌套补全代理
	/// </summary>
	private void TcmdAutocomplete(CommandConsole.CommandAutocomplete autocomplete) {
		if (!_MPSteamworks.IsHost)
			autocomplete.FromArray(new string[] { "You Are Not Host" });
		// 关照自己的第 0 个参数：玩家列表
		if (autocomplete.activeArg == 0) {
			var targets = TeamRuleManager.activeTeams.ToList();
			autocomplete.FromArray(targets);
			return;
		}

		// 其余的参数,丢给引擎,并明确告诉引擎：如果没有 :: 子命令默认从索引 1 开始
		NestedCommandEngine.ForwardAutocomplete(autocomplete, defaultStartIndex: 1);
	}

	/// <summary>
	/// 自动命令执行
	/// </summary>
	private void ExecuteAcmd(string[] args) {
		if (!EnsureHostPrivileges()) return; // 只有房主能用
		if (args.Length < 2) {
			CommandConsole.LogError(Localization.Get("CommandConsole.AutoCommandInsufficientParams"));
			return;
		}

		string action = args[0].ToLower();
		string triggerKey = "";
		int skipCount = 1; // 默认跳过 action 自身

		// --- 动态分支解析 ---
		if (action == "jointeam") {
			if (args.Length < 3) {
				CommandConsole.LogError(Localization.Get("CommandConsole.AutoCommandInvalidFormat"));
				return;
			}
			string teamName = args[1].ToLower();

			// 动态拼接专属队伍的 Lobby Key (例: MPKeys.JOIN_TEAM_COMMAND + "_red")
			triggerKey = MPKeys.JOIN_TEAM_COMMAND + "_" + teamName;
			skipCount = 2; // 跳过 "jointeam" 和 "teamName"
		} else if (action == "join") {
			triggerKey = MPKeys.JOIN_COMMAND;
		} else if (action == "restart") {
			triggerKey = MPKeys.RESTART_COMMAND;
		}

		if (string.IsNullOrEmpty(triggerKey)) {
			CommandConsole.LogError(Localization.Get("CommandConsole.AutoCommandInvalidTrigger"));
			return;
		}

		// 动态裁剪出后续的复合指令 payload
		string payload = string.Join(" ", args.Skip(skipCount)).Replace("::", ";");

		// 写入 Steam LobbyData 广播给所有人
		_MPSteamworks.SetLobbyData(triggerKey, payload);
		CommandConsole.Log(Localization.Get("CommandConsole.AutoCommandRegisterSuccess", triggerKey, payload));
	}

	/// <summary>
	/// acmd 专属的嵌套补全代理 (支持动态多级参数)
	/// </summary>
	private void AcmdAutocomplete(CommandConsole.CommandAutocomplete autocomplete) {
		if (!_MPSteamworks.IsHost) {
			autocomplete.FromArray(new string[] { "You Are Not Host" });
			return;
		}

		// 1. 第一层参数：选择触发时机
		if (autocomplete.activeArg == 0) {
			autocomplete.FromArray(new[] { "join", "restart", "jointeam" });
			return;
		}

		// 获取玩家当前已经输入的第一个参数
		string firstArg = autocomplete.ArgumentAt(0).ToLower();

		// 2. 第二层参数分支判断
		if (firstArg == "jointeam") {
			// 如果正在输入队伍名
			if (autocomplete.activeArg == 1) {
				var teams = TeamRuleManager.activeTeams.ToList();
				autocomplete.FromArray(teams);
				return;
			}

			// 队伍名输完之后的所有后续参数，甩给引擎，明确告诉引擎：子命令从索引 2 开始
			NestedCommandEngine.ForwardAutocomplete(autocomplete, defaultStartIndex: 2);
		} else {
			// 常规的 join 和 restart，子命令从索引 1 开始
			NestedCommandEngine.ForwardAutocomplete(autocomplete, defaultStartIndex: 1);
		}
	}

	/// <summary>
	/// acmd 专属的动态嵌套验证器代理
	/// </summary>
	private void AcmdValidator(CommandConsole.CommandValidator validator) {
		if (Patch_CommandConsole.ValidatorArgsRef == null) return;

		List<string> originalArgs = Patch_CommandConsole.ValidatorArgsRef(validator);
		if (originalArgs == null || originalArgs.Count == 0) return;

		// 动态识别子命令真正的起点
		string firstArg = originalArgs[0].ToLower();
		int dynamicStartIndex = (firstArg == "jointeam") ? 2 : 1;

		// 扔给通用引擎处理
		NestedCommandEngine.ForwardValidator(validator, defaultStartIndex: dynamicStartIndex);
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
		if (IsInLobby && !IsInitialized && !_MPSteamworks.IsHost) {
			StartCoroutine(InitGamemodeRoutine());
		}

		// 设置玩家个人数据
		var joinMsgArray = Localization.GetAll("0_DisplayMessage.JoinMessages");
		var leaveMsgArray = Localization.GetAll("0_DisplayMessage.LeaveMessages");

		int joinLen = joinMsgArray?.Length ?? 0;
		int leaveLen = leaveMsgArray?.Length ?? 0;
		int minLength = Math.Min(joinLen, leaveLen);

		var random = new System.Random();
		int randomInt = minLength > 0 ? random.Next(minLength) : -1;
		// 获取玩家名称
		var playerName = MPConfig.RemotePlayerName == "" ? SteamClient.Name : MPConfig.RemotePlayerName;

		// 保底格式化
		string rawJoinTemplate = (randomInt >= 0 && joinMsgArray != null) ? joinMsgArray[randomInt] : "{0} join";
		string rawLeaveTemplate = (randomInt >= 0 && leaveMsgArray != null) ? leaveMsgArray[randomInt] : "{0} leave";

		string finalJoinMsg = Localization.SafeFormat(rawJoinTemplate, playerName);
		string finalLeaveMsg = Localization.SafeFormat(rawLeaveTemplate, playerName);

		_MPSteamworks.SetMemberData(new Dictionary<string, string>() {
			{ MPKeys.PREFAB_ID, _LocalPlayer != null ? _LocalPlayer.FactoryId : "" },
			{ MPKeys.PLAYER_NAME, playerName },
			{ MPKeys.JOIN_MESSAGE, finalJoinMsg },
			{ MPKeys.LEAVE_MESSAGE, finalLeaveMsg },
			{ MPKeys.TEAM, MPKeys.DEFAULT_TEAM },
		});

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
			var message = Localization.GetRandom("0_DisplayMessage.EnteredMessages",
				lobby.GetData(MPKeys.LOBBY_NAME), lobby.MemberCount, lobby.MaxMembers, lobby.Id.Value);
			SystemMessage(message, UIDisplayType.AscentHeader);
		}

		// 获取游戏模式数据的协程
		IEnumerator InitGamemodeRoutine() {
			if (_MPSteamworks.IsHost || IsInitialized || !IsInLobby) yield break;

			for (int i = 0; i < 3; i++) {
				if (!IsInLobby || IsInitialized) yield break;

				string rawData = lobby.GetData(MPKeys.GAMEMODE_JSON);
				// 空数据尝试重试, 同时请求一次数据刷新
				if (string.IsNullOrEmpty(rawData)) {
					MPMain.LogWarning(Localization.Get("MPCore.GamemodeDataNotSynced", (i + 1).ToString()));
					_MPSteamworks.RefreshLobbyData();
					yield return new WaitForSeconds(1.0f);
					continue; // 提前进入下一次重试
				}

				MPMain.LogInfo(Localization.Get("MPCore.GamemodeDataReceived", rawData));
				// 尝试解析 JSON
				GameModeData data = null;
				try {
					data = JsonConvert.DeserializeObject<GameModeData>(rawData);
				} catch (JsonException ex) {
					MPMain.LogError(Localization.Get("MPCore.GamemodeParseError", rawData, ex.Message));
				}
				// 解析失败, 等待后重试
				if (data == null) {
					yield return new WaitForSeconds(1.0f);
					continue;
				}

				// 解析成功则加载并退出协程
				LoadGameMode(data);
				// 等待地图加载完成
				yield return new WaitUntil(() => WorldLoader.isLoaded == true);
				// 加载完成后执行加入指令
				string cmdData = lobby.GetData(MPKeys.JOIN_COMMAND);
				if (!string.IsNullOrEmpty(cmdData))
					Patch_CommandConsole.ExecuteCommandForcefully(cmdData);
				yield break;
			}
			if (!IsInitialized && !_MPSteamworks.IsHost) {
				MPMain.LogError(Localization.Get("MPCore.HandshakeFailed"));
				Leave(null);
			}
		}
	}

	/// <summary>
	/// 处理大厅成员加入
	/// </summary> 
	private void HandleLobbyMemberJoined(Friend friend) {

	}

	/// <summary>
	/// 处理离开大厅事件
	/// </summary>
	/// <param name="friend"></param>
	private void HandleLobbyMemberLeft(Friend friend) {

	}

	/// <summary>
	/// 处理玩家连接事件
	/// </summary>
	private void HandlePlayerConnected(SteamId steamId) {
		if (_MPSteamworks.IsHost) {
			ItemSyncManager.SendSnapshotToClient(steamId);
		}
	}

	/// <summary>
	/// 处理玩家断连事件
	/// </summary>
	private void HandlePlayerDisconnected(SteamId steamId) {
		// Debug
		MPMain.LogInfo(Localization.Get("MPCore.PlayerDisconnected", steamId.ToString()));
		_RPManager.ProcessPlayerLeave(steamId);
	}

	/// <summary>
	/// 处理事件总线 大厅邀请OnLobbyInvite
	/// </summary>
	private void HandleLobbyInvite(Friend friend, Lobby lobby) {
		var message = Localization.GetRandom("0_DisplayMessage.InviteReceivedMessages", friend.Name, lobby.GetData(MPKeys.LOBBY_NAME));
		SystemMessage(message, UIDisplayType.AscentHeader);
	}

	/// <summary>
	/// 处理事件总线 大厅数据(规则)改变OnLobbyDataChange<br/>
	/// 调用者: <see cref="MPSteamworks.HandleLobbyDataChanged"/><br/>
	/// 调用者: <see cref="MPSteamworks.RefreshLobbyData"/><br/>
	/// </summary>
	private void HandleLobbyDataChanged(Dictionary<string, string> changedData) {

		MPMain.LogInfo(Localization.Get("MPCore.LobbyDataChanged", string.Join(", ", changedData.Select(kvp => $"{kvp.Key}={kvp.Value}"))));

		if (changedData == null) return;

		// 处理是否允许作弊的特殊键, 直接影响游戏机制
		if (changedData.TryGetValue(MPKeys.ALLOW_CHEATS, out var cheatsValue)) {
			IsAllowCheats = bool.TryParse(cheatsValue, out var parsed) && parsed;
			// 明确要求关闭作弊
			if (!IsAllowCheats) {
				CommandConsole.cheatsEnabled = false;
				ENT_Player.GetPlayer().noclip = false;
				ENT_Player.GetPlayer().SetGodMode(false);
			}
		}

		// 处理活跃队伍列表的特殊键, 直接影响规则设置界面和规则应用逻辑
		if (changedData.TryGetValue(MPKeys.ACTIVE_TEAMS, out var activeTeamsValue)) {
			var teams = activeTeamsValue.Split(',').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s));
			TeamRuleManager.UpdateActiveTeams(teams);
		}

		// 规则相关的键以 "Rule_" 开头,例如 "Rule_{TeamA}_{TeamB}"为两个队伍之间的规则的键
		// 值为 "pvp:1,grab:0,hang:1" 等
		var ruleChange = false;
		foreach (var kvp in changedData) {
			var teamNames = kvp.Key.Split('_');
			if (teamNames.Length == 3 && teamNames[0] == "Rule") {
				// 更新规则缓存
				TeamRuleManager.UpdateRuleCache(kvp.Key, kvp.Value);
				if (teamNames[1] == CurrentTeam || teamNames[1] == MPKeys.DEFAULT_TEAM) ruleChange = true;
			}
		}

		// 如果规则对当前玩家有影响, 则更新当前玩家的实际规则
		if (ruleChange) {
			TeamRuleManager.UpdateActiveRules(CurrentTeam);
			_RPManager.RefreshAllRule();
		}

		// 伤害规则
		if (changedData.TryGetValue(MPKeys.DAMAGE_CONFIG, out var damageValue)) {
			damageRules = JsonConvert.DeserializeObject<DamageRules>(damageValue) ?? new DamageRules();
		}
	}

	/// <summary>
	/// 处理事件总线 玩家数据改变OnMemberDataChanged<br/>
	/// </summary>
	private void HandleMemberDataChanged(Friend steamId, Dictionary<string, string> data) {
		if (steamId.Id == MPSteamworks.UserSteamId) return;
		MPMain.LogInfo(Localization.Get("MPCore.SteamIdDataDebug",
			steamId.Id, string.Join(", ", data.Select(kvp => $"{kvp.Key}={kvp.Value}"))));
		_RPManager.ProcessMemberData(steamId.Id, data);
	}

	#endregion
	#region[联机状态函数]
	public static void SetStatus(MPStatus mask, MPStatus value) {
		MultiPlayerStatus.SetField(mask, value);
	}

	#endregion
	#region[工具函数]

	/// <summary>
	/// 获取物品清单字典
	/// </summary>
	public static Dictionary<string, byte> GetInventoryItems() {
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
	public static void SystemMessage(string message, UIDisplayType displayType, float duration = 5.0f) {
		UI_Manager.DisplayMessage(message, displayType, duration);
		CommandConsole.Log($"[SYSTEM] {message}");
	}

	/// <summary>
	/// 将内容复制到系统剪贴板
	/// </summary>
	private static void CopyToClipboard(string text) {
		GUIUtility.systemCopyBuffer = text;
	}

	/// <summary>
	/// 确保在大厅中使用指令
	/// </summary>
	private bool EnsureInLobby() {
		if (!_MPSteamworks.IsInLobby) {
			CommandConsole.LogError(Localization.Get("CommandConsole.NeedToBeInLobby"));
			return false;
		}
		return true;
	}

	/// <summary>
	/// 确保使用指令的是主机
	/// </summary>
	private bool EnsureHostPrivileges() {
		if (!_MPSteamworks.IsInLobby) {
			CommandConsole.LogError(Localization.Get("CommandConsole.NeedToBeInLobby"));
			return false;
		}
		if (!_MPSteamworks.IsHost) {
			CommandConsole.LogError(Localization.Get("CommandConsole.NeedToBeHost"));
			return false;
		}
		return true;
	}

	#endregion
}