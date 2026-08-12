using Steamworks;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
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

	private TickTimer _debugTick = new TickTimer(5f);
	// 存储所有远程对象
	internal Dictionary<IDType, RPContainer> Players = new Dictionary<IDType, RPContainer>();
	private Dictionary<IDType, float> _lastDeathTime = new Dictionary<IDType, float>();
	// 同一个玩家 2 秒内只能处理一次死亡信息, 避免重复处理导致的物品重复掉落和动画冲突
	private const float DEATH_COOLDOWN = 2.0f;
	// 根对象引用
	private Transform _remotePlayersRoot;
	// 加入信息和离开信息
	private HashSet<IDType> _joinMessageShown = new HashSet<IDType>();

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
			container.Destroy();
		}
		Players.Clear();
		_joinMessageShown.Clear();
		RPFactoryManager.Instance.ClearPrefabCache();
	}

	#endregion

	#region[容器创建/销毁]

	/// <summary>
	/// 获取或创建玩家容器(空壳)
	/// 容器本身不包含模型, 调用 EnsureModel 才会真正加载 GameObject
	/// </summary>
	private RPContainer GetOrCreateContainer(IDType playerId) {
		if (!Players.TryGetValue(playerId, out var container)) {
			container = new RPContainer(playerId);
			Players[playerId] = container;
		}
		return container;
	}

	/// <summary>
	/// 确保容器持有与 prefabId 匹配的模型
	/// </summary>
	private bool EnsureModel(RPContainer container, string prefabId) {
		// 有旧模型先销毁
		if (container.PlayerObject != null) container.Destroy();

		// 从工厂拿新实例
		GameObject instance = RPFactoryManager.Instance.Create(prefabId);
		if (instance == null) {
			MPMain.LogError(Localization.Get("RPManager.FactoryCreateObjectFailed"));
			return false;
		}

		// 通知容器接管并触发 FlushPendingData
		return container.Initialize(instance, _remotePlayersRoot);
	}

	/// <summary>
	/// 移除玩家(触发死亡特效 + 工厂回收 + 容器销毁)
	/// </summary>
	public void PlayerRemove(ulong playerId) {
		if (!Players.TryGetValue(playerId, out var container)) return;

		// 生成死亡特效(仅模型存在时)
		if (container.PlayerObject != null) {
			var pos = container.PlayerObject.transform.position;
			var rot = container.PlayerObject.transform.rotation;
			var deathParticle = MPAssetManager.GetFXPrefab(MPAssetManager.DEATH_OBJECT_NAME);
			if (deathParticle != null) GameObject.Instantiate(deathParticle, pos, rot);
		}

		container.Destroy();
		Players.Remove(playerId);
	}

	#endregion

	#region[处理消息]

	/// <summary>
	/// 处理玩家数据字典
	/// </summary>
	public void ProcessMemberData(IDType playerId, Dictionary<string, string> data) {
		MPMain.LogTest($"[RPMan] member data: {string.Join(",", data.Select(kvp => kvp.Key + ": " + kvp.Value))}");

		RPContainer container = GetOrCreateContainer(playerId);

		// 数据解析完全交给容器, Manager 只关心"要不要重建模型"
		bool needModel = container.ApplyMemberData(data);
		if (needModel) EnsureModel(container, container.prefabId);

		// 模型已生成 && 没有显示过加入信息
		if (container.IsModelReady && !_joinMessageShown.Contains(playerId)) {
			var msg = container.GetJoinMessage();
			if (msg != null) {
				_joinMessageShown.Add(playerId);
				MPCore.SystemMessage(msg, UIDisplayType.TipHeader);
			}
		}
	}

	/// <summary>
	/// 处理玩家离开
	/// </summary>
	public void ProcessPlayerLeave(IDType playerId) {
		if (Players.TryGetValue(playerId, out var container)) {
			var leaveMsg = container.GetLeaveMessage();
			if (leaveMsg != null)
				MPCore.SystemMessage(leaveMsg, UIDisplayType.TipHeader);
		}
		PlayerRemove(playerId);
		_joinMessageShown.Remove(playerId);
	}

	/// <summary>
	/// 处理玩家位置数据
	/// </summary>
	public void ProcessPlayerData(IDType playerId, ref PlayerData playerData) {
		if (!MPCore.IsInitialized || !MPCore.IsInLobby) return;

		// 以后加上时间戳处理
		if (Players.TryGetValue(playerId, out var container)) {
			if (!container.IsModelReady) return;
			container.HandlePlayerData(ref playerData);
		} else if (_debugTick.TryTick()) {
			MPMain.LogError(Localization.Get(
				"RPManager.RemotePlayerObjectNotFound", playerId.ToString()));
		}
	}

	public void ProcessPlayerDictData(IDType playerId, Dictionary<string, string> playerData) {
		if (!MPCore.IsInitialized || !MPCore.IsInLobby) return;

		// 以后加上时间戳处理
		if (Players.TryGetValue(playerId, out var container)) {
			if (!container.IsModelReady) return;
			container.HandlePlayerDictData(playerData);
		} else if (_debugTick.TryTick()) {
			MPMain.LogError(Localization.Get(
				"RPManager.RemotePlayerObjectNotFound", playerId.ToString()));
		}
	}

	/// <summary>
	/// 处理玩家数据
	/// </summary>
	public void ProcessPlayerTag(IDType playerId, string massage) {
		// 以后加上时间戳处理
		if (Players.TryGetValue(playerId, out var RPcontainer)) {
			RPcontainer.HandleNameTag(massage);
			return;
		}
		MPMain.LogError(Localization.Get(
			"RPManager.RemotePlayerObjectNotFound", playerId.ToString()));
	}

	/// <summary>
	/// 处理玩家死亡
	/// </summary>
	public void ProcessPlayerDeath(IDType playerId, Dictionary<string, byte> remoteItems) {
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
		if (!Players.TryGetValue(playerId, out var container)) return;

		var playerObject = container.PlayerObject;
		if (playerObject == null) return;
		
		var playerPosition = playerObject.transform.position;

		// 生成物品
		MPCore.Instance.StartCoroutine(InstantiateItem());

		// 处理死亡特效
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

				// 获取预制体
				if (!MPUtil.TryGetItemPrefab(itemId, out var itemObjectPrefab)) continue;

				for (int i = 0; i < count; i++) {
					// 随机位置 (-1~1,0.5~1,-1~1)
					Vector3 offset = new Vector3(Random.Range(-1f, 1f), Random.Range(0.5f, 1f), Random.Range(-1f, 1f));

					// 实例化物品
					var itemObject = GameObject.Instantiate(itemObjectPrefab, playerPosition + offset, Random.rotation);

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
	public void ProcessPlayerDamage(IDType playerId, Damageable.DamageInfo info) {
		// 获取对应的远程玩家容器
		if (!Players.TryGetValue(playerId, out var container)) return;
		container.HandleDamage(info);
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
		foreach (var (id, container) in Players) container.RefreshRuleReference();
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
