using Steamworks;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using WKMPMod.Components;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.Patch;
using WKMPMod.RemotePlayers;
using WKMPMod.UI;
using WKMPMod.Util;
using WKMPMod.World;
using static ENT_Player;
using static WKMPMod.Data.MPWriterPool;
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
		if (playerId == MPSteamworks.UserSteamId) return;

		RPManager.Instance.ProcessPlayerData(playerId, ref playerData);

		// 获取自定义额外数据
		if (reader.GetBool()) {
			var extraPlayerData = reader.GetStringStringDict();
			RPManager.Instance.ProcessExtraPlayerData(playerId, extraPlayerData);
		}
	}

	/// <summary>
	/// 主机/客户端接收MemberDataMessage: 请求/相应玩家数据<br/>
	/// 发送函数 <see cref="MPCore.CheckAndRepairPlayers"/><br/>
	/// 发送函数 <see cref="HandleMemberDataMessage"/><br/>
	/// </summary>
	[MPPacketHandler(PacketType.MemberDataMessage)]
	private static void HandleMemberDataMessage(IDType senderId, DataReader reader) {
		if (senderId == MPSteamworks.UserSteamId) return;
		var isResponse = reader.GetBool();
		if (!isResponse) {
			// 请求包
			var writer = GetWriter(MPSteamworks.UserSteamId, senderId, PacketType.MemberDataMessage);
			writer.Put(true);
			writer.Put(MPSteamworks.Instance.GetAllMemberData());
			writer.Put(LocalPlayer.Instance.LastPlayerData);
			writer.Put(LocalPlayer.Instance.extraPlayerData);
			MPSteamworks.Instance.SendToPeer(senderId, writer);
		} else {
			// 响应包
			var memberData = reader.GetStringStringDict();
			reader.GetOut<PlayerData>(out var playerData);
			var extraPlayerData = reader.GetStringStringDict();
			var playerId = playerData.playId;

			RPManager.Instance.ProcessMemberData(senderId, memberData);
			RPManager.Instance.ProcessPlayerData(playerId, ref playerData);
			RPManager.Instance.ProcessExtraPlayerData(playerId, extraPlayerData);
		}
	}

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
	[MPPacketHandler(PacketType.ClimbableSync)]
	private static void HandlePitonStateSync(IDType senderId, DataReader reader) {
		ClimbableSyncModule.Instance.HandlePitonState(senderId, reader);
	}

	/// <summary>
	/// 主机/客户端接收PlayerDamage: 受到伤害<br/>
	/// 发送函数: <see cref="MPCore.HandlePlayerDamage"/>
	/// 具体实现: <see cref="RemotePlayer.Damage"/>
	/// </summary>
	[MPPacketHandler(PacketType.PlayerDamage)]
	private static void HandlePlayerDamage(IDType senderId, DataReader reader) {
		float amount = reader.GetFloat();
		string type = reader.GetString();
		List<string> tags = reader.GetStringList();
		IDType source = reader.GetULong();

		// 玩家造成的伤害 && 存在玩家
		if (tags.Contains("player") && RPManager.Instance.Players.TryGetValue(source, out var container) && container?.remotePlayer != null) {
			GetPlayer().Damage(Damageable.DamageInfo.CreateDamageInfo(amount, type, tags, container.remotePlayer));
			// 有远程生物伤害标签 && 有对应ID 
		} else if (tags.Contains(MPKeys.REMOTE_ENEMY_DAMAGE_TAG)
					&& tags.FirstOrDefault(t => t.StartsWith("NetEnemyId:", StringComparison.Ordinal)) is string idTag
					&& ulong.TryParse(idTag.AsSpan(11), out ulong networkId)) {
			// 存在对应ID生物 && 生物被网络接管 && 本地未死亡
			if (EnemySyncModule.Instance.enemies.TryGetValue(networkId, out var identity)
				&& identity.IsNetworkControlled && !identity.Entity.dead) {
				GetPlayer().Damage(Damageable.DamageInfo.CreateDamageInfo(amount, type, tags, identity.Entity));
			}
		} else {
			GetPlayer().Damage(Damageable.DamageInfo.CreateDamageInfo(amount, type, tags));
		}
	}

	/// <summary>
	/// 主机/客户端接收PlayerAddForce: 受到冲击力<br/>
	/// 发送函数: <see cref="MPCore.HandlePlayerAddForce"/>
	/// 具体实现: <see cref="RemotePlayer.AddForce"/>
	/// </summary>
	[MPPacketHandler(PacketType.PlayerAddForce)]
	private static void HandlePlayerAddForce(IDType senderId, DataReader reader) {
		Vector3 force = reader.GetVector3();
		string source = reader.GetString();
		// 其他玩家造成的冲击力
		if (!source.StartsWith("NetEnemyId:", StringComparison.Ordinal)) {
			GetPlayer().AddForce(force, source);
			return;
		}
		// 有远程生物伤害标签 && 有对应ID && 存在对应ID生物 && 生物被网络接管 && 本地未死亡
		if (source.StartsWith("NetEnemyId:", StringComparison.Ordinal)
			&& ulong.TryParse(source.AsSpan(11), out ulong networkId)
			&& EnemySyncModule.Instance.enemies.TryGetValue(networkId, out var identity)
			&& identity.IsNetworkControlled && !identity.Entity.dead) {
			GetPlayer().AddForce(force, identity.Entity.name);
			return;
		}
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
	/// 主机/客户端接收 PlayerTeleportMessage: 请求/响应玩家传送与数据同步<br/>
	/// 发送函数 <see cref="MPCore.TpToPlayer"/><br/>
	/// 发送函数 <see cref="HandlePlayerTeleportMessage"/><br/>
	/// </summary>
	/// <param name="senderId">发送方ID</param>
	[MPPacketHandler(PacketType.PlayerTeleportMessage)]
	private static void HandlePlayerTeleportMessage(IDType senderId, DataReader reader) {
		if (senderId == MPSteamworks.UserSteamId) return;

		var isResponse = reader.GetBool();

		if (!isResponse) {
			// 请求包处理
			var playerPos = GetPlayer().transform.position;
			var writer = GetWriter(MPSteamworks.UserSteamId, senderId, PacketType.PlayerTeleportMessage);
			// 写入响应标志
			writer.Put(true);
			// 写入位置数据
			writer.Put(playerPos);
			// 写入库存物品字典
			writer.Put(InventoryManager.GetBlacklistInventoryItems(
				new string[] { InventoryManager.ARTIFACT, InventoryManager.TRINKET }));

			// 没有 Mess 环境则直接发送位置数据, 有则发送位置数据和 Mess 数据
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
		} else {
			// 响应包处理
			var pos = reader.GetVector3();

			// 对方背包物品补全处理
			var remoteItems = reader.GetStringByteDict();
			var localItems = InventoryManager.GetInventoryItems();
			var missingItems = SetDifference(remoteItems, localItems);

			var inventory = Inventory.instance;
			foreach (var (itemPrefabName, count) in missingItems) {
				// 获取预制体
				if (!MPUtil.TryGetItemPrefab(itemPrefabName, out var itemObjectPrefab)) continue;

				for (int i = 0; i < count; i++) {
					// 实例化物品在 0,1,0 
					var itemObject = GameObject.Instantiate(itemObjectPrefab, new Vector3(0, 1, 0), Quaternion.identity);
					var itemData = itemObject.itemData;
					// 通过 .upDirection 属性, 摆正为竖直向上
					itemData.bagRotation = Quaternion.LookRotation(itemData.upDirection);
					// 将物品放入背包
					inventory.AddItemToInventoryCenter(itemData);
					// 隐藏镜像物品对象, 因为它已经被添加到库存中, 不需要在场景中显示
					itemObject.gameObject.SetActive(false);
				}
			}

			// Mess 环境数据解析与传送
			if (reader.GetBool()) {
				var deathFloorData = new DEN_DeathFloor.SaveData {
					relativeHeight = reader.GetFloat(),
					active = reader.GetBool(),
					speed = reader.GetFloat(),
					speedMult = reader.GetFloat(),
				};

				// 关闭可击杀效果
				DEN_DeathFloor.instance.SetCanKill(new string[] { "false" });
				// 重设计数器, 期间位移视为传送
				LocalPlayer.Instance.TriggerTeleport();
				GetPlayer().Teleport(pos);
				DEN_DeathFloor.instance.LoadDataFromSave(deathFloorData);
				DEN_DeathFloor.instance.SetCanKill(new string[] { "true" });
			} else {
				// 重设计数器, 期间位移视为传送
				LocalPlayer.Instance.TriggerTeleport();
				GetPlayer().Teleport(pos);
			}
		}
	}

	/// <summary>
	/// 主机/客户端接收ItemStateSync: 通过物品同步管理器来进行物品同步
	/// </summary>
	[MPPacketHandler(PacketType.SceneItemSync)]
	private static void HandleSceneItemStateSync(IDType senderId, DataReader reader) {
		SceneItemModule.Instance.HandleItemState(senderId, reader);
	}

	/// <summary>
	/// 主机/客户端接收ItemStateSync: 通过物品同步管理器来进行物品同步
	/// </summary>
	[MPPacketHandler(PacketType.DroppedItemSync)]
	private static void HandleDroppedItemStateSync(IDType senderId, DataReader reader) {
		DroppedItemModule.Instance.HandleItemState(senderId, reader);
	}

	/// <summary>
	/// 主机/客户端接收EnemyStateSync: 同步敌人位置, 生命值, 伤害请求和死亡状态
	/// </summary>
	[MPPacketHandler(PacketType.EnemySync)]
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
	private static void HandleRemoteCommand(IDType senderId, DataReader reader) {
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
	private static void HandleCheckRequest(IDType senderId, DataReader reader) {
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

