using LiteNetLib.Utils;
using Steamworks.Data;
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using WKMultiMod.src.Data;
using WKMultiMod.src.NetWork;
using WKMultiMod.src.Util;
using static WKMultiMod.src.Data.PlayerData;

namespace WKMultiMod.src.Core;

//仅获取本地玩家信息并触发事件给其他系统使用
//仅在联机时创建一个实例
public class LocalPlayerManager: MonoBehaviour {
	private float _lastSendTime;

	// Debug日志输出间隔
	private TickTimer _debugTick = new TickTimer(5f);

	void Update() {
		// 没有开启多人时停止更新
		if (MPCore.IsMultiplayerActive == false)
			return;
		// 限制发送频率(20Hz)
		if (Time.time - _lastSendTime < 0.05f)
			return;
		_lastSendTime = Time.time;
		// 没有链接时停止更新
		if (!MPCore.Instance.Steamworks.HasConnections)
			return;



		// 创建玩家数据
		var playerData = CreateLocalPlayerData(MPCore.steamId);

		//// Debug
		//if(_debugTick.Test()){
		//	MPMain.Logger.LogInfo($"[LPManager] 发送数据 " +
		//		$"Player.Position: {playerData.Position.ToString()} " +
		//		$"Player.Rotation: {playerData.Rotation.ToString()} " +
		//		$"LeftHand.isFree: {playerData.LeftHand.IsFree.ToString()} " +
		//		$"LeftHand: {playerData.LeftHand.Position.ToString()} " +
		//		$"RightHand.isFree: {playerData.RightHand.IsFree.ToString()} " +
		//		$"RightHand: {playerData.RightHand.Position.ToString()} ");
		//}

		if (playerData == null) {
			MPMain.Logger.LogError("[LPMan] 本地玩家信息异常");
			return;
		}

		//// Debug
		//playerData.IsTeleport = true;

		// 进行数据写入
		NetDataWriter writer = new NetDataWriter();
		writer.Put((int)PacketType.PlayerDataUpdate);
		MPDataSerializer.WriteToNetData(writer, playerData);

		// 不通过总控模块直写
		// 触发Steam数据发送
		// 转为byte[]
		// 使用不可靠+立即发送
		SteamNetworkEvents.TriggerBroadcast(
			MPDataSerializer.WriterToBytes(writer),
			SendType.Unreliable | SendType.NoNagle);
	}

	/// <summary>
	/// 创建一个玩家数据
	/// </summary>
	/// <param name="Id"></param>
	/// <returns></returns>
	public static PlayerData CreateLocalPlayerData(ulong Id) {
		var player = ENT_Player.GetPlayer();
		if (player == null) return null;

		var data = new PlayerData {
			playId = Id,
			TimestampTicks = DateTime.UtcNow.Ticks
		};

		// 位置和旋转
		data.Position = player.transform.position;
		data.Rotation = player.transform.rotation;

		// 手部数据
		data.LeftHand = GetHandData(player.hands[(int)HandType.Left]);
		data.RightHand = GetHandData(player.hands[(int)HandType.Right]);

		return data;
	}

	/// <summary>
	/// 获取手部数据
	/// </summary>
	/// <param name="hand"></param>
	/// <returns></returns>
	private static HandData GetHandData(ENT_Player.Hand hand) {
		var handData = new HandData();
		handData.IsFree = hand.IsFree();

		if (!handData.IsFree) {
			//handData.Position = hand.GetHoldPosition();
			handData.Position = hand.GetHoldWorldPosition();
		}

		return handData;
	}
}

