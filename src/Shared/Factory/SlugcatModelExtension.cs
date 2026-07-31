using TMPro;
using UnityEngine;
using WKMultiPlayerMod.Shared.Component;
using WKMultiPlayerMod.Shared.Data;


namespace WKMPMod.RemotePlayer;

public class SlugcatModelExtension : ICustomModelExtension {
	public string ModelId => "slugcat";
	public string PrefabAssetName => "SlugcatPlayerPrefab";

	// 特效资产名称定义 (如果走主Mod默认, 就返回 null)
	public string SpawnEffectAssetName => null;
	public string DeathEffectAssetName => null;
	public string DamageEffectAssetName => null;

	private const string TMP_DISTANCE_FIELD_OVERLAY_MAT = "assets/projects/materials/textmeshpro_distance field overlay.mat";
	private const string GAME_TMP_FONT_ASSET = "Ticketing SDF";

	/// <summary>
	/// 原 SlugcatFactory.OnPrepare 的解构实现
	/// </summary>
	public void OnPrefabLoaded(GameObject prefabTemplate, IAssetHelper assetHelper) {
		FixTMPComponent(prefabTemplate, assetHelper);

		// 获取组件
		var behaviour = prefabTemplate.GetComponent<SlugcatModelBehaviour>();
		if (behaviour != null) behaviour.OnPrefabLoaded();
	}

	/// <summary>
	/// 当具体的远程玩家被克隆出来时调用
	/// </summary>
	public void OnPlayerInstanceCreated(GameObject playerInstance) {
		// 如果这里需要对特定克隆体做初始化操作可以写在这, 不需要就保持空
	}

	/// <summary>
	/// 修复TMP组件字体问题
	/// </summary>
	private void FixTMPComponent(GameObject prefab, IAssetHelper assetHelper) {
		foreach (var tmpText in prefab.GetComponentsInChildren<TMP_Text>(true)) {
			Debug.Log("SlugcatFactory.SpecializingTMPComponent" + tmpText.name);

			// 获取原版字体
			TMP_FontAsset gameFont = Resources.FindObjectsOfTypeAll<TMP_FontAsset>()
				.FirstOrDefault(f => f.name == GAME_TMP_FONT_ASSET);

			if (gameFont == null) {
				Debug.LogError("SlugcatFactory.FontAssetNotFound" + GAME_TMP_FONT_ASSET);
				continue;
			}
			tmpText.font = gameFont;

			// 利用 assetHelper 代替直接去调原版 bundle.LoadAsset
			Material bundleMat = assetHelper.GetCustomAsset<Material>(TMP_DISTANCE_FIELD_OVERLAY_MAT);
			Material instanceMat = tmpText.fontMaterial;

			if (instanceMat != null && bundleMat != null) {
				instanceMat.shader = bundleMat.shader;
				Debug.Log("SlugcatFactory.ImplementOverlayViaShader");
			} else {
				Debug.LogError("SlugcatFactory.UnableToLoadMaterial" + TMP_DISTANCE_FIELD_OVERLAY_MAT);
			}
		}
	}
}