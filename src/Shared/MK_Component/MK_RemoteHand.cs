using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace WKMPMod.MK_Component;

public class MK_RemoteHand : MonoBehaviour {
	[Header("左右手")]
	public byte hand;   // 手部标识

	[Header("距离设置")]
	[Tooltip("当当前位置与目标位置超过此距离时直接瞬移")]
	public float teleportThreshold = 50f;   // Unity可编辑的瞬移阈值

	[Tooltip("平滑移动的最大距离限制")]
	public float fastSmoothDistance = 10f;   // 超过此距离使用更快的平滑

	[Header("拉拽参数")]
	public float basePullStrength = 0.2f;    // 基础拖拽力

	[Header("肩膀 Transform")]
	public Transform shoulderTransform; //  若无胳膊可留空

	[Header("身体 Transform")]
	public Transform bodyTransform;     //  无胳膊时的备用参照

	// 立即传送
	public void Teleport(Vector3 position) { 
		// 立即设置位置
		transform.position = position;
	}
}
