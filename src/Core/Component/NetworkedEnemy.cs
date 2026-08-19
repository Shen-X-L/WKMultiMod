using UnityEngine;

namespace WKMPMod.Component;

public class NetworkedEnemy : MonoBehaviour {
	public ulong NetworkId;
	public float LastHealth = float.NaN;
	public Vector3 LastPosition;
	public Quaternion LastRotation;
	public bool LastRemoved;
}
