using Steamworks;
using Steamworks.Ugc;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.XR;
using WKMPMod.Asset;
using WKMPMod.Component;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.Patch;
using WKMPMod.RemotePlayer;
using WKMPMod.UI;
using WKMPMod.Util;
using WKMPMod.World;
using static ENT_Player;
using static WKMPMod.Data.MPWriterPool;
using static WKMPMod.Data.PlayerData;
using static WKMPMod.UI.UI_Manager;
using static WKMPMod.Util.DictionaryExtensions;


namespace WKMPMod.NetWork;

public class MPPacketHandlers {
	/// <summary>
	/// 主机/客户端接收PlayerDataUpdate: 处理玩家数据更新<br/>
	/// 发送函数 <see cref="LocalPlayer.TrySendLocalPlayerData"/><br/>
	/// </summary>
	[MPPacketHandler(PacketType.PlayerDataUpdate)]
	private static void HandlePlayerDataUpdate(IDType senderId, DataReader reader) {
		// 如果是从转发给自己的,忽略
		reader.GetOut<PlayerData>(out var playerData);
		var playerId = playerData.playId;
		if (playerId == MPSteamworks.UserSteamId) {
			return;
		}

		RPManager.Instance.ProcessPlayerData(playerId, ref playerData);

		// 获取自定义额外数据
		if (reader.GetBool()) {
			var playerDictData = reader.GetStringStringDict();
			RPManager.Instance.ProcessPlayerCustomProperties(playerId, playerDictData);
		}
	}

	///// <summary>
	///// 主机/客户端接收RequestMemberData: 处理玩家本体数据请求<br/>
	///// 发送ResponseMemberData: 玩家本体数据字典<br/>
	///// 接受函数 <see cref="HandleResponseMemberData"/><br/>
	///// 发送函数 <see cref="MPCore.CheckAndRepairPlayers"/><br/>
	///// </summary>
	//[MPPacketHandler(PacketType.RequestMemberData)]
	//private static void HandleRequestMemberData(IDType senderId, DataReader reader) {
	//	var writer = MPWriterPool.GetWriter(MPSteamworks.UserSteamId, senderId, PacketType.ResponseMemberData);
	//	writer.Put(MPSteamworks.Instance.MemberData);
	//	MPSteamworks.Instance.SendToPeer(senderId, writer);
	//}

	///// <summary>
	///// 主机/客户端接收ResponseMemberData: 处理远程玩家数据响应<br/>
	///// 发送函数 <see cref="HandleRequestMemberData"/><br/>
	///// 发送函数 <see cref="MPSteamworks.SendAllMemberData"/><br/>
	///// </summary>
	//[MPPacketHandler(PacketType.ResponseMemberData)]
	//private static void HandleResponseMemberData(IDType senderId, DataReader reader) {
	//	var data = reader.GetStringStringDict();
	//	RPManager.Instance.ProcessMemberData(senderId, data);
	//}

	/// <summary>
	/// 主机/客户端接收BroadcastMessage: 处理玩家文字广播<br/>
	/// 发送函数: <see cref="MPCore.Talk"/>
	/// 发送函数: <see cref="HandleCheckRequest"/>
	/// </summary>
	[MPPacketHandler(PacketType.BroadcastMessage)]
	private static void HandleBroadcastMessage(IDType senderId, DataReader reader) {
		bool tagShow = reader.GetBool();    // 是否显示在Tag中
		string msg = reader.GetString();    // 读取消息
		CommandConsole.Log(msg);
		if (tagShow) RPManager.Instance.ProcessPlayerTagMessage(senderId, msg);
	}

	/// <summary>
	/// 主机/客户端接收WorldStateSync: 世界状态同步
	/// </summary>
	[MPPacketHandler(PacketType.WorldStateSync)]
	private static void HandleWorldStateSync(IDType senderId, DataReader reader) {

	}

	/// <summary>
	/// 主机/客户端接收PitonStateSync: 同步实时放置的piton状态.
	/// </summary>
	[MPPacketHandler(PacketType.PitonStateSync)]
	private static void HandlePitonStateSync(IDType senderId, DataReader reader) {
		ClimbableSyncModule.Instance.HandlePitonState(senderId, reader);
	}

