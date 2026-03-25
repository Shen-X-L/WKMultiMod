using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using WKMPMod.Core;

namespace WKMPMod.Core;

public class GameModeManager {
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
			MPMain.LogError($"[MP Debug] 未找到对应游戏模式:{data.gameModeName}");
		} else {
			CL_GameManager.gamemode = m_Gamemode;
		}
		// 更改难度
		SettingsManager.settings.g_iron = data.isIron;
		SettingsManager.settings.g_hard = data.isHard;
		// 存在种子且种子不同时用种子
		if (data.seed is int value
			&& WorldLoader.instance != null
			&& value != WorldLoader.instance.seed) {

			WorldLoader.SetPresetSeed(value.ToString());
		}
		// 手动重载地图
		SceneManager.LoadScene(m_Gamemode.gamemodeScene);
	}
}
