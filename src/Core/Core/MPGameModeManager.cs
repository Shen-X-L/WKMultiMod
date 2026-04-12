using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using WKMPMod.Util;

namespace WKMPMod.Core;

public class MPGameModeManager {
	public struct GameModeData {
		public bool isIron;
		public bool isHard;
		public string gameModeName;			// 可能重名
		public string gameModeObjectName;	// 可能重名
		public int? seed;
	}

	public static Dictionary<string,M_Gamemode> gameModeDict = new Dictionary<string,M_Gamemode>();
	public static GameModeData? CurrentData { get; private set; }

	/// <summary>
	/// 获取全部游戏模式
	/// </summary>
	public static void Initialize() {
		M_Gamemode[] objects = Resources.FindObjectsOfTypeAll<M_Gamemode>();
		foreach (M_Gamemode obj in objects) {
			if (obj != null && !string.IsNullOrEmpty(obj.name)) {
				gameModeDict[obj.gamemodeName] = obj;
				gameModeDict[obj.name] = obj;
			}
		}
	}

	/// <summary>
	/// 当玩家退出大厅或返回主菜单时调用，清除同步数据
	/// </summary>
	public static void ClearCurrentData() {
		CurrentData = null;
	}

	/// <summary>
	/// 获取当前游戏模式数据
	/// </summary>
	public static GameModeData GetGameModeData() {
		var gameModedata = new GameModeData {
			isIron = SettingsManager.settings.g_iron,
			isHard = SettingsManager.settings.g_hard,
			gameModeName = CL_GameManager.gamemode.name,
			gameModeObjectName = CL_GameManager.gamemode.ToString(),
		};
		if (WorldLoader.instance != null) {
			gameModedata.seed = WorldLoader.instance.seed;
		} else {
			gameModedata.seed = null;
		}
		return gameModedata;
	}

	public static void LoadGameMode(GameModeData data) {
		// 字典内没有数据时重初始化字典
		if (gameModeDict.Count == 0) Initialize();
		// 保存当前数据
		CurrentData = data;
		// 更改游戏模式
		if (!gameModeDict.TryGetValue(data.gameModeName, out var m_Gamemode)) {
			MPMain.LogError(Localization.Get("MPGameModeManager", "GameModeNotFound", data.gameModeName));
			return;
		}
		// 设置游戏模式
		CL_GameManager.gMan.SetGamemode(m_Gamemode);
		// 更改难度
		SettingsManager.settings.g_iron = data.isIron;
		SettingsManager.settings.g_hard = data.isHard;
		// 设置种子
		if (data.seed is int value && (WorldLoader.instance?.seed != value)) {
			WorldLoader.SetPresetSeed(value.ToString());
		}
		// 手动重载地图
		SceneManager.LoadScene(m_Gamemode.gamemodeScene);
	}

	// 尝试获取游戏模式
	public static bool TryGetGameMode(string name, out M_Gamemode gamemode) {
		if (gameModeDict.Count == 0)
			Initialize();
		return gameModeDict.TryGetValue(name, out gamemode);
	}
}
