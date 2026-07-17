using TMPro;
using UnityEngine;


namespace WKMPMod.RemotePlayer;

public class SlugcatModelExtension : ICustomModelExtension {
	private static readonly string[] TintColorProperties = { "_BaseColor", "_Color", "_MainColor" };
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
			Debug.Log("RPSlugcatFactory.SpecializingTMPComponent"+ tmpText.name);

			// 获取原版字体
			TMP_FontAsset gameFont = Resources.FindObjectsOfTypeAll<TMP_FontAsset>()
				.FirstOrDefault(f => f.name == GAME_TMP_FONT_ASSET);

			if (gameFont == null) {
				Debug.LogError("RPSlugcatFactory.FontAssetNotFound" + GAME_TMP_FONT_ASSET);
				continue;
			}
			tmpText.font = gameFont;

			// 利用 assetHelper 代替直接去调原版 bundle.LoadAsset
			Material bundleMat = assetHelper.GetCustomAsset<Material>(TMP_DISTANCE_FIELD_OVERLAY_MAT);
			Material instanceMat = tmpText.fontMaterial;

			if (instanceMat != null && bundleMat != null) {
				instanceMat.shader = bundleMat.shader;
				Debug.Log("RPSlugcatFactory.ImplementOverlayViaShader");
			} else {
				Debug.LogError("RPSlugcatFactory.UnableToLoadMaterial" + TMP_DISTANCE_FIELD_OVERLAY_MAT);
			}
		}
	}

	/// <summary>
	/// 修改玩家颜色
	/// </summary>
	public void ApplyPlayerColor(GameObject instance, Color32 color) {
		if (instance == null)
			return;

		foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true)) {
			if (renderer.GetComponent<TMP_Text>() != null) continue;

			Color targetColor = renderer.transform.name.StartsWith("Eyes")
				? GetContrastColor(color)
				: (Color)color;

			foreach (var material in renderer.materials) {
				if (material == null) continue;

				foreach (var propertyName in TintColorProperties) {
					if (!material.HasProperty(propertyName)) continue;
					
					var current = material.GetColor(propertyName);
					material.SetColor(
						propertyName,
						new Color(targetColor.r,targetColor.g,targetColor.b,
							(byte)Mathf.Clamp(Mathf.RoundToInt(current.a * 255f), 0, 255)));
				}
			}
		}
	}

	/// <summary>
	/// 根据亮度计算高对比色
	/// </summary>
	private static Color GetContrastColor(Color color) {
		float luminance =
			0.299f * color.r +
			0.587f * color.g +
			0.114f * color.b;

		return luminance > 0.5f
			? Color.black
			: Color.white;
	}

	/// <summary>
	/// 切换玩家下蹲状态
	/// </summary>
	public void Crouching(GameObject gameObject,bool isCrouching) {

	}

	public void HandlePlayerData(GameObject gameObject,Dictionary<string,string> playerData) { 
	
	}
}