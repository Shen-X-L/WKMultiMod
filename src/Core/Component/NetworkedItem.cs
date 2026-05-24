using UnityEngine;

namespace WKMPMod.Component;

public class NetworkedItem : MonoBehaviour {
    public string NetworkId = string.Empty;
    public string StableSceneId = string.Empty;
    public string PrefabKey = string.Empty;
    public ulong OwnerId;
    public bool IsRemote;
    public bool WasInstantiatedBySync;
}