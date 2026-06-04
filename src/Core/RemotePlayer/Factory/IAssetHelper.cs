using UnityEngine;

namespace WKMPMod.RemotePlayer;

public interface IAssetHelper {
	/// <summary>
	/// 第三方可以从主Mod帮其加载的专属外部AB包资源里提取非GameObject的资产(如材质, 贴图等)
	/// </summary>
	T GetCustomAsset<T>(string assetName) where T : Object;
}
