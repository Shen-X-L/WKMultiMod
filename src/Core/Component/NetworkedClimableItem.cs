using UnityEngine;

namespace WKMPMod.Component;

public class NetworkedClimableItem : MonoBehaviour {
    public string NetworkId = string.Empty;
    public string PrefabKey = string.Empty;
    public ulong OwnerId;
	public bool IsRemote;
    public float LastSentTime;
    public float LastSecureAmount;
    public bool LastSecure;
    public bool LastActive;
    public Vector3 LastPosition;
    public Quaternion LastRotation;
}