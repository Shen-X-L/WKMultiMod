using Newtonsoft.Json;
using Steamworks.Data;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
using WKMPMod.Data;
using WKMPMod.NetWork;
using WKMPMod.Patch;
using WKMPMod.Util;
using static CL_AssetManager;

namespace WKMPMod.Core;

public class MPGameModeManager {
	public const string CHIMNEY_GAME_MODE = "Chimney";

	[Serializable]
	public record class GameModeData : INetworkSerializable {
		public string gameModeName;         // 可能重名
		public string gameModeObjectName;   // 可能重名
		public List<string> activeTrinkets; // 包含饰品和绑定的名称列表
		public List<string> activeSettings; // 包含铁人, 困难等设置的名称列表
		public bool needBindSync;           // 是否需要同步绑定数据(如果饰品名称不唯一或包含绑定数据则需要同步绑定数据)
		public int? seed;

		public void Serialize(DataWriter writer) {
			writer.Put(gameModeName);
			writer.Put(gameModeObjectName);
			writer.Put(activeTrinkets);
			writer.Put(activeSettings);
			writer.Put(needBindSync);
			writer.Put(seed);
		}

		public void Deserialize(DataReader reader) {
			this.gameModeName = reader.GetString();
			this.gameModeObjectName = reader.GetString();
			this.activeTrinkets = reader.GetStringList();
			this.activeSettings = reader.GetStringList();
			this.needBindSync = reader.GetBool();
			this.seed = reader.GetNullableInt();
		}
	}

	public static Dictionary<string, M_Gamemode> gameModeDict = new Dictionary<string, M_Gamemode>();
	public static GameModeData? CurrentData { get; private set; }

	/// <summary>
	/// 获取全部游戏模式
	/// </summary>
	public static void Initialize() {
		FieldInfo field = typeof(CL_AssetManager).GetField(
			"activeDatabases", BindingFlags.NonPublic | BindingFlags.Static);
		if (field == null) {
			MPMain.LogError(Localization.Get("MPGameModeManager.FieldNotFound"));
			return;
		}
		var dict = (Dictionary<string, WKDatabaseHolder>)field.GetValue(null);

		foreach (var (key, value) in dict) {
			foreach (var gamemode in value.database.gamemodeAssets) {
				gameModeDict[gamemode.name] = gamemode;
				gameModeDict[gamemode.gamemodeName] = gamemode;
			}
		}
	}

	/// <summary>
	/// 当玩家退出大厅或返回主菜单时调用, 清除同步数据
	/// </summary>
	public static void ClearCurrentData() {
		CurrentData = null;
	}

	/// <summary>
	/// 获取当前游戏模式数据
	/// </summary>
	public static GameModeData CaptureCurrentModeData() {
		CurrentData = new GameModeData {
			gameModeName = CL_GameManager.gamemode.name,
			gameModeObjectName = CL_GameManager.gamemode.ToString(),
			activeTrinkets = StatManager.saveData.GetGameMode(CL_GameManager.GetGamemodeName()).activeTrinkets,
			activeSettings = StatManager.saveData.GetGameMode(CL_GameManager.GetBaseGamemode().gamemodeName).activeSettings,
			needBindSync = MPConfig.BindSync,
			seed = WorldLoader.instance != null ? WorldLoader.instance.seed : (int?)null
		};
		return CurrentData;
	}

	public static void LoadGameModeIfChanged(GameModeData data) {
		if (CurrentData != null && CurrentData == data)
			return;
		LoadGameMode(data);
	}

	/// <summary>
	/// 通过GameModeData加载游戏模式, 包括游戏模式, 难度和种子
	/// </summary>
	public static void LoadGameMode(GameModeData data) {
		// 字典内没有数据时重初始化字典
		if (gameModeDict.Count == 0) Initialize();
		// 保存当前数据
		CurrentData = data;
		// 更改游戏模式
		if (!gameModeDict.TryGetValue(data.gameModeName, out var m_Gamemode)) {
			MPMain.LogError(Localization.Get("MPGameModeManager.GameModeNotFound", data.gameModeName));
			return;
		}
		// 设置游戏模式
		CL_GameManager.gMan.SetGamemode(m_Gamemode);
		// 更改难度设置
		StatManager.saveData.GetGameMode(m_Gamemode.gamemodeName)?.activeSettings = data.activeSettings;
		// 饰品和绑定数据默认不同步, 只有设置了需要同步绑定数据才同步
		if (data.needBindSync) {
			StatManager.saveData.SetGamemodeTrinkets(CL_GameManager.GetGamemodeName(), data.activeTrinkets);
			MPCore.NeedResetGamemodeName = CL_GameManager.GetGamemodeName();
			MPCore.NeedResetTrinkets = true;
		}
		// 一直设置种子, 相同种子而跳过设置种子会导致种子重随机
		if (data.seed is int value) {
			WorldLoader.SetPresetSeed(value.ToString());
		}
		// 设置为已经初始化地图
		MPCore.SetStatus(MPStatus.INIT_MASK, MPStatus.Initialized);
		// 手动重载地图
		SceneManager.LoadScene(m_Gamemode.gamemodeScene);
	}

	/// <summary>
	/// 重加载游戏模式
	/// </summary>
	public static void RestartGameMode() {
		if (CurrentData != null) {
			LoadGameMode(CurrentData);
			MPCore.Instance.StartCoroutine(ExecuteRestartCMD());
		} else {
			MPMain.LogWarning(Localization.Get("MPGameModeManager.NoCurrentGameModeData"));
		}

		IEnumerator ExecuteRestartCMD() {
			// 等待地图加载完成 || WorldLoader.instance 为空(游乐场)
			yield return new WaitUntil(() => WorldLoader.isLoaded || WorldLoader.instance == null);

			// 加载完成后执行加入指令
			if (MPSteamworks.Instance.LobbyData.TryGetValue(MPKeys.RESTART_COMMAND, out var cmdData) && !string.IsNullOrEmpty(cmdData)) {
				Patch_CommandConsole.ExecuteCommandForcefully(cmdData);
			}
			yield break;
		}
	}

	/// <summary>
	/// 尝试获取游戏模式
	/// </summary>
	public static bool TryGetGameMode(string name, out M_Gamemode gamemode) {
		if (gameModeDict.Count == 0)
			Initialize();
		return gameModeDict.TryGetValue(name, out gamemode);
	}
}
