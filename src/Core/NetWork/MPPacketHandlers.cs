using Steamworks;
using Steamworks.Ugc;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using WKMPMod.Asset;
using WKMPMod.Component;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.RemotePlayer;
using WKMPMod.Util;
using WKMPMod.World;
using static WKMPMod.Core.MPGameModeManager;
using static WKMPMod.Data.MPWriterPool;
using static WKMPMod.UI.UI_Manager;
using static WKMPMod.Util.DictionaryExtensions;
using Random = UnityEngine.Random;


namespace WKMPMod.NetWork;

public class MPPacketHandlers {
	public const string NO_ITEM_NAME = "None";
	public const string HAMMER_NAME = "Item_Hammer";
	public const string ARTIFACT_NAME = "Artifact";

	/// <summary>
	/// 主机接收WorldInitRequest: 请求初始化数据
	/// 发送函数 <see cref="MPCore.InitHandshakeRoutine"/>
	/// 发送WorldInitData: 初始化数据给新玩家
	/// <see cref="HandleWorldInit"/>
	/// </summary>
	[MPPacketHandler(PacketType.WorldInitRequest)]
	private static void HandleWorldInitRequest(ulong senderId, DataReader reader) {
		var writer = GetWriter(MPSteamworks.Instance.UserSteamId, senderId, PacketType.WorldInitData);
		// 获取游戏模式数据 (是否铁指,是否困难,模式名称,模式对象名称,可能的种子)
		writer.Put(MPGameModeManager.CaptureCurrentData());
		// 发生到客户端
		MPSteamworks.Instance.SendToPeer(senderId, writer);
		// Debug
		MPMain.LogInfo(Localization.Get("MPMessageHandlers.SentInitData"));
	}

	/// <summary>
	/// 客户端接收WorldInitData: 新加入玩家,加载世界种子
	/// </summary>
	[MPPacketHandler(PacketType.WorldInitData)]
	private static void HandleWorldInit(ulong senderId, DataReader reader) {
		// 获取游戏模式数据 
		var gameModeData = reader.Get<OldGameModeData>();//[MP Debug]
		// 加载游戏模式
		MPGameModeManager.LoadGameMode(gameModeData);
		// 设置多人模式加载标签为完成
		MPCore.SetStatus(MPStatus.INIT_MASK, MPStatus.Initialized);
	}

	/// <summary>
	/// 主机/客户端接收PlayerDataUpdate: 处理玩家数据更新<br/>
	/// 发送函数 <see cref="LocalPlayer.TrySendLocalPlayerData"/><br/>
	/// 发送函数 <see cref="HandlePlayerCreateRequest"/>
	/// </summary>
	[MPPacketHandler(PacketType.PlayerDataUpdate)]
	private static void HandlePlayerDataUpdate(ulong senderId, DataReader reader) {
		// 如果是从转发给自己的,忽略
		reader.GetOut<PlayerData>(out var playerData);
		var playerId = playerData.playId;
		if (playerId == MPSteamworks.Instance.UserSteamId) {
			return;
		}
		RPManager.Instance.ProcessPlayerData(playerId,ref playerData);
	}

	/// <summary>
	/// 主机/客户端接收BroadcastMessage: 处理玩家标签更新
	/// </summary>
	[MPPacketHandler(PacketType.BroadcastMessage)]
	private static void HandlePlayerTagUpdate(ulong senderId, DataReader reader) {
		string msg = reader.GetString();    // 读取消息
		string playerName = new Friend(senderId).Name;
		CommandConsole.Log($"{playerName}: {msg}");
		RPManager.Instance.ProcessPlayerTag(senderId, msg);
	}

	/// <summary>
	/// 主机/客户端接收WorldStateSync: 世界状态同步
	/// </summary>
	[MPPacketHandler(PacketType.WorldStateSync)]
	private static void HandleWorldStateSync(ulong senderId, DataReader reader) {

	}

	/// <summary>
	/// 主机/客户端接收PitonStateSync: 同步实时放置的piton状态.
	/// </summary>
	[MPPacketHandler(PacketType.PitonStateSync)]
	private static void HandlePitonStateSync(ulong senderId, DataReader reader) {
		ClimbableItemSyncManager.HandlePitonState(senderId, reader);
	}

