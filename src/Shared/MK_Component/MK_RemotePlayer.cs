using UnityEngine;

namespace WKMPMod.MK_Component;

public class MK_RemotePlayer : MonoBehaviour {
	public ulong playerId;
	[Header("距离设置")]
	[Tooltip("当当前位置与目标位置超过此距离时直接瞬移")]
	public float teleportThreshold = 50f;   // Unity可编辑的瞬移阈值

	[Tooltip("平滑移动的最大距离限制")]
	public float maxSmoothDistance = 10f;   // 超过此距离使用更快的平滑
}
