using UnityEngine;

namespace WKMPMod.Component;

public class NetworkedClimableItem : MonoBehaviour {
    public string NetworkId { get; set; } = string.Empty;
    public string PrefabKey { get; set; } = string.Empty;
    public ulong OwnerId { get; set; }
    public bool IsRemote { get; set; }
    public float LastSentTime { get; set; }
    public float LastSecureAmount { get; set; }
    public bool LastSecure { get; set; }
    public bool LastActive { get; set; }
    public Vector3 LastPosition { get; set; }
    public Quaternion LastRotation { get; set; }
}