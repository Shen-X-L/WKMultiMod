using System.Linq;
using TMPro;
using UnityEngine;
using WKMPMod.Core;
using WKMPMod.Util;
using Object = UnityEngine.Object;

namespace WKMPMod.RemotePlayer;

public class SlugcatFactory : BaseRemoteFactory {
	// 透视字体材质
	private const string TMP_DISTANCE_FIELD_OVERLAY_MAT = 
		"assets/projects/materials/textmeshpro_distance field overlay.mat";
	// 游戏字体贴图
	private const string GAME_TMP_FONT_ASSET = "Ticketing SDF";

	#region[接口实现]

	protected override void OnPrepare(GameObject prefab, AssetBundle bundle) {
		FixTMPComponent(prefab, bundle);
	}

	public override void Cleanup(GameObject instance) {
		if (instance != null) {
			// 找到名字标签组件
			//var tmpText = instance.GetComponentInChildren<TMPro.TMP_Text>();

			//if (tmpText != null && tmpText.fontMaterial != null) {
			//	Object.Destroy(tmpText.fontMaterial);
			//}
		}
		base.Cleanup(instance);
	}

	#endregion

	#region[修复TMP字体和材质]
	/// <summary>
	/// 修复TMP字体和材质
	/// </summary>
	private void FixTMPComponent(GameObject prefab,AssetBundle bundle) {
		// 特化处理 TextMeshPro
		foreach (var tmpText in prefab.GetComponentsInChildren<TMP_Text>(true)) {
			MPMain.LogInfo(Localization.Get("RPSlugcatFactory.SpecializingTMPComponent", tmpText.name));

			// 游戏内原生字体
			TMP_FontAsset gameFont = Resources.FindObjectsOfTypeAll<TMP_FontAsset>()
							 .FirstOrDefault(f => f.name == GAME_TMP_FONT_ASSET);
			if (gameFont == null) {
				MPMain.LogError(Localization.Get("RPSlugcatFactory.FontAssetNotFound", GAME_TMP_FONT_ASSET));
				continue;
			}
			// 赋值组件字体
			tmpText.font = gameFont;

			// 透视字体材质
			Material bundleMat = bundle.LoadAsset<Material>(TMP_DISTANCE_FIELD_OVERLAY_MAT);
			// 实例材质副本
			Material instanceMat = tmpText.fontMaterial;
			if (instanceMat != null && bundleMat != null) {
				// Overlay Shader 赋给实例副本
				instanceMat.shader = bundleMat.shader;

				MPMain.LogInfo(Localization.Get("RPSlugcatFactory.ImplementOverlayViaShader"));
			} else {
				MPMain.LogError(Localization.Get("RPSlugcatFactory.UnableToLoadMaterial",TMP_DISTANCE_FIELD_OVERLAY_MAT));
			}
		}
	}

	#endregion

}