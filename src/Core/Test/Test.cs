using BepInEx;
using HarmonyLib;
using Steamworks.Data;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.NetWork;
using WKMPMod.RemotePlayer;
using WKMPMod.UI;
using WKMPMod.Util;
using static CL_AchievementManager;
using static CommandConsole;
using static FXManager;
using Object = UnityEngine.Object;
using Quaternion = UnityEngine.Quaternion;
using Random = UnityEngine.Random;
using Vector3 = UnityEngine.Vector3;

namespace WKMPMod.Test;

public class Test : MonoBehaviour {
	public const string NO_ITEM_PREFAB_NAME = "None";
	public static float x = 0;
	public static float y = 0;
	public static float z = 0;
	public static ulong id = 0;
	public static Dictionary<string, M_Gamemode> gamemodeMap = new Dictionary<string, M_Gamemode>();
	public static void Main(string[] args) {

		if (args.Length == 0) {
			Debug.Log("测试命令需要参数");
			return;
		}

		// 使用 switch 表达式使代码更简洁
		_ = args[0] switch {
			"0" => RunCommand(GetGraphicsAPI),		// 获取图形API信息
			"1" => RunCommand(GetMPStatus),			// 获取联机状态
			"2" => RunCommand(GetMassData),			// 获取Mass数据
			"3" => RunCommand(GetSystemLanguage),   // 获取系统语言
			"4" => RunCommand(() => CreateRemotePlayer(args[1..])), // 创建远程玩家,参数:玩家ID(ulong),预制体工厂ID(string)
			"5" => RunCommand(() => RemoveRemotePlayer(args[1..])), // 移除远程玩家,参数:玩家ID(ulong)
			"6" => RunCommand(() => UpdateRemoteTag(args[1..])),    // 更新远程玩家标签,参数:标签文本(string)
			"7" => RunCommand(GetAllFactoryList),   // 列出所有预制体工厂信息
			"8" => RunCommand(GetPath),             // 获取程序路径信息
			"9" => RunCommand(CreateTestPrefab),    // 创建测试预制体
			"10" => RunCommand(GetHandCosmetic),    // 获取手部皮肤信息
			"11" => RunCommand(CreateDontDestroyGameObject),	// 创建测试对象并设置DontDestroyOnLoad
			"12" => RunCommand(DisplayMessageTest),				// 测试UI消息显示
			"13" => RunCommand(SimulationPlayerUpdata),			// 模拟玩家数据更新事件
			"14" => RunCommand(() => GetAssetGameObject(args[1..])),		// 获取预制体测试,参数:预制体名称(string),数据库名称(string,可选)
			"15" => RunCommand(() => GetAllAssetGameObject(args[1])),		// 获取全部预制体测试,参数:预制体名称(string)
			"16" => RunCommand(() => GetParticleEffectPrefab(args[1])),			// 获取粒子特效预制体测试,参数:预制体名称(string)
			"17" => RunCommand(() => MPCore.Instance.ResetStateVariables()),	// 重置状态变量测试
			"18" => RunCommand(SearchAllLobby),                     // 大厅搜索测试
			"19" => RunCommand(LoadAllGameMode),                    // 获取游戏模式
			"20" => RunCommand(() => LoadGamemode(args[1..])),      // 加载游戏模式
			"21" => RunCommand(GetAllGameModeData),                 // 获取同步时需要的数据
			"22" => RunCommand(GetLobbyData),                       // 获取大厅数据
			"23" => RunCommand(() => GetOtherLobbyData(args[1])),   // 获取其他大厅数据
			"24" => RunCommand(GetAllFX),                           // 获取全特效
			"25" => RunCommand(() => PlayParticle(args[1], new Vector3(1, 1, 1), 5)),	// 生成特效
			_ => RunCommand(() => Debug.Log($"未知命令: {args[0]}"))
		};
	}

	// 辅助方法:安全执行命令
	private static bool RunCommand(Action action) {
		action();
		return true;
	}

