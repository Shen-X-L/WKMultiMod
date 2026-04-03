using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using WKMPMod.Core;
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
	
	// 获取全部游戏模式
	public static void Initialize() {
		M_Gamemode[] objects = Resources.FindObjectsOfTypeAll<M_Gamemode>();
		foreach (M_Gamemode obj in objects) {
			if (obj != null && !string.IsNullOrEmpty(obj.name)) {
				//MPMain.LogWarning($"[MP Debug] GameMode: {obj.gamemodeName} ObjectName: {obj.name}");
				gameModeDict[obj.gamemodeName] = obj;
				gameModeDict[obj.name] = obj;
			}
		}
	}

	// 获取当前游戏模式数据
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
		if (gameModeDict.Count == 0)
			Initialize();
		// 更改游戏模式
		if (!gameModeDict.TryGetValue(data.gameModeName, out var m_Gamemode)) {
			MPMain.LogError(Localization.Get("MPGameModeManager", "GameModeNotFound", data.gameModeName));
			return;
		} else {
			CL_GameManager.gMan.SetGamemode(m_Gamemode);
			//CL_GameManager.gamemode = m_Gamemode;
		}
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
