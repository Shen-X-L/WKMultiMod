using UnityEngine;

namespace WKMPMod.Component;

public class NetworkedItem : MonoBehaviour {
    public ulong NetworkId;
    public string PrefabKey = string.Empty;
    public ulong OwnerId;
    public bool IsRemote;
	/// <summary>
    /// 1是场景物品 2是丢弃物品 
    /// </summary>
	public byte SceneOrDropped;
}