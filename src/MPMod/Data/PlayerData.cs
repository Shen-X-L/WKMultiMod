using LiteNetLib.Utils;
using Steamworks;
using System;
using UnityEngine;
using static WKMultiMod.src.Data.PlayerData;

namespace WKMultiMod.src.Data;



[System.Serializable]
public class PlayerData {
	public enum HandType { Left = 0, Right = 1 }

	// 玩家ID
	public ulong playId;
	// 时间戳(网络同步关键)
	public long TimestampTicks;
	// 位置和旋转(直接用float字段)
	public float PosX, PosY, PosZ;
	public float RotX, RotY, RotZ, RotW;
	// 手部数据
	public HandData LeftHand;
	public HandData RightHand;
	// 特殊标志
	public bool IsTeleport;

	// PlayerId(8) + TimestampTicks(8) + 位置(12) + 旋转(16) + 
	// 左手(12) + 右手(12) + IsTeleport(1)
	// 包长度
	public static int CalculateSize => 8 + 8 + 12 + 16 + 12 + 12 + 1;

	public Vector3 Position {
		get => new Vector3(PosX, PosY, PosZ); // 直接返回,无 GC 压力
		set {
			PosX = value.x; PosY = value.y; PosZ = value.z;
		}
	}

	public Quaternion Rotation {
		get => new Quaternion(RotX, RotY, RotZ, RotW); // 永远返回当前字段的真实值
		set {
			RotX = value.x; RotY = value.y; RotZ = value.z; RotW = value.w;
		}
	}

	public DateTime Timestamp {
		get => new DateTime(TimestampTicks);
		set => TimestampTicks = value.Ticks;
	}

	// 构造函数
	public PlayerData() {
		LeftHand = new HandData { handType = HandType.Left };
		RightHand = new HandData { handType = HandType.Right };
	}
}

[System.Serializable]
public struct HandData {
	// 手部类型
	public PlayerData.HandType handType;
	// 位置
	public float PosX;
	public float PosY;
	public float PosZ;

	public Vector3 Position {
		get => new Vector3(PosX, PosY, PosZ);
		set {
			PosX = value.x; PosY = value.y; PosZ = value.z;
		}
	}
}


// 封装的读取方法
public static class MPDataSerializer {

	/// <summary>
	/// 序列化到NetDataWriter (无数据包类型)
	/// </summary>
	/// <param name="writer"></param>
	/// <param name="data"></param>
	public static void WriteToNetData(NetDataWriter writer, PlayerData data) {
		// 基础信息
		writer.Put(data.playId);
		writer.Put(data.TimestampTicks);   // long

		// 变换信息
		writer.Put(data.PosX);
		writer.Put(data.PosY);
		writer.Put(data.PosZ);

		writer.Put(data.RotX);
		writer.Put(data.RotY);
		writer.Put(data.RotZ);
		writer.Put(data.RotW);

		// 左手数据

		writer.Put(data.LeftHand.PosX);
		writer.Put(data.LeftHand.PosY);
		writer.Put(data.LeftHand.PosZ);


		// 右手数据

		writer.Put(data.RightHand.PosX);
		writer.Put(data.RightHand.PosY);
		writer.Put(data.RightHand.PosZ);


		// 状态标志
		writer.Put(data.IsTeleport);
	}

	/// <summary>
	/// 反序列化从NetDataReader (无数据包类型)
	/// </summary>
	/// <param name="reader"></param>
	/// <returns></returns>
	public static PlayerData ReadFromNetData(NetDataReader reader) {
		var data = new PlayerData();

		data.playId = reader.GetULong();
		data.TimestampTicks = reader.GetLong();

		// 变换信息
		data.PosX = reader.GetFloat();
		data.PosY = reader.GetFloat();
		data.PosZ = reader.GetFloat();

		data.RotX = reader.GetFloat();
		data.RotY = reader.GetFloat();
		data.RotZ = reader.GetFloat();
		data.RotW = reader.GetFloat();

		// 左手数据

		data.LeftHand.PosX = reader.GetFloat();
		data.LeftHand.PosY = reader.GetFloat();
		data.LeftHand.PosZ = reader.GetFloat();


		// 右手数据


		data.RightHand.PosX = reader.GetFloat();
		data.RightHand.PosY = reader.GetFloat();
		data.RightHand.PosZ = reader.GetFloat();


		// 状态标志
		data.IsTeleport = reader.GetBool();

		return data;
	}

	/// <summary>
	/// NetDataWriter 转 byte[]
	/// </summary>
	public static byte[] WriterToBytes(NetDataWriter writer) {
		// 简单直接的方法
		return writer.AsReadOnlySpan().ToArray();
	}

	/// <summary>
	/// byte[] 转 NetDataReader
	/// </summary>
	public static NetDataReader BytesToReader(byte[] data) {
		var reader = new NetDataReader();
		reader.SetSource(data);
		return reader;
	}
}


