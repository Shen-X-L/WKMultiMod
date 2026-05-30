using TMPro;
using UnityEngine;
using WKMPMod.Component;
using WKMPMod.Core;
using WKMPMod.MK_Component;
using WKMPMod.Util;

namespace WKMPMod.RemotePlayer;

/// <summary>
/// 负责对所有刚从 AB 包捞出来的预制体母本进行游戏原版兼容性修复
/// </summary>
public static class RPPrefabProcessor {

	/// <summary>
	/// 执行主Mod标准修复流水线
	/// </summary>
	public static void RunDefaultPipeline(GameObject prefab, string modelId) {
		ProcessPrefabMarkers(prefab);
		FixShaders(prefab);
	}

	/// <summary>
	/// shader资源链接
	/// </summary>
	private static void FixShaders(GameObject prefab) {
		foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(true)) {
			if (renderer.GetComponent<TMP_Text>() != null) continue;

			foreach (var mat in renderer.sharedMaterials) {
				if (mat == null) continue;
				var internalShader = Shader.Find(mat.shader.name);
				if (internalShader != null) {
					mat.shader = internalShader;
				} else {
					MPMain.LogError(Localization.Get("RPBaseFactory.ShaderNotFoundOnRenderer", mat.shader.name, renderer.name));
				}
			}
		}
	}

	private static void ProcessPrefabMarkers(GameObject prefab) {
		var rootRigidbody = prefab.GetComponent<Rigidbody>();

		foreach (var mk in prefab.GetComponentsInChildren<MK_RemoteHand>(true)) {
			var component = mk.gameObject.AddComponent<RemoteHand>();
			component.handType = mk.hand;
			component.teleportThreshold = mk.teleportThreshold;
			component.fastSmoothDistance = mk.fastSmoothDistance;
			component.basePullStrength = mk.basePullStrength;
			Object.DestroyImmediate(mk);
		}

		// 1. 处理受击实体
		foreach (var mk in prefab.GetComponentsInChildren<MK_RemoteEntity>(true)) {
			var component = mk.gameObject.AddComponent<RemoteEntity>();
			Object.DestroyImmediate(mk);
		}

		// 2. 处理标签
		foreach (var mk in prefab.GetComponentsInChildren<MK_ObjectTagger>(true)) {
			var component = mk.gameObject.GetComponent<ObjectTagger>() ?? mk.gameObject.AddComponent<ObjectTagger>();
			if (component != null) {
				foreach (var t in mk.tags) {
					if (!component.tags.Contains(t)) component.tags.Add(t);
				}
			}
			Object.DestroyImmediate(mk);
		}

		// 3. 处理攀爬
		foreach (var mk in prefab.GetComponentsInChildren<MK_CL_Handhold>(true)) {
			var component = mk.gameObject.AddComponent<CL_Handhold>();
			if (component != null) {
				component.activeEvent = mk.activeEvent;
				component.stopEvent = mk.stopEvent;
				component.handholdRenderer = mk.handholdRenderer ?? mk.gameObject.GetComponent<Renderer>();
			}
			Object.DestroyImmediate(mk);
		}

		// 4. 处理 LookAt
		foreach (var la in prefab.GetComponentsInChildren<LookAt>(true)) {
			la.userScale = MPConfig.NameTagScale;
		}
	}

}