	public static void GetGraphicsAPI() {
		// 方法1:直接获取当前API
		Debug.Log($"当前图形API: {SystemInfo.graphicsDeviceType}");

		// 方法2:获取详细版本信息
		Debug.Log($"图形API版本: {SystemInfo.graphicsDeviceVersion}");

		// 方法3:获取Shader Model级别
		int smLevel = SystemInfo.graphicsShaderLevel;
		Debug.Log($"Shader Model: {smLevel / 10}.{smLevel % 10}");

		// 方法4:检查具体功能支持
		Debug.Log($"支持计算着色器: {SystemInfo.supportsComputeShaders}");
		Debug.Log($"支持几何着色器: {SystemInfo.supportsGeometryShaders}");
		Debug.Log($"支持曲面细分: {SystemInfo.supportsTessellationShaders}");
		Debug.Log($"支持GPU实例化: {SystemInfo.supportsInstancing}");
	}
	// 输出联机模式状态
	public static void GetMPStatus() {
		Debug.Log($"{((int)(MPCore.MultiPlayerStatus)).ToString()}");
		Debug.Log($"Is in lobby {MPCore.IsInLobby}");
		Debug.Log($"Is initialized {MPCore.IsInitialized}");
	}
	// 输出Mass数据
	public static void GetMassData() {
		var data = DEN_DeathFloor.instance.GetSaveData();
		Debug.Log($"高度:{data.relativeHeight}, 是否活动:{data.active}, 速度:{data.speed}, 速度乘数:{data.speedMult}");
	}
	// 输出系统语言
	public static void GetSystemLanguage() {
		Debug.Log($"系统语言:{Localization.GetGameLanguage()}");
	}
	// 创建远程玩家
	public static void CreateRemotePlayer(string[] args) {
		id += 1;
		string prefab = "default";
		if (args.Length >= 1 && ulong.TryParse(args[0], out ulong parsedId)) {
			id = parsedId;
		}
		if (args.Length >= 2) {
			prefab = string.Join(" ", args[1..]);
		}
		RPManager.Instance.PlayerCreate(id, prefab);
		RPManager.Instance.Players[id]
			.HandlePlayerData(new PlayerData { Position = new Vector3(x, y, z) });
		y += 4.0f;
	}
	// 移除远程玩家
	public static void RemoveRemotePlayer(string[] args) {
		int id = 1;
		if (args.Length >= 1 && int.TryParse(args[0], out int parsedId)) {
			id = parsedId;
		}
		RPManager.Instance.PlayerRemove((ulong)id);
	}
	// 更新远程玩家名字标签
	public static void UpdateRemoteTag(string[] args) {
		string tagText = args.Length > 0
			? string.Join(" ", args)
			: "中文测试: 斯卡利茨恐虐神选";

		if (RPManager.Instance.Players.TryGetValue(1, out var player)) {
			player.HandleNameTag(tagText);
		} else {
			Debug.LogWarning("玩家ID 1 不存在");
		}
	}
	// 获取程序路径信息
	public static void GetPath() {
		//D:\GAME\Steam\steamapps\common\White Knuckle\BepInEx\plugins
		MPMain.LogInfo(Paths.PluginPath);
		//D:\GAME\Steam\steamapps\common\White Knuckle\BepInEx\plugins\MultiPlayer\WKMultiPlayerMod.dll
		MPMain.LogInfo(Assembly.GetExecutingAssembly().Location);
		//D:\GAME\Steam\steamapps\common\White Knuckle
		MPMain.LogInfo(AppDomain.CurrentDomain.BaseDirectory);
		//D:/GAME/Steam/steamapps/common/White Knuckle/White Knuckle_Data
		MPMain.LogInfo(Application.dataPath);
		//D:\GAME\Steam\steamapps\common\White Knuckle\BepInEx\plugins\MultiPlayer
		MPMain.LogInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty);

