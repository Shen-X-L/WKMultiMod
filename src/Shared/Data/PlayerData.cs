using System;
using UnityEngine;

namespace WKMPMod.Data;


public enum HandType { Left = 0, Right = 1 };

[System.Serializable]
public class PlayerData {
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


