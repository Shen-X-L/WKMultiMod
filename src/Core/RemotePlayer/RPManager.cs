using Steamworks;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
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
	internal Dictionary<IDType, RPContainer> Players = new Dictionary<IDType, RPContainer>();
	private Dictionary<IDType, float> _lastDeathTime = new Dictionary<IDType, float>();
	// 同一个玩家 1 秒内只能处理一次死亡信息, 避免重复处理导致的物品重复掉落和动画冲突
	private const float DEATH_COOLDOWN = 1.0f;
	// 根对象引用
	private Transform _remotePlayersRoot;
	// 加入信息和离开信息
	private HashSet<IDType> joinMessages = new HashSet<IDType>();
	private Dictionary<IDType, string> leaveMessages = new Dictionary<IDType, string>();

	private RPManager() {
		_ = RPFactoryManager.Instance;
	}

	#region[RAII]

	public void Initialize(Transform RootTransform) {
		_remotePlayersRoot = RootTransform;
		MPEventBusGame.OnPlayerDamage += ProcessPlayerDamage;
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
		joinMessages.Clear();
		leaveMessages.Clear();
		RPFactoryManager.Instance.ClearPrefabCache();
	}

	#endregion
	#region[创建/销毁玩家]

	/// <summary>
	/// 根据Id创建玩家
	/// </summary>
	public RPContainer PlayerCreate(IDType playId, string prefab) {
		if (Players.TryGetValue(playId, out var existing))
			return existing;

		// 从工厂获取物体
		GameObject instance = RPFactoryManager.Instance.Create(prefab);
		if (instance == null) {
			MPMain.LogError(Localization.Get("RPManager.FactoryCreateObjectFailed"));
			return null;
		}

		// 构造对象
		var container = new RPContainer(playId, prefab);

		// 检查初始化是否成功
		if (!container.Initialize(instance, _remotePlayersRoot)) {
			// Initialize 内部已经 Destroy 了 instance, 这里直接返回 null 即可
			return null;
		}

		// 成功后才写入缓存
		Players[playId] = container;
		return container;
	}

	/// <summary>
	/// 清除特定玩家
	/// </summary>
	public void PlayerRemove(ulong playerId) {
		if (Players.TryGetValue(playerId, out var container)) {

			// 生成死亡特效
			var playerPosition = container.PlayerObject.transform.position;
			var playerRotation = container.PlayerObject.transform.rotation;

			var deathParticle = MPAssetManager.GetAssetGameObject(MPAssetManager.DEATH_OBJECT_NAME);
			if (deathParticle != null) {
				MPMain.Debug(playerPosition.ToString());
				GameObject.Instantiate(deathParticle, playerPosition, playerRotation);
			}

			// 工厂清理
			RPFactoryManager.Instance.Cleanup(container.PlayerObject);

			// 容器清理引用
			container.Destroy();

			// 字典删除
			Players.Remove(playerId);
		}
	}

	#endregion
	#region[处理消息]

	/// <summary>
	/// 处理玩家数据字典
	/// </summary>
	public void ProcessMemberData(ulong playerId, Dictionary<string, string> data) {
		// 更新玩家模型
		if (data.TryGetValue(MPKeys.PREFAB_ID, out var prefabId)) {
			// 不存在 → 创建
			if (!Players.TryGetValue(playerId, out var container)) {
				PlayerCreate(playerId, prefabId);
			}
			// 存在但不一致 → 重建
			else if (container.prefabId != prefabId) {
				PlayerRemove(playerId);
				PlayerCreate(playerId, prefabId);
			}
		}

		// 更新玩家名字
		if (data.TryGetValue(MPKeys.PLAYER_NAME, out var playerName)) {
			if (Players.TryGetValue(playerId, out var container)) {
				container.UpdatePlayerName(playerName);
			}
		}

		// 更新玩家队伍并更新权限
		if (data.TryGetValue(MPKeys.TEAM, out var teamName)) {
			if (Players.TryGetValue(playerId, out var container) && container.team != teamName) {
				container.HandleTeamChanged(teamName);
			}
		}

		// 没有加入消息记录 播放加入消息
		if (data.TryGetValue(MPKeys.JOIN_MESSAGE, out var joinMessage)
			&& !joinMessages.Contains(playerId)) {
			joinMessages.Add(playerId);
			MPCore.SystemMessage(joinMessage, UIDisplayType.TipHeader);
		}

		// 更新离开信息
		if (data.TryGetValue(MPKeys.LEAVE_MESSAGE, out var leaveMessage))
			leaveMessages[playerId] = leaveMessage;
	}

	/// <summary>
	/// 处理玩家离开
	/// </summary>
	public void ProcessPlayerLeave(ulong playerId) {
		PlayerRemove(playerId);
		if (leaveMessages.TryGetValue(playerId, out var leaveMessage))
			MPCore.SystemMessage(leaveMessage, UIDisplayType.TipHeader);
		leaveMessages.Remove(playerId);
		joinMessages.Remove(playerId);
	}

	/// <summary>
	/// 处理玩家位置数据
	/// </summary>
	public void ProcessPlayerData(ulong playerId, ref PlayerData playerData) {
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
	public void ProcessPlayerDeath(ulong playerId, Dictionary<string, byte> remoteItems) {
		float currentTime = Time.time;

		if (_lastDeathTime.TryGetValue(playerId, out float lastTime)) {
			if (currentTime - lastTime < DEATH_COOLDOWN) {
				MPMain.LogWarning(Localization.Get("RPManager.PlayerDeathRateExceeded", playerId.ToString()));
				return;
			}
		}
		_lastDeathTime[playerId] = currentTime;

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

		MPCore.Instance.StartCoroutine(InstantiateItem());

		container.HandleDeath();

		if (container.actionRule.syncDied && ENT_Player.GetPlayer().IsDead() == false) {
			ENT_Player.GetPlayer().Kill("syncDied");
		}

		return;

		// 生成物品
		IEnumerator InstantiateItem() {
			int index = 0;
			foreach (var (itemId, count) in remoteItems) {
				if (!MPCore.IsInLobby)
					yield break;
				if (itemId == NO_ITEM_NAME)
					continue;
				if (itemId == HAMMER_NAME)
					continue;

				GameObject itemPrefab = CL_AssetManager.GetAssetGameObject(itemId);
				if (itemPrefab == null) {
					MPMain.LogError(Localization.Get("RPManager.PrefabDoesNotExist", itemId));
					continue;
				}

				for (int i = 0; i < count; i++) {
					// 随机位置 (-1~1,0.5~1,-1~1)
					Vector3 offset = new Vector3(
						Random.Range(-1f, 1f), Random.Range(0.5f, 1f), Random.Range(-1f, 1f));

					// 实例化物品
					var itemObject = GameObject.Instantiate(itemPrefab, playerPosition + offset, Random.rotation);

					// 获取Rigidbody并添加随机斜上方动量
					if (itemObject.TryGetComponent<Rigidbody>(out var rb)) {
						// 随机动量方向: (-1~1,1,-1~1)再归一化
						Vector3 direction = new Vector3(
							Random.Range(-1f, 1f), 1f, Random.Range(-1f, 1f)).normalized;
						// 添加冲量 力度(1-2)
						rb.AddForce(direction * Random.Range(1f + index * 0.05f, 2f + index * 0.1f), ForceMode.Impulse);
						// 可选: 添加随机旋转扭矩,让物品在空中旋转
						//rb.AddTorque(Random.insideUnitSphere * Random.Range(1f, 5f), ForceMode.Impulse);
					}
					index++;
					if ((index & 0b11) == 0b11) // index % 4 == 0
						yield return null;
				}
			}
		}
	}

	/// <summary>
	/// 处理玩家受伤
	/// </summary>
	public void ProcessPlayerDamage(ulong playerId, Damageable.DamageInfo info) {
		// 获取对应的远程玩家容器
		if (!Players.TryGetValue(playerId, out var container)) return;

		// 获取对应模型专属的数据配置
		ICustomModelExtension extension = RPFactoryManager.GetExtension(container.prefabId);

		// 决定采用什么受击特效资产名字
		string effectName = extension?.DamageEffectAssetName ?? MPAssetManager.DAMAGE_OBJECT_NAME;
		GameObject effectPrefab = MPAssetManager.GetAssetGameObject(effectName)
						   ?? CL_AssetManager.GetAssetGameObject(effectName);
		if (effectPrefab != null) {
			MPMain.Debug(info.position.ToString());
			if (info.position != new Vector3(0, 0, 0))
				GameObject.Instantiate(effectPrefab, info.position, Quaternion.identity);
			else
				GameObject.Instantiate(effectPrefab, container.PlayerObject.transform);
		}
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
	#region[工具函数]

	/// <summary>
	/// 根据距离将远程玩家列表分为两组, 实现视野内和视野外的分层同步
	/// 返回 ulong(玩家ID) 列表, 以便直接交给网络层调用 SendToPeer
	/// </summary>
	/// <param name="origin">计算距离的中心点(通常为本地玩家坐标)</param>
	/// <param name="distanceThreshold">距离阈值</param>
	/// <param name="farPlayers">输出: 距离 >= 阈值的玩家ID集合</param>
	/// <param name="nearPlayers">输出: 距离 < 阈值的玩家ID集合</param>
	public void GetPlayersByDistance(Vector3 origin, float distanceThreshold, ref List<IDType> farPlayers, ref List<IDType> nearPlayers) {
		farPlayers.Clear();
		nearPlayers.Clear();

		// 使用平方距离计算, 避免使用开销较大的 Vector3.Distance (开平方运算)
		float sqrThreshold = distanceThreshold * distanceThreshold;

		foreach (var kvp in Players) {
			IDType playerId = kvp.Key;
			RPContainer container = kvp.Value;

			// 安全检查: 忽略无效的远程玩家实体
			if (container == null || container.PlayerObject == null) {
				continue;
			}

			float sqrDistance = (container.PlayerObject.transform.position - origin).sqrMagnitude;
			if (sqrDistance >= sqrThreshold) {
				farPlayers.Add(playerId);
			} else {
				nearPlayers.Add(playerId);
			}
		}
	}

	/// <summary>
	/// 切换所有玩家的抓取/悬挂状态, 以适应不同的交互类型
	/// </summary>
	public void ChangeAllPlayerGrabOrHang(ENT_Player.InteractType interactType) {
		foreach (var (id, container) in Players) {
			container.ChangeGrabOrHang(interactType);
		}
	}

	/// <summary>
	/// 刷新所有玩家的规则引用, 以适应规则修改时的即时生效
	/// </summary>
	public void RefreshAllRule() {
		foreach (var (id, container) in Players)
			container.RefreshRuleReference();
	}

	public List<IDType> GetPlayerInTeam(string teamName) {
		var players = new List<IDType>();
		foreach (var (id, container) in Players) {
			if (container.team == teamName)
				players.Add(id);
		}
		return players;
	}

	#endregion

}