		MPMain.LogInfo(MPMain.path);
	}
	// 创建测试预制体
	public static void CreateTestPrefab() {
		var bundle = AssetBundle.LoadFromFile(Path.Combine(MPMain.path, "playerprefab"));
		BaseRemoteFactory.ListAllAssetsInBundle(bundle);
		var rawPrefab = bundle.LoadAsset<GameObject>("cl_player");
		Instantiate(rawPrefab);
	}

	// 列出所有预制体工厂信息
	public static void GetAllFactoryList() {
		RPFactoryManager.Instance.ListAllFactory();
	}

	public static void GetAllPlayerList() {


	}
	// 获取手部皮肤信息
	public static void GetHandCosmetic() {
		MPMain.LogWarning($"左手皮肤id {CL_CosmeticManager.GetCosmeticInHand(0).cosmeticData.id}");
		MPMain.LogWarning($"右手皮肤id {CL_CosmeticManager.GetCosmeticInHand(1).cosmeticData.id}");
	}
	// 创建根对象测试DontDestroyOnLoad
	public static void CreateDontDestroyGameObject() {
		GameObject singleton1 = new GameObject("Test Game Object1");
		DontDestroyOnLoad(singleton1);
		GameObject singleton2 = new GameObject("Test Game Object2");
	}
	// 模拟玩家数据更新事件
	public static void SimulationPlayerUpdata() {
		byte[] data = { 0x01 };
		ArraySegment<byte> segment = new ArraySegment<byte>(data);
		MPEventBusNet.NotifyReceive(1, segment);
	}
	// 管理器预制体查询测试
	public static void GetAssetGameObject(string[] args) {
		string name, database = "";
		if (args.Length < 1) {
			MPMain.LogError("[MP Debug]需要至少一个参数: 预制体名称");
			return;
		}
		name = args[0];
		if (args.Length >= 2)
			database = args[1];
		if (CL_AssetManager.GetAssetGameObject(name, database) != null)
			MPMain.LogInfo($"[MP Debug]成功获取预制体: {name} 来自数据库: {database}");
		else
			MPMain.LogError($"[MP Debug]获取预制体失败: {name} 来自数据库: {database}");
	}
	// 全部预制体查询测试
	public static void GetAllAssetGameObject(string prefabName) {
		// Resources.FindObjectsOfTypeAll会找到所有已加载的资源
		GameObject[] allObjects = Resources.FindObjectsOfTypeAll<GameObject>();
		foreach (GameObject obj in allObjects) {
			// 关键判断：预制体不在场景中(scene.name == null)
			if (obj.scene.name == null && obj.name == prefabName) {
				Debug.Log($"[MP Debug]找到预制体: {prefabName}, 类型: {(obj.hideFlags == HideFlags.None ? "普通预制体" : "内部资源")}");
				return;
			}
		}
		Debug.LogWarning($"[MP Debug]找不到预制体: {prefabName}");
	}
	// 仅粒子特效预制体查询测试
	public static void GetParticleEffectPrefab(string prefabName) {

		// 默认生成位置: 相机位置 + 相机前方1单位
		Vector3 position = Camera.main.transform.position + Camera.main.transform.forward;
		// 默认旋转: 无旋转
		Quaternion identity = Quaternion.identity;

		GameObject[] allObjects = Resources.FindObjectsOfTypeAll<GameObject>();
		foreach (GameObject obj in allObjects) {
			if (obj.scene.name == null && obj.name == prefabName) {
				ParticleSystem ps = obj.GetComponent<ParticleSystem>();
				if (ps != null) {
					Debug.Log($"[MP Debug]找到粒子特效预制体: {prefabName}");
					GameObject.Instantiate(obj, position, identity);
					return;
				}
			}
		}
		Debug.LogWarning($"[MP Debug]找不到粒子特效预制体: {prefabName}");

		//var particle = MPAssetManager.GetAssetGameObject(prefabName);
		//if (particle != null) {
		//	GameObject.Instantiate(particle, position, identity);
		//	Debug.Log($"[MP Debug]找到粒子特效预制体: {prefabName}");
		//} else {
		//	Debug.LogWarning($"[MP Debug]找不到粒子特效预制体: {prefabName}");
		//}
	}
	// 启动异步搜索大厅函数
	public static void SearchAllLobby() {
		StartLobbySearchAsync();
	}
	private static async void StartLobbySearchAsync() {
		try {
			MPMain.LogInfo("[MP Debug] 搜索大厅...");

			var query = new Steamworks.Data.LobbyQuery()
				.FilterDistanceWorldwide()
				.WithKeyValue("game", "White Knuckle")
				.WithMaxResults(20);

			var lobbies = await query.RequestAsync();

			if (lobbies != null) {
				foreach (var lobby in lobbies) {
					Console.WriteLine($"[MP Debug] 发现大厅: {lobby.Id}");
				}
			}
		} catch (Exception ex) {
			MPMain.LogError($"[MP Debug] 搜索失败: {ex.Message}");
		}
	}
	// 获取游戏模式
	public static void LoadAllGameMode() {
		// Resources.FindObjectsOfTypeAll 会找到所有已加载的对象
		// 包括场景中的、Resources 中的、以及项目中的
		M_Gamemode[] objects = Resources.FindObjectsOfTypeAll<M_Gamemode>();
		// 过滤掉未保存的临时对象(可选)
		foreach (M_Gamemode obj in objects) {
			if (obj != null && !string.IsNullOrEmpty(obj.name)) {
				MPMain.LogWarning($"[MP Debug] GameMode: {obj.gamemodeName} ObjectName: {obj.name}");
				gamemodeMap[obj.gamemodeName] = obj;
				gamemodeMap[obj.name] = obj;
			}
		}
	}
	// 加载游戏模式
	public static void LoadGamemode(string[] args) {
		string gamemodeName = string.Join(" ", args);
		if (!gamemodeMap.TryGetValue(gamemodeName, out var m_Gamemode)) {
			MPMain.LogError($"[MP Debug] 未找到对应游戏模式");
			return;
		}
		if (UI_GamemodeScreen.instance == null) {
			// 这个只有主菜单有
			MPMain.LogError($"[MP Debug] UI_GamemodeScreen.instance为空");
			CL_GameManager.gamemode = m_Gamemode;
			// 测试铁指/困难的修改应该在什么时期使用
			SettingsManager.SetSetting(["g_iron", "true"]);
			SettingsManager.SetSetting(["g_hard", "true"]);
			SceneManager.LoadScene(m_Gamemode.gamemodeScene);
			return;
		} else {
			UI_GamemodeScreen.instance.Initialize(m_Gamemode);
			UI_GamemodeScreen.instance.LoadGamemode();
		}
	}
	// 获取全部所需数据
	public static void GetAllGameModeData() {
		MPMain.LogWarning($"[MP Debug] Is Iron Mod:{SettingsManager.GetSetting("g_iron")}");
		MPMain.LogWarning($"[MP Debug] Is Hard Mod:{SettingsManager.GetSetting("g_hard")}");
		MPMain.LogWarning($"[MP Debug] Gamemode:{CL_GameManager.gamemode}");
		MPMain.LogWarning($"[MP Debug] World Seed:{WorldLoader.instance.seed}");
	}
	// 获取全部解锁条件
	public static void GetGameModeData() {
		Dictionary<string, GameAchievement> dict = Traverse.Create(CL_AchievementManager.instance)
			.Field("achievementDictionary")
			.GetValue<Dictionary<string, GameAchievement>>();
		foreach (var (key, value) in dict) {
			MPMain.LogWarning($"[MP Debug] key: {key}, value: {value.flagged}");
		}
	}
	// 获取大厅数据
	public static void GetLobbyData() {
		if (MPSteamworks.Instance.IsInLobby) {
			MPMain.LogWarning($"[MP Debug] 当前大厅ID: {MPSteamworks.Instance._currentLobby.Id}");
			MPMain.LogWarning($"[MP Debug] 大厅成员数量: {MPSteamworks.Instance._currentLobby.MemberCount}");
			MPMain.LogWarning($"[MP Debug] 房主名称: {MPSteamworks.Instance._currentLobby.Owner.Name}");
			foreach (var member in MPSteamworks.Instance._currentLobby.Members) {
				MPMain.LogWarning($"[MP Debug] 成员ID: {member.Id}, 昵称: {member.Name}");
			}
		} else {
			MPMain.LogWarning($"[MP Debug] 当前不在任何大厅中");
		}
	}

	// 获取其他大厅数据
	public static void GetOtherLobbyData(string lobbyId) {
		if (!ulong.TryParse(lobbyId, out ulong parsedLobbyId)) {
			MPMain.LogError($"[MP Debug] 无效的大厅ID: {lobbyId}");
			return;
		}
		var lobby = new Lobby(parsedLobbyId);
		MPMain.LogWarning($"[MP Debug] 大厅成员数量: {lobby.MemberCount} 房主名称: {lobby.Owner.Name}");
		foreach (var member in lobby.Members) {
			MPMain.LogWarning($"[MP Debug] 成员ID: {member.Id}, 昵称: {member.Name}");
		}
		foreach (var (key, value) in lobby.Data) {
			MPMain.LogWarning($"[MP Debug] Data Key: {key}, Value: {value}");
		}
	}
	public static void DisplayMessageTest() {
		UI_Manager.Instance.DisplayMessage("[randomchar s=0.2 c=0.1]AAAAAAAAAAAA[/randomchar]", UI_Manager.UIDisplayType.AscentHeader);
		UI_Manager.Instance.DisplayMessage("[randomchar s=0.2 c=0.1]BBBBBBBBBBBB[/randomchar]", UI_Manager.UIDisplayType.TipHeader);
		UI_Manager.Instance.DisplayMessage("[randomchar s=0.2 c=0.1]CCCCCCCCCCCC[/randomchar]", UI_Manager.UIDisplayType.Header);
		UI_Manager.Instance.DisplayMessage("[randomchar s=0.2 c=0.1]DDDDDDDDDDDD[/randomchar]", UI_Manager.UIDisplayType.HighscoreHeader);
	}

	public static void GetAllFX() {
		FieldInfo field = typeof(FXManager).GetField(
			"particleDict",
			BindingFlags.NonPublic |
			BindingFlags.Instance);
		var dict =(Dictionary<string, ParticleAsset>) field.GetValue(FXManager.fxMan);
		foreach (var key in dict) {
			MPMain.LogWarning($"[MP Debug] {key}");
		}
	}

}
public class CheatsTest : MonoBehaviour {
	public static void Main(string[] args) {

		if (args.Length == 0) {
			Debug.Log("测试命令需要参数");
			return;
		}

		// 使用 switch 表达式使代码更简洁
		_ = args[0] switch {
			"0" => RunCommand(() => CreateItem(args[1..])),  // 创建物品,参数:物品预制体名称(string)
			"1" => RunCommand(() => AddItemInInventory(args[1..])),  // 创建物品并放入库存,参数:物品预制体名称(string)
			"2" => RunCommand(GetInventoryItems),  // 获取库存信息
			"3" => RunCommand(AddItemInInventoryQuaternionTest),
			_ => RunCommand(() => Debug.Log($"未知命令: {args[0]}"))
		};
	}
	// 辅助方法:安全执行命令
	private static bool RunCommand(Action action) {
		action();
		return true;
	}

