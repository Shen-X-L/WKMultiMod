using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using WKMPMod.Components;
using WKMPMod.Core;
using WKMPMod.MK_Component;
using WKMPMod.Util;

namespace WKMPMod.RemotePlayers;

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
					MPMain.LogError(Localization.Get("RPPrefabProcessor.ShaderNotFoundOnRenderer", mat.shader.name, renderer.name));
				}
			}
		}
	}

	private static void ProcessPrefabMarkers(GameObject prefab) {
		var rootRigidbody = prefab.GetComponent<Rigidbody>();

		// 1. 处理玩家和受击实体（先添加 RemotePlayer）
		foreach (var mk in prefab.GetComponentsInChildren<MK_RemotePlayer>(true)) {
			mk.gameObject.AddComponent<RemotePlayer>();
		}

		// 2. 处理远程手
		foreach (var mk in prefab.GetComponentsInChildren<MK_RemoteHand>(true)) {
			var component = mk.gameObject.AddComponent<RemoteHand>();
			component.handType = mk.hand;
			component.teleportThreshold = mk.teleportThreshold;
			component.fastSmoothDistance = mk.fastSmoothDistance;
			component.baseGrabStrength = mk.basePullStrength;
			component.shoulderTransform = mk.shoulderTransform;
			component.bodyTransform = mk.bodyTransform;
		}

		// 3. 处理标签
		foreach (var mk in prefab.GetComponentsInChildren<MK_ObjectTagger>(true)) {
			var component = mk.gameObject.GetComponent<ObjectTagger>() ?? mk.gameObject.AddComponent<ObjectTagger>();
			if (component != null) {
				foreach (var t in mk.tags) {
					if (!component.tags.Contains(t)) component.tags.Add(t);
				}
			}
		}

		// 4. 处理受击体（此时 mk.entity 依然有效, 且其 GameObject 上已成功挂载 RemotePlayer）
		foreach (var mk in prefab.GetComponentsInChildren<MK_ENT_Hitbox>(true)) {
			var component = mk.gameObject.AddComponent<ENT_Hitbox>();

			// 安全获取目标 GameObject 上的 RemotePlayer 组件
			if (mk.entity != null) 
				component.entity = mk.entity.gameObject.GetComponent<RemotePlayer>();

			component.canBlinkFrag = false;
			component.passTags = mk.passTags != null ? new List<string>(mk.passTags) : new List<string>();

			component.damageEffects = mk.damageEffects != null
				? mk.damageEffects.Select(effect => new Damageable.DamageEffect {
					id = effect.id,
					requiredTags = effect.requiredTags != null ? new List<string>(effect.requiredTags) : new List<string>(),
					damageMultiplier = effect.damageMultiplier,
					damageEvent = effect.damageEvent
				}).ToList() 
				: new List<Damageable.DamageEffect>();
		}

		// 5. 处理攀爬
		foreach (var mk in prefab.GetComponentsInChildren<MK_CL_Handhold>(true)) {
			var component = mk.gameObject.AddComponent<CL_Handhold>();
			if (component != null) {
				component.activeEvent = mk.activeEvent;
				component.stopEvent = mk.stopEvent;
				component.handholdRenderer = mk.handholdRenderer ?? mk.gameObject.GetComponent<Renderer>();
				component.useSharedHandholdMaterial = false;
			}
		}

		// 6. 处理远程拖拽
		foreach (var mk in prefab.GetComponentsInChildren<MK_RemoteProp>(true)) {
			var component = mk.gameObject.AddComponent<RemoteProp>();
			if (mk.entity != null) {
				component.remoteEntity = mk.entity.gameObject.GetComponent<RemotePlayer>();
			}
		}

		// 7. 处理 LookAt
		foreach (var la in prefab.GetComponentsInChildren<LookAt>(true)) {
			la.userScale = MPConfig.NameTagScale;
		}

		// 统一销毁所有 Marker
		DestroyMarkers<MK_RemotePlayer>(prefab);
		DestroyMarkers<MK_RemoteHand>(prefab);
		DestroyMarkers<MK_ObjectTagger>(prefab);
		DestroyMarkers<MK_ENT_Hitbox>(prefab);
		DestroyMarkers<MK_CL_Handhold>(prefab);
		DestroyMarkers<MK_RemoteProp>(prefab);
	}

	private static void DestroyMarkers<T>(GameObject prefab) where T : Component {
		var markers = prefab.GetComponentsInChildren<T>(true);
		for (int i = 0; i < markers.Length; i++) {
			Object.DestroyImmediate(markers[i]);
		}
	}

}