	/// <summary>
	/// 主机/客户端接收PlayerDamage: 受到伤害<br/>
	/// 发送函数: <see cref="MPCore.HandlePlayerDamage"/>
	/// </summary>
	[MPPacketHandler(PacketType.PlayerDamage)]
	private static void HandlePlayerDamage(IDType senderId, DataReader reader) {
		float amount = reader.GetFloat();
		string type = reader.GetString();
		List<string> tags = reader.GetStringList();
		IDType source = reader.GetULong();

		if (RPManager.Instance.Players.TryGetValue(source, out var container)
			&& container?.RemoteEntities?.Length > 0) {
			ENT_Player.GetPlayer().Damage(Damageable.DamageInfo.CreateDamageInfo(amount, type, tags, container.RemoteEntities[0]));
		} else
			ENT_Player.GetPlayer().Damage(Damageable.DamageInfo.CreateDamageInfo(amount, type, tags));
	}

	/// <summary>
	/// 主机/客户端接收PlayerAddForce: 受到冲击力<br/>
	/// 发送函数: <see cref="MPCore.HandlePlayerAddForce"/>
	/// </summary>
	[MPPacketHandler(PacketType.PlayerAddForce)]
	private static void HandlePlayerAddForce(IDType senderId, DataReader reader) {
		Vector3 force = new Vector3 {
			x = reader.GetFloat(),
			y = reader.GetFloat(),
			z = reader.GetFloat(),
		};
		string source = reader.GetString();
		ENT_Player.GetPlayer().AddForce(force, source);
	}

	/// <summary>
	/// 主机/客户端接收PlayerDeath: 玩家死亡<br/>
	/// 发送函数: <see cref="MPCore.HandlePlayerDeath"/>
	/// </summary>
	[MPPacketHandler(PacketType.PlayerDeath)]
	private static void HandlePlayerDeath(IDType senderId, DataReader reader) {
		// 掉落物品
		Dictionary<string, byte> remoteItems = reader.GetStringByteDict();
		// 处理玩家死亡
		RPManager.Instance.ProcessPlayerDeath(senderId, remoteItems);
	}

	///// <summary>
	///// 主机/客户端接收PlayerCreateRequest<br/>
	///// 发送函数 <see cref="MPCore.CheckAndRepairPlayers"/><br/>
	///// 发送PlayerCreateResponse: 携带远程玩家工厂ID,让请求方创建远程玩家对象<br/>
	///// 发送PlayerDataUpdate: 强制同步玩家数据给新玩家,让新玩家更新远程玩家数据<br/>
	///// </summary>
	//[MPPacketHandler(PacketType.PlayerCreateRequest)]
	//private static void HandlePlayerCreateRequest(IDType senderId, DataReader reader) {
	//	var writer = GetWriter(MPSteamworks.UserSteamId, senderId, PacketType.PlayerCreateResponse);
	//	writer.Put(LocalPlayer.Instance.FactoryId);
	//	MPSteamworks.Instance.SendToPeer(senderId, writer);

	//	// 1秒后强制同步玩家数据,让新玩家更新远程玩家数据,因为有可能在创建玩家对象时,玩家数据还没有被同步过去
	//	MPCore.Instance.StartCoroutine(RoutineMultiSync(new float[] { 1f, 3f, 9f, 9f }));

	//	IEnumerator RoutineMultiSync(float[] delays) {
	//		foreach (float waitTime in delays) {
	//			if (waitTime > 0) 
	//				yield return new WaitForSeconds(waitTime);
	//			if (!MPCore.IsInLobby || LocalPlayer.Instance == null) 
	//				yield break; // 停止协程, 防止对不存在的玩家发包
	//			if (LocalPlayer.Instance != null) 
	//				LocalPlayer.Instance.ForceSyncToTarget(senderId);
	//		}
	//	}
	//}

