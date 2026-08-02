using UnityEngine;

namespace WKMPMod.Component;

public class NetworkedEnemy : MonoBehaviour {
	public string NetworkId;
	public float LastHealth = float.NaN;
	public Vector3 LastPosition;
	public Quaternion LastRotation;
	public bool LastRemoved;
}