	/// <summary>
	/// 主机/客户端接收PlayerDamage: 受到伤害<br/>
	/// 发送函数: <see cref="MPCore.HandlePlayerDamage"/>
	/// </summary>
	[MPPacketHandler(PacketType.PlayerDamage)]
	private static void HandlePlayerDamage(ulong senderId, DataReader reader) {
		float amount = reader.GetFloat();
		string type = reader.GetString();
		List<string> tags = reader.GetStringList();
		ENT_Player.GetPlayer().Damage(Damageable.DamageInfo.CreateDamageInfo(amount, type,tags));
	}

	/// <summary>
	/// 主机/客户端接收PlayerAddForce: 受到冲击力<br/>
	/// 发送函数: <see cref="MPCore.HandlePlayerAddForce"/>
	/// </summary>
	[MPPacketHandler(PacketType.PlayerAddForce)]
	private static void HandlePlayerAddForce(ulong senderId, DataReader reader) {
		Vector3 force = new Vector3 {
			x = reader.GetFloat(),
			y = reader.GetFloat(),
			z = reader.GetFloat(),
		};
		string source = reader.GetString();
		ENT_Player.GetPlayer().AddForce(force, source);
	}

	/// <summary>
	/// 主机/客户端接收PlayerDeath: 玩家死亡
	/// </summary>
	[MPPacketHandler(PacketType.PlayerDeath)]
	private static void HandlePlayerDeath(ulong senderId, DataReader reader) {
		// 生成死亡消息
		string type = reader.GetString();
		string playerName = new Friend(senderId).Name;
		MPCore.SystemMessage(Localization.GetRandom("DisplayMessage.PlayerDeath", playerName, type), UIDisplayType.HighscoreHeader);

		// 获取玩家对象
		var playerObject = RPManager.Instance.GetPlayerObject(senderId);
		if (playerObject == null) {
			return;
		}

		// 生成死亡后掉落物品
		Dictionary<string, byte> remoteItems = reader.GetStringByteDict();
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
				// 随机位置 (-1~1,0~0.5,-1~1)
				Vector3 offset = new Vector3(
					Random.Range(-1f, 1f), Random.Range(0f, 0.5f), Random.Range(-1f, 1f));

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

		// 处理玩家死亡
		RPManager.Instance.ProcessPlayerDeath(senderId);
	}

	/// <summary>
	/// 主机/客户端接收PlayerCreateRequest<br/>
	/// 发送函数 <see cref="MPCore.CheckAndRepairPlayers"/><br/>
	/// 发送PlayerCreateResponse: 携带远程玩家工厂ID,让请求方创建远程玩家对象<br/>
	/// 发送PlayerDataUpdate: 强制同步玩家数据给新玩家,让新玩家更新远程玩家数据<br/>
	/// </summary>
	[MPPacketHandler(PacketType.PlayerCreateRequest)]
	private static void HandlePlayerCreateRequest(ulong senderId, DataReader reader) {
		var writer = GetWriter(MPSteamworks.Instance.UserSteamId, senderId, PacketType.PlayerCreateResponse);
		writer.Put(LocalPlayer.Instance.FactoryId);
		MPSteamworks.Instance.SendToPeer(senderId, writer);

		// 1秒后强制同步玩家数据,让新玩家更新远程玩家数据,因为有可能在创建玩家对象时,玩家数据还没有被同步过去
		MPCore.Instance.StartCoroutine(RoutineForceSyncDelay(1.0f));
		MPCore.Instance.StartCoroutine(RoutineForceSyncDelay(3.0f));
		MPCore.Instance.StartCoroutine(RoutineForceSyncDelay(9.0f));

		IEnumerator RoutineForceSyncDelay(float time) {
			yield return new WaitForSeconds(time);
			if (LocalPlayer.Instance != null) {
				LocalPlayer.Instance.ForceSyncToTarget(senderId);
			}
		}
	}

	/// <summary>
	/// 主机/客户端接收PlayerCreateResponse: 创建玩家对象<br/>
	/// 发送函数 <see cref="HandlePlayerCreateRequest"/><br/>
	/// </summary>
	[MPPacketHandler(PacketType.PlayerCreateResponse)]
	private static void HandlePlayerCreateResponse(ulong senderId, DataReader reader) {
		string factoryId = reader.GetString();
		RPManager.Instance.PlayerCreate(senderId, factoryId);
	}

