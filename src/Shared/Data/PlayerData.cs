using UnityEngine;
using WKMPMod.Data;

public enum HandType { Left = 0, Right = 1 };

[Serializable]
public struct PlayerData : INetworkSerializable {
	[Serializable]
	public struct HandData : INetworkSerializable {
		// 手部类型
		public HandType handType;
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
		public void Serialize(DataWriter writer) {
			writer.Put((byte)handType);
			writer.Put(Position);
		}
		public void Deserialize(DataReader reader) {
			handType = (HandType)reader.GetByte();
			Position = reader.GetVector3();
		}
	}

	public ulong playId;
	public long TimestampTicks;

	// 直接存储原始字段以保证极致性能
	public float PosX, PosY, PosZ;
	public float RotX, RotY, RotZ, RotW;

	// 建议 HandData 也改为 struct
	public HandData LeftHand;
	public HandData RightHand;

	public bool IsTeleport;

	public Vector3 Position {
		get => new Vector3(PosX, PosY, PosZ);
		set { PosX = value.x; PosY = value.y; PosZ = value.z; }
	}

	public Quaternion Rotation {
		get => new Quaternion(RotX, RotY, RotZ, RotW);
		set { RotX = value.x; RotY = value.y; RotZ = value.z; RotW = value.w; }
	}

	public void Serialize(DataWriter writer) {
		writer.Put(playId).Put(TimestampTicks);
		writer.Put(Position).Put(Rotation);
		writer.Put(LeftHand.Position).Put(RightHand.Position);
		writer.Put(IsTeleport);
	}

	public void Deserialize(DataReader reader) {
		playId = reader.GetULong();
		TimestampTicks = reader.GetLong();
		Position = reader.GetVector3();
		Rotation = reader.GetQuaternion();

		LeftHand.Position = reader.GetVector3();
		RightHand.Position = reader.GetVector3();

		IsTeleport = reader.GetBool();
	}
}

