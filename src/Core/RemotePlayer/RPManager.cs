using Steamworks;
using System.Collections.Generic;
using UnityEngine;
using WKMPMod.Asset;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.Util;
using static WKMPMod.UI.UI_Manager;
using Object = UnityEngine.Object;

namespace WKMPMod.RemotePlayer;

// 生命周期为全局
public class RPManager : Singleton<RPManager> {

	public const string NO_ITEM_NAME = "None";
	public const string HAMMER_NAME = "Item_Hammer";
	public const string ARTIFACT_NAME = "Artifact";
	public const string BLINK_EYE = "Item_BlinkEye";

	// Debug日志输出间隔
	private TickTimer _debugTick = new TickTimer(5f);
	// 存储所有远程对象
	internal Dictionary<ulong, RPContainer> Players = new Dictionary<ulong, RPContainer>();
	private Dictionary<ulong, float> _lastDeathTime = new Dictionary<ulong, float>();
	// 同一个玩家 1 秒内只能处理一次死亡信息, 避免重复处理导致的物品重复掉落和动画冲突
	private const float DEATH_COOLDOWN = 1.0f; 
	// 根对象引用
	private Transform _remotePlayersRoot;

	private RPManager() {
		_ = RPFactoryManager.Instance;
	}

	public void Initialize(Transform RootTransform) {
		_remotePlayersRoot = RootTransform;
	}

	/// <summary>
	/// 清除全部玩家
	/// </summary>
	public void ResetAll() {
		foreach (var container in Players.Values) {
			RPFactoryManager.Instance.Cleanup(container.PlayerObject);
			container.Destroy();
		}
		Players.Clear();
	}

	#region[创建/销毁玩家]
	/// <summary>
	/// 根据Id创建玩家
	/// </summary>
	public RPContainer PlayerCreate(ulong playId, string prefab) {
		if (Players.TryGetValue(playId, out var existing))
			return existing;

		var container = new RPContainer(playId);

		// 从工厂直接获取实例
		GameObject instance = RPFactoryManager.Instance.Create(prefab);

		if (instance == null) {
			MPMain.LogError(Localization.Get("RPManager.FactoryCreateObjectFailed"));
			return null;
		}

		container.Initialize(instance, _remotePlayersRoot);
		Players[playId] = container;
		return container;
	}

	/// <summary>
	/// 清除特定玩家
	/// </summary>
	public void PlayerRemove(ulong playId) {
		if (Players.TryGetValue(playId, out var container)) {

			// 生成死亡特效
			var playerPosition = container.PlayerObject.transform.position;
			var playerRotation = container.PlayerObject.transform.rotation;

			var deathParticle = MPAssetManager.GetAssetGameObject(MPAssetManager.DEATH_OBJECT_NAME);
			if (deathParticle != null) 
				GameObject.Instantiate(deathParticle,playerPosition, playerRotation);

			// 工厂清理
			RPFactoryManager.Instance.Cleanup(container.PlayerObject);

			// 容器清理引用
			container.Destroy();

			// 字典删除
			Players.Remove(playId);
		}
	}
	#endregion

	#region[处理消息]

	/// <summary>
	/// 处理玩家数据
	/// </summary>
	public void ProcessPlayerData(ulong playerId,ref PlayerData playerData) {
		if (!MPCore.IsInitialized || !MPCore.IsInLobby) return;

		// 以后加上时间戳处理
		if (Players.TryGetValue(playerId, out var RPcontainer)) {
			RPcontainer.HandlePlayerData(ref playerData);
			return;
		} else if (_debugTick.TryTick()) {
			MPMain.LogError(Localization.Get(
				"RPManager.RemotePlayerObjectNotFound", playerId.ToString()));
			return;
		}
		return;
	}

	/// <summary>
	/// 处理玩家数据
	/// </summary>
	public void ProcessPlayerTag(ulong playerId, string massage) {

		// 以后加上时间戳处理
		if (Players.TryGetValue(playerId, out var RPcontainer)) {
			RPcontainer.HandleNameTag(massage);
			return;
		}
		MPMain.LogError(Localization.Get(
			"RPManager.RemotePlayerObjectNotFound", playerId.ToString()));
		return;

	}

	/// <summary>
	/// 处理玩家死亡
	/// </summary>
	public void ProcessPlayerDeath(ulong playerId, string type, Dictionary<string, byte> remoteItems) {
		float currentTime = Time.time;

		if (_lastDeathTime.TryGetValue(playerId, out float lastTime)) {
			if (currentTime - lastTime < DEATH_COOLDOWN) {
				MPMain.LogWarning($"[MP Debug] 玩家 {playerId} 死亡信息频率过高");
				return;
			}
		}
		_lastDeathTime[playerId] = currentTime;

		string playerName = new Friend(playerId).Name;
		MPCore.SystemMessage(Localization.GetRandom("DisplayMessage.PlayerDeath", playerName, type), UIDisplayType.HighscoreHeader);

		// 获取玩家对象
		GetPlayerObject(playerId);
		if (!Players.TryGetValue(playerId, out var container)) {
			return;
		}
		var playerObject = container.PlayerObject;
		if (playerObject == null) {
			return;
		}

		var playerPosition = playerObject.transform.position;

		foreach (var (itemId, count) in remoteItems) {
			if (itemId == NO_ITEM_NAME)
				continue;
			if (itemId == HAMMER_NAME)
				continue;

			GameObject itemPrefab = CL_AssetManager.GetAssetGameObject(itemId);
			if (itemPrefab == null) {
				MPMain.LogError(Localization.Get("MPMessageHandlers.PrefabDoesNotExist", itemId));
				continue;
			}

			for (int i = 0; i < count; i++) {
				// 随机位置 (-1~1,0.5~1,-1~1)
				Vector3 offset = new Vector3(
					Random.Range(-1f, 1f), Random.Range(0.5f, 1f), Random.Range(-1f, 1f));

				// 实例化物品
				var itemObject = GameObject.Instantiate(
					itemPrefab, playerPosition + offset, Random.rotation);

				// 获取Rigidbody并添加随机斜上方动量
				if (itemObject.TryGetComponent<Rigidbody>(out var rb)) {
					// 随机动量方向: (-1~1,1,-1~1)再归一化
					Vector3 direction = new Vector3(
						Random.Range(-1f, 1f), 1f, Random.Range(-1f, 1f)).normalized;
					// 添加冲量 力度(1-2)
					rb.AddForce(direction * Random.Range(1f, 2f), ForceMode.Impulse);
					// 可选: 添加随机旋转扭矩,让物品在空中旋转
					//rb.AddTorque(Random.insideUnitSphere * Random.Range(1f, 5f), ForceMode.Impulse);
				}
			}
		}

		if (Players.TryGetValue(playerId, out var RPcontainer)) {
			RPcontainer.HandleDeath();
			return;
		}
		MPMain.LogError(Localization.Get(
			"RPManager.RemotePlayerObjectNotFound", playerId.ToString()));
		return;
	}

	#endregion

	#region[获取玩家对象]

	// 返回玩家对象
	public GameObject GetPlayerObject(ulong playerId) {
		if (Players.TryGetValue(playerId, out var container)) {
			return container.PlayerObject;
		}
		MPMain.LogError(Localization.Get(
			"RPManager.RemotePlayerObjectNotFound", playerId.ToString()));
		return null;
	}
	#endregion
}