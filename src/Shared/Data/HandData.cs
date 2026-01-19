using UnityEngine;

namespace WKMPMod.Data;

[System.Serializable]
public struct HandData {
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
}