	// 创建物品测试
	public static void CreateItem(string[] args) {
		foreach (var arg in args) {
			if (arg != "None") {
				// 从资源管理器获取预制体
				GameObject prefabAsset = CL_AssetManager.GetAssetGameObject(arg);
				if (prefabAsset != null) {
					// 随机位置
					Vector3 randomOffset = new Vector3(
										Random.Range(-1f, 1f),     // X轴随机
										Random.Range(0f, 0.5f),     // Y轴随机(向上)
										Random.Range(-1f, 1f)       // Z轴随机
									);

					// 实例化物品
					var itemObject = GameObject.Instantiate(
						prefabAsset,
						new Vector3(0, 0.5f, 0) + randomOffset,
						Random.rotation  // 随机旋转
					);

					// 获取Rigidbody并添加随机斜上方动量
					Rigidbody rb = itemObject.GetComponent<Rigidbody>();
					if (rb != null) {
						// 随机方向: 斜上方 (XZ随机,Y固定向上)
						Vector3 randomDirection = new Vector3(
							Random.Range(-1f, 1f),  // X轴随机方向
							1f,                     // Y轴向上
							Random.Range(-1f, 1f)   // Z轴随机方向
						).normalized;

						// 随机力度 (3-8之间)
						float randomForce = Random.Range(3f, 8f);

						// 添加冲量(瞬间力)
						rb.AddForce(randomDirection * randomForce, ForceMode.Impulse);

						// 可选: 添加随机旋转扭矩,让物品在空中旋转
						//rb.AddTorque(Random.insideUnitSphere * Random.Range(1f, 5f), ForceMode.Impulse);
					}
				} else {
					MPMain.LogInfo($"[MP Debug] 生成物: {arg} 不存在");
				}
			}
		}
	}
	// 获取库存内全部物品信息
	public static void GetInventoryItems() {
		// 获取库存单例
		var inventory = Inventory.instance;
		if (inventory != null) {
			// 获取库存中的物品列表
			var items = inventory.GetItems();
			foreach (var item in items) {
				MPMain.LogInfo($"物品名称: {item.itemName}, 标签: {item.itemTag}, 预制体名称: {item.prefabName}");
			}
		} else {
			MPMain.LogWarning("库存不存在");
		}
	}
	// 创建物品并放入库存测试
	public static void AddItemInInventory(string[] args) {
		var inventory = Inventory.instance;
		foreach (var arg in args) {
			if (arg != "None") {
				// 从资源管理器获取预制体
				GameObject prefabAsset = CL_AssetManager.GetAssetGameObject(arg);
				if (prefabAsset != null) {
					// 实例化物品在 0,0.5,0 
					var item = Instantiate(prefabAsset, new Vector3(0, 0.5f, 0), Quaternion.identity);
					var itemObject = item.GetComponent<Item_Object>();
					var itemData = itemObject.itemData;
					if (itemObject != null) {
						itemObject.itemData.bagRotation = Quaternion.LookRotation(itemData.upDirection);

						inventory.AddItemToInventoryCenter(itemObject.itemData);

						// 隐藏镜像物品对象,因为它已经被添加到库存中,不需要在场景中显示
						itemObject.gameObject.SetActive(false);
					} else {
						MPMain.LogInfo($"[MP Debug] 生成物: {item.name} 不可放入库存");
					}

				} else {
					MPMain.LogInfo($"[MP Debug] 生成物: {arg} 不存在");
				}
			}
		}
	}
	// 创建物品并放入库存测试(旋转版本)
	public static void AddItemInInventoryQuaternionTest() {
		var inventory = Inventory.instance;

		void AddItemInInventoryQuaternionTest(string arg) {
			// 从资源管理器获取预制体
			GameObject prefabAsset = CL_AssetManager.GetAssetGameObject(arg);
			var item_Object = prefabAsset.GetComponent<Item_Object>();
			item_Object.itemData.bagRotation = Quaternion.LookRotation(item_Object.itemData.upDirection); // 设置物品数据中的旋转
			inventory.AddItemToInventoryCenter(item_Object.itemData);
			// 隐藏镜像物品对象,因为它已经被添加到库存中,不需要在场景中显示
			item_Object.gameObject.SetActive(false);
		}
		// 不能用预制体生成,必须要实际对象
		AddItemInInventoryQuaternionTest("Item_Rebar");
		AddItemInInventoryQuaternionTest("Item_Rebar");
		AddItemInInventoryQuaternionTest("Item_Rebar");
		AddItemInInventoryQuaternionTest("Item_Rebar_Explosive");
		AddItemInInventoryQuaternionTest("Item_RebarRope");
		AddItemInInventoryQuaternionTest("Item_Rebar_Holiday");
		AddItemInInventoryQuaternionTest("Item_RebarRope_Holiday");
	}
}