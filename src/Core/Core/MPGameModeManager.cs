using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
using WKMPMod.Util;
using static CL_AssetManager;

namespace WKMPMod.Core;

public class MPGameModeManager {
	public record struct GameModeData {
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

		//MPMain.LogWarning($"[MP Debug] 全部游戏模式");
		FieldInfo field = typeof(CL_AssetManager).GetField(
			"activeDatabases", BindingFlags.NonPublic | BindingFlags.Static);
		if (field == null) {
			MPMain.LogError("[MP Debug] 字段未找到");
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
	public static GameModeData CaptureCurrentData() {
		CurrentData = new GameModeData {
			isIron = SettingsManager.settings.g_iron,
			isHard = SettingsManager.settings.g_hard,
			gameModeName = CL_GameManager.gamemode.name,
			gameModeObjectName = CL_GameManager.gamemode.ToString(),
			seed = WorldLoader.instance != null ? WorldLoader.instance.seed : (int?)null
		};
		return CurrentData.Value;
	}

	public static void LoadGameModeIfChanged(GameModeData data) { 
		if (CurrentData.HasValue && CurrentData.Value == data)
			return;
		LoadGameMode(data);
	}

	/// <summary>
	/// 通过GameModeData加载游戏模式, 包括游戏模式、难度和种子
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
		// 更改难度
		SettingsManager.settings.g_iron = data.isIron;
		SettingsManager.settings.g_hard = data.isHard;
		// 一直设置种子, 相同种子而跳过设置种子会导致种子重随机
		if (data.seed is int value) {
			WorldLoader.SetPresetSeed(value.ToString());
		}
		// 手动重载地图
		SceneManager.LoadScene(m_Gamemode.gamemodeScene);
	}

	/// <summary>
	/// 重加载游戏模式
	/// </summary>
	public static void RestartGameMode() {
		if (CurrentData.HasValue) {
			LoadGameMode(CurrentData.Value);
		} else {
			MPMain.LogWarning(Localization.Get("MPGameModeManager.NoCurrentGameModeData"));
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
