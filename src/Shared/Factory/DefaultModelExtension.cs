using TMPro;
using UnityEngine;
using UnityEngine.UI;
using WKMultiPlayerMod.Shared.Component;
using WKMultiPlayerMod.Shared.Data;
using static Unity.Burst.Intrinsics.X86.Avx;
using static UnityEngine.InputSystem.OnScreen.OnScreenStick;

namespace WKMPMod.RemotePlayer;

public class DefaultModelExtension : ICustomModelExtension {

	public string ModelId => "default";
	public string PrefabAssetName => "CapsulePlayerPrefab";

	// 特效资产名称定义 (如果走主Mod默认, 就返回 null)
	public string SpawnEffectAssetName => null;
	public string DeathEffectAssetName => null;
	public string DamageEffectAssetName => null;

	private const string DISTANCE_FIELD_OVERLAY_SHADER = "TextMeshPro/Distance Field";
	private const string GAME_TMP_FONT_ASSET = "Ticketing SDF";

	/// <summary>
	/// 预制体加载时进行的操作
	/// </summary>
	public void OnPrefabLoaded(GameObject prefabTemplate, IAssetHelper assetHelper) {
		FixTMPComponent(prefabTemplate, assetHelper);

		// 获取组件
		var behaviour = prefabTemplate.GetComponent<DefaultModelBehaviour>();
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
		if (prefab == null) return;

		// 获取原版字体
		TMP_FontAsset gameFont = Resources.FindObjectsOfTypeAll<TMP_FontAsset>()
			.FirstOrDefault(f => f.name == GAME_TMP_FONT_ASSET);

		if (gameFont == null) {
			Debug.LogError($"SlugcatFactory.FontAssetNotFound: {GAME_TMP_FONT_ASSET}");
			return;
		}

		// Shader 从 AB 包只加载一次
		Shader overlayShader = assetHelper.GetCustomAsset<Shader>(DISTANCE_FIELD_OVERLAY_SHADER);
		if (overlayShader == null) Debug.LogError($"DefaultFactory.UnableToLoadShader: {DISTANCE_FIELD_OVERLAY_SHADER}");
		
		// 获取 Prefab 下所有的 TMP 组件
		var tmpComponents = prefab.GetComponentsInChildren<TMP_Text>(true);

		foreach (var tmpText in tmpComponents) {
			Debug.Log($"DefaultFactory.SpecializingTMPComponent: {tmpText.name}");

			// 赋予原版字体
			tmpText.font = gameFont;

			if (overlayShader != null) {
				// 基于原版字体材质实例化一个副本
				// 必须 new Material() 复制一份, 否则改 shader 会直接影响游戏里所有使用该字体的 UI
				Material overlayMat = new Material(gameFont.material);
				overlayMat.shader = overlayShader;

				// 将带有 Overlay Shader 的新材质副本赋值给 TMP
				tmpText.fontSharedMaterial = overlayMat;

				Debug.Log($"DefaultFactory.ImplementOverlayViaShader: {tmpText.name}");
			}
		}
	}
}