	///// <summary>
	///// 主机/客户端接收PlayerCreateResponse: 创建玩家对象<br/>
	///// 发送函数 <see cref="HandlePlayerCreateRequest"/><br/>
	///// </summary>
	//[MPPacketHandler(PacketType.PlayerCreateResponse)]
	//private static void HandlePlayerCreateResponse(IDType senderId, DataReader reader) {
	//	string factoryId = reader.GetString();
	//	RPManager.Instance.PlayerCreate(senderId, factoryId);
	//}

	/// <summary>
	/// 主机/客户端接收SystemUIMessage: 显示文字在游戏内UI<br/>
	/// </summary>
	/// <param name="senderId">发送方ID</param>
	[MPPacketHandler(PacketType.GameUIMessage)]
	private static void HandleSystemUIMessage(IDType senderId, DataReader reader) {
		var message = reader.GetString();
		var displayType = reader.GetByte();
		var duration = reader.GetFloat();
		var logToConsole = reader.GetBool();
		if (logToConsole)
			MPCore.SystemMessage(message, (UIDisplayType)displayType, duration);
		else
			UI_Manager.DisplayMessage(message, (UIDisplayType)displayType, duration);
	}

	/// <summary>
	/// 主机/客户端接收PlayerTeleportRequest<br/>
	/// 发送PlayerTeleportRespond: 位置数据, 库存数据, 有Mess环境则携带Mess数据<br/>
	/// 接受函数 <see cref="HandlePlayerTeleportRespond"/>
	/// </summary>
	/// <param name="senderId">发送方ID</param>
	[MPPacketHandler(PacketType.PlayerTeleportRequest)]
	private static void HandlePlayerTeleportRequest(IDType senderId, DataReader reader) {
		// 获取数据
		var playerPos = ENT_Player.GetPlayer().transform.position;
		var writer = GetWriter(MPSteamworks.UserSteamId, senderId, PacketType.PlayerTeleportRespond);
		writer.Put(playerPos.x);
		writer.Put(playerPos.y);
		writer.Put(playerPos.z);

		// 库存物品字典
		writer.Put(InventoryManager.GetBlacklistInventoryItems(
			new string[] { InventoryManager.ARTIFACT, InventoryManager.TRINKET }));

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
	/// 发送函数 <see cref="HandlePlayerTeleportRequest"/>
	/// </summary>
	/// <param name="senderId">发送ID</param>
	[MPPacketHandler(PacketType.PlayerTeleportRespond)]
	private static void HandlePlayerTeleportRespond(IDType senderId, DataReader reader) {
		var posX = reader.GetFloat();
		var posY = reader.GetFloat();
		var posZ = reader.GetFloat();

		// 对方背包物品
		var remoteItems = reader.GetStringByteDict();
		var localItems = InventoryManager.GetInventoryItems();
		var missingItems = SetDifference(remoteItems, localItems);

		var inventory = Inventory.instance;
		foreach (var (itemPrefabName, count) in missingItems) {
			// 获取预制体
			if(!MPUtil.TryGetItemPrefab(itemPrefabName, out var itemObjectPrefab)) continue;

			for (int i = 0; i < count; i++) {
				// 实例化物品在 0,1,0 
				var itemObject = GameObject.Instantiate(itemObjectPrefab, new Vector3(0, 1, 0), Quaternion.identity);
				var itemData = itemObject.itemData;
				// 通过.upDirection属性,摆正为竖直向上
				itemData.bagRotation = Quaternion.LookRotation(itemData.upDirection);
				// 将物品放入背包
				inventory.AddItemToInventoryCenter(itemData);
				// 隐藏镜像物品对象,因为它已经被添加到库存中,不需要在场景中显示
				itemObject.gameObject.SetActive(false);

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

	/// <summary>
	/// 主机/客户端接收ItemStateSync: 通过物品同步管理器来进行物品同步
	/// </summary>
	[MPPacketHandler(PacketType.ItemStateSync)]
	private static void HandleItemStateSync(IDType senderId, DataReader reader) {
		ItemSyncManager.HandleItemState(senderId, reader);
	}

	/// <summary>
	/// 主机/客户端接收EnemyStateSync: 同步敌人位置、生命值、伤害请求和死亡状态
	/// </summary>
	[MPPacketHandler(PacketType.EnemyStateSync)]
	private static void HandleEnemyStateSync(IDType senderId, DataReader reader) {
		EnemySyncModule.Instance.HandleEnemyState(senderId, reader);
	}

	/// <summary>
	/// 主机/客户端接收PlayerStopInteraction: 处理远程玩家松开物品或手抓点<br/>
	/// </summary>
	[MPPacketHandler(PacketType.PlayerStopInteraction)]
	private static void HandlePlayerStopInteraction(IDType senderId, DataReader reader) {
		var hands = ENT_Player.GetPlayer().hands;
		for (int i = 0; i < hands.Length; i++) {
			var hand = hands[i];
			if (hand.interactState == InteractType.none) {
				continue;
			}
			if (hand.interactState == InteractType.grab
				&& hand.grabTarget?.gameObject.TryGetComponent<RPContainerRef>(out var parent) == true
				&& senderId == parent.container.PlayerId) {
				DropIt(i);
			}
			if (hand.interactState == InteractType.hanging
				&& hand.handhold?.gameObject.TryGetComponent<RPContainerRef>(out var hangingParent) == true
				&& senderId == hangingParent.container.PlayerId) {
				DropIt(i);
			}
		}

		void DropIt(int handIndex) {
			var _cachedPlayer = ENT_Player.GetPlayer();
			_cachedPlayer.StopInteraction(handIndex);
			_cachedPlayer.AddForce(-_cachedPlayer.camTransform.forward, "RepelByRemote");
		}
	}

	/// <summary>
	/// 客户端接收RemoteCommand: 处理指令远程调用<br/>
	/// </summary>
	[MPPacketHandler(PacketType.RemoteCommand)]
	public static void HandleRemoteCommand(IDType senderId, DataReader reader) {
		string command = reader.GetString();
		CommandConsole.Log(Localization.Get("CommandConsole.PlayerIssuedCommand", new Friend(senderId).Name, command));
		Patch_CommandConsole.ExecuteCommandForcefully(command);
	}

	/// <summary>
	/// 客户端接收PlayerCheckRequest: 检查玩家数据
	/// 发送BroadcastMessage: 玩家本体数据字典<br/>
	/// 接受函数 <see cref="HandleBroadcastMessage"/><br/>
	/// </summary>
	[MPPacketHandler(PacketType.PlayerCheckRequest)]
	public static void HandleCheckRequest(IDType senderId, DataReader reader) {
		string checkRequest = reader.GetString();
		var player = ENT_Player.GetPlayer();
		string data = checkRequest switch {
			"inventory" => "item: {" + GetInventoryItems() + "}",
			"perk" => "perk: {" + GetPerks() + "}",
			"stamina" => $"left: {player.hands[0].gripStrength} right: {player.hands[1].gripStrength}",
			"health" => "health: " + player.health,
			"cheats" => "cheats: " + CommandConsole.cheatsEnabled.ToString(),
			_ => "",
		};

		var writer = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.BroadcastMessage);
		writer.Put(false);
		writer.Put(data);
		MPSteamworks.Instance.SendToPeer(senderId, writer);

		// 获取物品函数
		string GetInventoryItems() {
			var inventory = Inventory.instance;
			var itemsDict = new Dictionary<string, byte>();

			if (inventory == null)
				return "";
			else {
				// 获取库存中的物品列表
				var items = inventory.GetItems();
				foreach (var item in items) {
					itemsDict.TryAdd(item.prefabName, 0);
					itemsDict[item.prefabName]++;
				}
			}
			return string.Join(",", itemsDict.Select((item, number) => $"{item}"));
		}
		// 获取perk函数
		string GetPerks() {
			var perks = player.perks;
			var perksDict = new Dictionary<string, byte>();
			if (perks == null)
				return "";
			else {
				// 获取库存中的物品列表
				foreach (var perk in perks) {
					perksDict.TryAdd(perk.id, 0);
					perksDict[perk.id]++;
				}
			}
			return string.Join(",", perksDict.Select((perk, number) => $"{perk}: {number.ToString()}, "));
		}
	}
}

