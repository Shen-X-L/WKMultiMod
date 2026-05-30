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
	private MPSteamworks _MPsteamworks;
	private RPManager _RPManager;
	private LocalPlayer _LocalPlayer;
	private MPAssetManager _MPAssetManager;
	private UI_Manager _UIManager;

	// 多人模式状态
	public static MPStatus MultiPlayerStatus = MPStatus.NotInitialized;

	// 多人模式大厅规则
	public static bool IsAllowPVP { get; private set; }
	public static bool IsAllowCheats { get; private set; }

	// PVP伤害倍率
	public static DamageRules damageRules { get; private set; }
	// 专注模式缓存
	private bool focusMode = false;

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

		if (!IsInitialized || !IsInLobby) return;

		// 定期检查玩家数量和连接状态,修复异常状态
		CheckAndRepairPlayers();

		//// 检测是否启用不攀爬模式并修改ObjectTagger
		//if (focusMode != Patch_ENT_Player.IsFocusModeActive()) {
		//	focusMode = !focusMode;
		//	// 是专注模式 添加Pickupable标签
		//	if (focusMode) {
		//		_RPManager.AddAllObjectTagger("Pickupable");
		//	} else {
		//		_RPManager.RemoveAllObjectTagger("Pickupable");
		//	}
		//}
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
			IsAllowPVP = MPConfig.AllowPVP;

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
	}

	#endregion
	#region[玩家数量同步]
	private void CheckAndRepairPlayers() {
		if (!_syncTick.TryTick()) return;
		// 在大厅但没有连接
		foreach (var member in _MPsteamworks.Members) {
			if (member.Id == MPSteamworks.UserSteamId) continue;
			if (!_MPsteamworks._allConnections.ContainsKey(member.Id)) {
				_MPsteamworks.ConnectionController(member.Id, true);
			}
		}
		// 有连接但没有创建对象
		foreach (var (steamId, connection) in _MPsteamworks._allConnections) {
			if (!_RPManager.Players.ContainsKey(steamId)) {
				MPMain.LogWarning(Localization.Get("MPCore.PlayerDataMissing", steamId));
				// 从MemberData获取模型数据
				var data = _MPsteamworks.GetAllMemberData(new Friend(steamId));
				_RPManager.ProcessMemberData(steamId, data);
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
		switch (scene.name) {
			case "Game-Main": {
				// 注册命令和初始化世界数据
				ChangeRPFactoryId();
				// 如果是主游戏场景且是房主,抓取当前模式数据并广播给其他人
				if (_MPsteamworks.IsHost) {
					// 设置当前游戏模式数据
					var currentModeData = MPGameModeManager.CaptureCurrentModeData();
					if (string.IsNullOrWhiteSpace(_MPsteamworks.LobbyData?.GetValueOrDefault(MPKeys.GAMEMODE_JSON))) {
						_MPsteamworks.SetLobbyData(MPKeys.GAMEMODE_JSON, JsonConvert.SerializeObject(currentModeData));
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
				if (_MPsteamworks.IsHost) {
					// 设置当前游戏模式数据
					var currentModeData = MPGameModeManager.CaptureCurrentModeData();
					if (string.IsNullOrWhiteSpace(_MPsteamworks.LobbyData?.GetValueOrDefault(MPKeys.GAMEMODE_JSON))) {
						_MPsteamworks.SetLobbyData(MPKeys.GAMEMODE_JSON, JsonConvert.SerializeObject(currentModeData));
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
		_MPsteamworks.DisconnectAll();
		_RPManager.ResetAll();
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
	private void HandlePlayerDamage(ulong steamId, Damageable.DamageInfo info) {
		var writer = GetWriter(MPSteamworks.UserSteamId, steamId, PacketType.PlayerDamage);
		writer.Put(info.amount);
		writer.Put(info.type);
		writer.Put(info.tags);

		_MPsteamworks.SendToPeer(steamId, writer);
	}

	/// <summary>
	/// 发送给予其他玩家冲击力数据<br/>
	/// 接受路由函数: <see cref="MPPacketHandlers.HandlePlayerAddForce"/><br/>
	/// </summary>
	private void HandlePlayerAddForce(ulong steamId, Vector3 force, string source) {
		var writer = GetWriter(MPSteamworks.UserSteamId, steamId, PacketType.PlayerAddForce);
		writer.Put(force.x);
		writer.Put(force.y);
		writer.Put(force.z);
		writer.Put(source);
		_MPsteamworks.SendToPeer(steamId, writer);
	}

	/// <summary>
	/// 发送玩家死亡信息<br/>
	/// 发送函数: <see cref="Patch_ENT_Player.Prefix"/><br/>
	/// 发送PacketType.PlayerDeath: 库存物品 Dictionary&lt;string, short&gt;<br/>
	/// 接受路由函数: <see cref="MPPacketHandlers.HandlePlayerDeath"/><br/>
	/// 发送PacketType.GameUIMessage: 死因 string,UI类型 byte,持续时间 float,是否在控制台显示 bool<br/>
	/// 接受路由函数: <see cref="MPPacketHandlers.HandleSystemUIMessage"/><br/>
	/// </summary>
	private void HandlePlayerDeath(string type) {
		var writerDeath = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.PlayerDeath);

		// 库存物品字典
		writerDeath.Put(GetGetInventoryItems());

		// 发送背包道具
		_MPsteamworks.Broadcast(writerDeath);

		// 死亡信息获取
		var name = new Friend(MPSteamworks.UserSteamId).Name;
		var message = Localization.HasKey("0_DeathMessage", type)
			? Localization.GetRandomSplit("0_DeathMessage", type, name)
			: Localization.GetRandom("0_DeathMessage.default", type, name);

		var writerMessage = BuildingMessage(message, UIDisplayType.HighscoreHeader, logToConsole: true);
		if (writerMessage != null)
			// 发送死亡信息
			_MPsteamworks.Broadcast(writerMessage);
	}

	/// <summary>
	/// 发送玩家胜利信息<br/>
	/// </summary>
	private void HandlePlayerWin() {
		var writerMessage = BuildingMessage(Localization.GetRandom("0_DisplayMessage.WinMessages"), UIDisplayType.TipHeader, logToConsole: true);
		if (writerMessage != null)
			// 发送胜利信息
			_MPsteamworks.Broadcast(writerMessage);
	}
	#endregion
	#region[命令注册]

	/// <summary>
	/// 命令注册
	/// </summary>
	public void RegisterCommands() {
		// 将命令注册到 CommandConsole
		// 创建大厅
		CommandConsole.BuildCommand("host", Host)
			.NotCheat()
			.Description(Localization.Get("CommandHelp.Host"))
			// 参数补全
			.AutocompleteCustom(autocomplete => {
				// activeArg 表示当前正在输入的参数位置
				switch (autocomplete.activeArg) {
					case 1: // 第二参数: Visibility
						autocomplete.FromArray(new[] { "public", "friends", "private" });
						break;
					case 2: // 第三参数: Max Player
						autocomplete.FromArray(new[] { "2", "4", "8", "16" });
						break;
				}
			})
			// 参数校验
			.AutocompleteValidator(validator => {
				if (validator.activeArg == 1) {
					string vis = validator.ArgumentAt(1).ToLower();
					if (vis != "public" && vis != "friends" && vis != "private") {
						validator.Reject(); // 不匹配则高亮红色
					}
				}
				if (validator.activeArg == 2) {
					if (!int.TryParse(validator.ArgumentAt(2), out _)) {
						validator.Reject();
					}
				}
			});

		// 加入大厅
		CommandConsole.BuildCommand("join", Join)
			.NotCheat()
			.Description(Localization.Get("CommandHelp.Join"))
			.AutocompleteCustom(autocomplete => {
				// activeArg 表示当前正在输入的参数位置
				_ = _MPsteamworks.RefreshLobbyListAsync();
				switch (autocomplete.activeArg) {
					case 0:
						autocomplete.FromArrayWithDesc(
							_MPsteamworks.LastFetchedLobbies
								.Select(lobby => (
									desc: lobby.Id.ToString(),
									name: lobby.GetData(MPKeys.LOBBY_NAME) ?? "Unnamed Lobby"))
								.ToList());
						break;
				}
			});

		// 离开大厅
		CommandConsole.BuildCommand("leave", Leave)
			.NotCheat()
			.Description(Localization.Get("CommandHelp.Leave"))
			// 显示默认值
			.OverValue(() => _MPsteamworks.IsInLobby ? "In Lobby" : "Not In Lobby")
			// 不在大厅则变红
			.AutocompleteValidator(validator => { if (!_MPsteamworks.IsInLobby) validator.Reject(); });

		// 获取大厅ID
		CommandConsole.BuildCommand("lobbyid", (args) => {
			if (!EnsureInLobby()) return;
			string lobby_id = _MPsteamworks.LobbyId.ToString();
			CopyToClipboard(lobby_id);
			CommandConsole.Log(Localization.Get(
				"CommandConsole.LobbyIdOutput", lobby_id));

		})
			.NotCheat()
			.Description(Localization.Get("CommandHelp.LobbyId"))
			.OverValue(() => _MPsteamworks.IsInLobby ? _MPsteamworks.LobbyId : "Not In Lobby")
			.AutocompleteValidator(validator => { if (!_MPsteamworks.IsInLobby) validator.Reject(); });

		// 获取大厅全部玩家
		CommandConsole.BuildCommand("allplayer", (args) => {
			foreach (var friend in _MPsteamworks.Members) {
				Vector3 position = friend.Id == MPSteamworks.UserSteamId ? Vector3.zero : _RPManager.GetPlayerObject(friend.Id)?.transform.position ?? Vector3.zero;
				float distance = position == Vector3.zero ? 0 : Vector3.Distance(LocalPlayer.Instance.transform.position, position);
				CommandConsole.Log(Localization.Get(
					"CommandConsole.AllPlayer", friend.Name, friend.Id, distance, position));
			}
		})
			.NotCheat()
			.Description(Localization.Get("CommandHelp.AllPlayer"))
			.OverValue(() => _MPsteamworks.IsInLobby
				? $"Player: {_MPsteamworks.Members.Count()}/{_MPsteamworks.LobbySize}"
				: "Not In Lobby")
			.AutocompleteValidator(validator => { if (!_MPsteamworks.IsInLobby) validator.Reject(); });

		// 向大厅广播
		CommandConsole.BuildCommand("talk", Talk)
			.NotCheat()
			.Description(Localization.Get("CommandHelp.Talk"));

		// tp到某人(同步背包物品)
		CommandConsole.BuildCommand("tpto", TpToPlayer)
			.Description(Localization.Get("CommandHelp.TpTo"))
			.AutocompleteCustom(autocomplete => {
				if (autocomplete.activeArg == 0) {
					autocomplete.FromArrayWithDesc(
						_RPManager.Players.Values
							.Select(container => (
								id: container.PlayerId.ToString(),
								name: container.PlayerName)).ToList());
				}
			});

		// 修改玩家模型(局内不生效)
		CommandConsole.BuildCommand("changemodel", (args) => {
			_LocalPlayer.DefaulFactoryId = args[0];
			MPConfig.RemotePlayerModel = args[0];
			_MPsteamworks.SetMemberData(MPKeys.PREFAB_ID, args[0]);
		})
			.NotCheat()
			.Description(Localization.Get("CommandHelp.ChangeModel"))
			.AutocompleteCustom(autocomplete => {
				if (autocomplete.activeArg == 0) {
					autocomplete.FromArray(RPFactoryManager.ModelIDs);
				}
			});

		// 获取全部大厅
		CommandConsole.BuildCommand("lobbylist", GetAllLobby)
			.NotCheat()
			.Description(Localization.Get("CommandHelp.LobbyList"));

		// 邀请其他好友
		CommandConsole.BuildCommand("invite", (args) => {
			if (!EnsureInLobby()) return;
			ulong lobby_id = _MPsteamworks.LobbyId;
			SteamFriends.OpenGameInviteOverlay(lobby_id);

		})
			.NotCheat()
			.Description(Localization.Get("CommandHelp.Invite"))
			.OverValue(() => _MPsteamworks.IsInLobby ? _MPsteamworks.LobbyId : "Not In Lobby");

		// 设置大厅可见度
		CommandConsole.BuildCommand("lobbytype", SetLobbyVisibility)
			.NotCheat()
			.Description(Localization.Get("CommandHelp.LobbyYype"))
			.OverValue(() => _MPsteamworks.IsInLobby
				? (_MPsteamworks.LobbyData?.GetValueOrDefault(MPKeys.LOBBY_VISIBILITY) ?? "unknown value")
				: "Not In Lobby")
			.AutocompleteCustom(autocomplete => {
				if (autocomplete.activeArg == 0)
					autocomplete.FromArray(new[] { "public", "friends", "private" });
			})
			.AutocompleteValidator(validator => {
				if (validator.activeArg == 1) {
					string vis = validator.ArgumentAt(1).ToLower();
					if (vis != "public" && vis != "friends" && vis != "private")
						validator.Reject(); // 不匹配则高亮红色
				}
			});

		// 设置大厅名称
		CommandConsole.BuildCommand("setlobbyname", (args) => {
			if (!EnsureHostPrivileges()) return;
			_MPsteamworks.SetLobbyData(MPKeys.LOBBY_NAME, string.Join(" ", args));
		}).NotCheat()
			.Description(Localization.Get("CommandHelp.SetLobbyName"));

		// 设置是否可开启作弊模式
		CommandConsole.BuildCommand("allowcheats", (args) => {
			if (!EnsureHostPrivileges()) return;
			bool enabled = false;
			if (args.Length == 0 && bool.TryParse(_MPsteamworks.LobbyData?.GetValueOrDefault(MPKeys.ALLOW_CHEATS), out bool result1)) {
				// 如果没有参数 获取大厅数据并取反 || 取否
				enabled = !result1;
			} else if (bool.TryParse(args[0], out bool result2)) {
				// 有参数直接使用参数
				enabled = result2;
			}
			MPConfig.AllowCheats = enabled;
			IsAllowCheats = enabled;
			_MPsteamworks.SetLobbyData(MPKeys.ALLOW_CHEATS, enabled.ToString());
		}).NotCheat()
			.Description(Localization.Get("CommandHelp.AllowCheats"))
			.OverValue(() => _MPsteamworks.IsInLobby
				? (_MPsteamworks.LobbyData?.GetValueOrDefault(MPKeys.ALLOW_CHEATS) ?? "unknown value")
				: "Not In Lobby")
			.AutocompleteCustom(autocomplete => {
				if (autocomplete.activeArg == 0 && _MPsteamworks.IsHost)
					autocomplete.FromArray(new[] { "True", "False" });
				if (autocomplete.activeArg == 0 && !_MPsteamworks.IsHost)
					autocomplete.FromArray(new[] { "You Are Not Host" });
			}).AutocompleteValidator(validator => {
				if (validator.activeArg == 1) {
					string vis = validator.ArgumentAt(1).ToLower();
					if (vis != "True" && vis != "False")
						validator.Reject(); // 不匹配则高亮红色
				}
			});

		// 设置是否可PVP
		CommandConsole.BuildCommand("allowpvp", (args) => {
			if (!EnsureHostPrivileges()) return;
			bool enabled = false;
			if (args.Length == 0 && bool.TryParse(_MPsteamworks.LobbyData?.GetValueOrDefault(MPKeys.ALLOW_PVP), out bool result1)) {
				// 如果没有参数 获取大厅数据并取反 || 取否
				enabled = !result1;
			} else if (bool.TryParse(args[0], out bool result2)) {
				// 有参数直接使用参数
				enabled = result2;
			}
			MPConfig.AllowPVP = enabled;
			IsAllowPVP = enabled;
			_MPsteamworks.SetLobbyData(MPKeys.ALLOW_PVP, enabled.ToString());
		}).NotCheat()
			.Description(Localization.Get("CommandHelp.AllowPVP"))
			.OverValue(() => _MPsteamworks.IsInLobby
				? (_MPsteamworks.LobbyData?.GetValueOrDefault(MPKeys.ALLOW_PVP) ?? "unknown value")
				: "Not In Lobby")
			.AutocompleteCustom(autocomplete => {
				if (autocomplete.activeArg == 0 && _MPsteamworks.IsHost)
					autocomplete.FromArray(new[] { "True", "False" });
				if (autocomplete.activeArg == 0 && !_MPsteamworks.IsHost)
					autocomplete.FromArray(new[] { "You Are Not Host" });
			}).AutocompleteValidator(validator => {
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
			if (args.Length == 0 && bool.TryParse(_MPsteamworks.LobbyData?.GetValueOrDefault(MPKeys.BIND_SYNC), out bool result1)) {
				// 如果没有参数 获取大厅数据并取反 || 取否
				enabled = !result1;
			} else if (bool.TryParse(args[0], out bool result2)) {
				// 有参数直接使用参数
				enabled = result2;
			}
			MPConfig.BindSync = enabled;
		}).NotCheat()
			.Description(Localization.Get("CommandHelp.BindSync"))
			.OverValue(() => _MPsteamworks.IsInLobby
				? (MPConfig.BindSync.ToString())
				: "Not In Lobby")
			.AutocompleteCustom(autocomplete => {
				if (autocomplete.activeArg == 0 && _MPsteamworks.IsHost)
					autocomplete.FromArray(new[] { "True", "False" });
				if (autocomplete.activeArg == 0 && !_MPsteamworks.IsHost)
					autocomplete.FromArray(new[] { "You Are Not Host" });
			}).AutocompleteValidator(validator => {
				if (validator.activeArg == 1) {
					string vis = validator.ArgumentAt(1).ToLower();
					if (vis != "True" && vis != "False")
						validator.Reject(); // 不匹配则高亮红色
				}
			});

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
			bool success = await _MPsteamworks.CreateRoomAsync(maxPlayers, lobbyData);

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
						if (string.IsNullOrWhiteSpace(_MPsteamworks.LobbyData?.GetValueOrDefault(MPKeys.GAMEMODE_JSON)))
							_MPsteamworks.SetLobbyData(MPKeys.GAMEMODE_JSON, JsonConvert.SerializeObject(currentModeData));
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
			MPMain.LogError(Localization.Get("CommandConsole.CriticalErrorDuringCreate", ex.Message));
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

		string input = string.Join(" ", args);
		Lobby? targetLobby = null;

		CommandConsole.Log(Localization.Get("CommandConsole.SearchingLobbyByName", input));

		try {
			// 进行异步查询
			var LobbyList = await _MPsteamworks.RefreshLobbyListAsync();

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
						MPMain.LogWarning($"[MP Debug] 无法将 {gamemode} 转为JSON数据 错误信息: {ex.Message}");
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
			// 直接 await 异步结果, 代码逻辑变为线性
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
	/// 设置大厅可见性 参数: public/friends/private
	/// </summary>
	public void SetLobbyVisibility(string[] args) {

		if (!EnsureHostPrivileges()) return;

		bool success = args[0].ToLower() switch {
			"public" => _MPsteamworks._currentLobby.SetPublic(),
			"friends" => _MPsteamworks._currentLobby.SetFriendsOnly(),
			"private" => _MPsteamworks._currentLobby.SetPrivate(),
			_ => false
		};
		if (success) {
			_MPsteamworks._currentLobby.SetData(MPKeys.LOBBY_VISIBILITY, args[0].ToLower());
			CommandConsole.Log(Localization.Get("CommandConsole.LobbyVisibilitySet", args[0]));
		} else {
			CommandConsole.LogError(Localization.Get("CommandConsole.LobbyVisibilitySetFailed"));
		}


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
		_MPsteamworks.Broadcast(writer);
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
			_MPsteamworks.SendToPeer(ids[0], writer);
		}
	}

	/// <summary>
	/// 通过指令获取全部大厅信息,包含Id/名称/房主/游戏模式等
	/// </summary>
	public async void GetAllLobby(string[] args) {
		await _MPsteamworks.RefreshLobbyListAsync();
		foreach (var lobby in _MPsteamworks.LastFetchedLobbies) {
			var gamemode = lobby.GetData(MPKeys.GAMEMODE_JSON);
			try {
				GameModeData gameModeData = JsonConvert.DeserializeObject<GameModeData>(gamemode);
				if (gameModeData != null) gamemode = gameModeData.gameModeName;
			} catch (Exception ex) {
				MPMain.LogWarning($"[MP Debug] 无法将 {gamemode} 转为JSON数据");
			}

			CommandConsole.Log(Localization.Get(
				"CommandConsole.LobbyInfo", lobby.Id, lobby.GetData(MPKeys.LOBBY_NAME),
				lobby.GetData(MPKeys.OWNER_NAME), gamemode));
		}
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
		if (IsInLobby && !IsInitialized && !_MPsteamworks.IsHost) {
			StartCoroutine(InitGamemodeRoutine());
		}

		// 设置玩家个人数据
		var joinMsgArray = Localization.GetAll("0_DisplayMessage.JoinMessages");
		var leaveMsgArray = Localization.GetAll("0_DisplayMessage.LeaveMessages");
		var random = new System.Random();
		var randomInt = random.Next(Math.Min(joinMsgArray.Length, leaveMsgArray.Length));
		_MPsteamworks.SetMemberData(MPKeys.PREFAB_ID, _LocalPlayer.FactoryId);
		_MPsteamworks.SetMemberData(MPKeys.JOIN_MESSAGE, string.Format(joinMsgArray[randomInt], SteamClient.Name));
		_MPsteamworks.SetMemberData(MPKeys.LEAVE_MESSAGE, string.Format(leaveMsgArray[randomInt], SteamClient.Name));

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
			for (int i = 0; i < 3 && IsInLobby && !IsInitialized && !_MPsteamworks.IsHost; i++) {
				string rawData = lobby.GetData(MPKeys.GAMEMODE_JSON);
				if (string.IsNullOrEmpty(rawData)) {
					// 尝试获取原始字符串
					MPMain.LogWarning($"[MP Debug] (尝试 {i + 1}) 大厅的游戏模式数据尚未同步或丢失");
					_MPsteamworks.RefreshLobbyData();
				} else {
					MPMain.LogInfo($"[MP Debug] 游戏模式数据: {rawData}");
					// 尝试解析 JSON
					GameModeData data = null;
					try {
						data = JsonConvert.DeserializeObject<GameModeData>(rawData);

					} catch (JsonException ex) {
						MPMain.LogError($"[MP Debug] (尝试 {i + 1}) JSON 格式解析失败,原始数据: {rawData} | 错误: {ex.Message}");
					}
					// 解析成功则加载并退出协程
					if (data != null) {
						LoadGameMode(data);
						yield break;
					}
				}
				yield return new WaitForSeconds(1.0f);
			}
			if (!IsInitialized && !_MPsteamworks.IsHost) {
				MPMain.LogError("[MP Debug] 初始化握手失败: 重试次数耗尽或已离开大厅.");
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
		if (_MPsteamworks.IsHost) {
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
	private void HandleLobbyDataChanged(Dictionary<string, string> delta) {

		MPMain.LogInfo(Localization.Get("MPCore.LobbyDataChanged", string.Join(", ", delta.Select(kvp => $"{kvp.Key}={kvp.Value}"))));

		if (delta == null) return;

		if (delta.TryGetValue(MPKeys.ALLOW_CHEATS, out var cheatsValue)) {
			IsAllowCheats = bool.TryParse(cheatsValue, out var parsed) && parsed;
			// 明确要求关闭作弊
			if (!IsAllowCheats) {
				CommandConsole.cheatsEnabled = false;
				ENT_Player.GetPlayer()?.noclip = false;
			}
		}

		if (delta.TryGetValue(MPKeys.ALLOW_PVP, out var pvpValue)) {
			IsAllowPVP = bool.TryParse(pvpValue, out var parsed) && parsed;
		}

		if (delta.TryGetValue(MPKeys.DAMAGE_CONFIG, out var damageValue)) {
			damageRules = JsonConvert.DeserializeObject<DamageRules>(damageValue) ?? new DamageRules();

			// 前向兼容
			if (damageRules.FireTime == 0) {
				damageRules.FireTime = MPConfig.FireTimeMult;
			}
			if (damageRules.FireDamage == 0) {
				damageRules.FireDamage = MPConfig.FireDamageMult;
			}
			ENT_Player.GetPlayer()?.fireTimeMult = damageRules.FireTime;
			ENT_Player.GetPlayer()?.fireDamageMult = damageRules.FireDamage;
		}
	}

	/// <summary>
	/// 处理事件总线 玩家数据改变OnMemberDataChanged<br/>
	/// </summary>
	private void HandleMemberDataChanged(Friend steamId, Dictionary<string, string> data) {
		if (steamId.Id == MPSteamworks.UserSteamId) return;
		MPMain.Debug($"steamId: {steamId.Id} data: {string.Join(", ", data.Select(kvp => $"{kvp.Key}={kvp.Value}"))}");
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
		if (!_MPsteamworks.IsInLobby) {
			CommandConsole.LogError(Localization.Get("CommandConsole.NeedToBeInLobby"));
			return false;
		}
		return true;
	}

	/// <summary>
	/// 确保使用指令的是主机
	/// </summary>
	private bool EnsureHostPrivileges() {
		if (!_MPsteamworks.IsInLobby) {
			CommandConsole.LogError(Localization.Get("CommandConsole.NeedToBeInLobby"));
			return false;
		}
		if (!_MPsteamworks.IsHost) {
			CommandConsole.LogError(Localization.Get("CommandConsole.NeedToBeHost"));
			return false;
		}
		return true;
	}
	#endregion
}