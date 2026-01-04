using LiteNetLib.Utils;
using Steamworks.Data;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using UnityEngine;
using WKMultiMod.src.Data;
using WKMultiMod.src.NetWork;
using WKMultiMod.src.Util;
using static WKMultiMod.src.Data.PlayerData;

namespace WKMultiMod.src.Core;

//仅获取本地玩家信息并触发事件给其他系统使用
//仅在联机时创建一个实例
public static class LocalPlayerManager{

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

		//handData.Position = hand.GetHoldPosition();
		handData.Position = hand.GetHoldWorldPosition();
		
		return handData;
	}
}