	/// <summary>
	/// 主机/客户端接收PlayerTeleportRequest<br/>
	/// 发送PlayerTeleportRespond: 位置数据, 库存数据, 有Mess环境则携带Mess数据<br/>
	/// <see cref="MPPacketHandlers.HandlePlayerTeleportRespond(ulong, DataReader)"/>
	/// </summary>
	/// <param name="senderId">发送方ID</param>
	[MPPacketHandler(PacketType.PlayerTeleportRequest)]
	private static void HandlePlayerTeleportRequest(ulong senderId, DataReader reader) {
		// 获取数据
		var playerPos = ENT_Player.GetPlayer().transform.position;
		var writer = GetWriter(MPSteamworks.Instance.UserSteamId, senderId, PacketType.PlayerTeleportRespond);
		writer.Put(playerPos.x);
		writer.Put(playerPos.y);
		writer.Put(playerPos.z);

		// 库存物品字典
		writer.Put(MPCore.GetGetInventoryItems());

		// 没有Mess环境则直接发送位置数据,有则发送位置数据和Mess数据
		if (DEN_DeathFloor.instance == null) {
			writer.Put(false);
		} else {
			var deathFloorData = DEN_DeathFloor.instance.GetSaveData();
			writer.Put(true);
			writer.Put(deathFloorData.relativeHeight);
			writer.Put(deathFloorData.active);
			writer.Put(deathFloorData.speed);
			writer.Put(deathFloorData.speedMult);
		}
		MPSteamworks.Instance.SendToPeer(senderId, writer);
	}

	/// <summary>
	/// 主机/客户端接收PlayerTeleportRespond: 位置数据, 库存数据, 有Mess环境则携带Mess数据
	/// <see cref="MPPacketHandlers.HandlePlayerTeleportRequest(ulong, DataReader)"/>
	/// </summary>
	/// <param name="senderId">发送ID</param>
	[MPPacketHandler(PacketType.PlayerTeleportRespond)]
	private static void HandlePlayerTeleportRespond(ulong senderId, DataReader reader) {
		var posX = reader.GetFloat();
		var posY = reader.GetFloat();
		var posZ = reader.GetFloat();

		// 对方背包物品
		var remoteItems = reader.GetStringByteDict();
		var localItems = MPCore.GetGetInventoryItems();
		var missingItems = SetDifference(remoteItems, localItems);

		var inventory = Inventory.instance;
		foreach (var (itemId, count) in missingItems) {
			// 空物品ID或神器不生成
			if (itemId == NO_ITEM_NAME)
				continue;
			if (itemId.Contains(ARTIFACT_NAME))
				continue;

			GameObject itemPrefab = CL_AssetManager.GetAssetGameObject(itemId);
			if (itemPrefab == null) {
				MPMain.LogError(Localization.Get("MPMessageHandlers.PrefabDoesNotExist", itemId));
				continue;
			}

			for (int i = 0; i < count; i++) {
				// 实例化物品在 0,1,0 
				var pickupObj = GameObject.Instantiate(itemPrefab, new Vector3(0, 1, 0), Quaternion.identity);
				var itemObject = pickupObj.GetComponent<Item_Object>();
				var itemData = itemObject.itemData;
				if (itemObject != null) {
					// 通过.upDirection属性,摆正为竖直向上
					itemObject.itemData.bagRotation = Quaternion.LookRotation(itemData.upDirection);
					// 将物品放入背包
					inventory.AddItemToInventoryCenter(itemObject.itemData);
					// 隐藏镜像物品对象,因为它已经被添加到库存中,不需要在场景中显示
					itemObject.gameObject.SetActive(false);
				} else {
					MPMain.LogError(Localization.Get("MPMessageHandlers.PrefabIsNotItem", pickupObj.name));
					GameObject.Destroy(pickupObj);
					continue;
				}
			}
		}

		if (reader.GetBool()) {
			var deathFloorData = new DEN_DeathFloor.SaveData {
				relativeHeight = reader.GetFloat(),
				active = reader.GetBool(),
				speed = reader.GetFloat(),
				speedMult = reader.GetFloat(),
			};

			// 关闭可击杀效果
			DEN_DeathFloor.instance.SetCanKill(new string[] { "false" });
			// 重设计数器,期间位移视为传送
			LocalPlayer.Instance.TriggerTeleport();
			ENT_Player.GetPlayer().Teleport(new Vector3(posX, posY, posZ));
			DEN_DeathFloor.instance.LoadDataFromSave(deathFloorData);
			DEN_DeathFloor.instance.SetCanKill(new string[] { "true" });
		} else {
			// 重设计数器,期间位移视为传送
			LocalPlayer.Instance.TriggerTeleport();
			ENT_Player.GetPlayer().Teleport(new Vector3(posX, posY, posZ));
		}

	}
}
