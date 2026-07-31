using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace WKMultiPlayerMod.Shared.Data;

[System.Serializable]
public struct ItemPoseData {
	[Header("物品相对于手的 Transform")]
	public Vector3 itemPosition;
	public Quaternion itemRotation;
	public Vector3 itemScale;

	[Header("拿持该物品时手部的姿态偏置")]
	public Quaternion handRotationOffset;

	public ItemPoseData(Vector3 pos, Quaternion rot, Vector3 scale, Quaternion handRot) {
		itemPosition = pos;
		itemRotation = rot;
		itemScale = scale;
		handRotationOffset = handRot;
	}

	// 默认回退配置
	public static ItemPoseData Default => new ItemPoseData(
		Vector3.zero,
		Quaternion.identity,
		Vector3.one,
		Quaternion.identity
	);
}