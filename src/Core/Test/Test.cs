using System;
using System.Collections.Generic;
using UnityEngine;
using WKMPMod.Component;
using WKMPMod.Core;
using WKMPMod.RemoteManager;
using WKMPMod.Shared.MK_Component;
using Object = UnityEngine.Object;
using Quaternion = UnityEngine.Quaternion;
using Vector3 = UnityEngine.Vector3;

namespace WKMPMod.Test;

public class Test : MonoBehaviour{
	static GameObject slugcatPrefab;
	public static void LoadSlugcat(string[] args) {
		Debug.Log(MPMain.path);
		var bundle = AssetBundle.LoadFromFile($"{MPMain.path}/projects_bundle");
		if (bundle == null) {
			Debug.LogError("无法加载资源");
			return;
		}

		// 检查预制体
		GameObject[] prefabs = bundle.LoadAllAssets<GameObject>();
		Debug.Log($"预制体数量: {prefabs.Length}");
		foreach (GameObject prefab in prefabs) {
			Debug.Log($"材质: {prefab.name}");
		}

		// 检查材质
		Material[] materials = bundle.LoadAllAssets<Material>();
		Debug.Log($"材质数量: {materials.Length}");
		foreach (Material material in materials) {
			Debug.Log($"材质: {material.name}");
		}

		// 检查纹理
		Texture2D[] textures = bundle.LoadAllAssets<Texture2D>();
		Debug.Log($"纹理数量: {textures.Length}");
		foreach (Texture2D texture in textures) {
			Debug.Log($"纹理: {texture.name}");
		}

		// 详细列出
		foreach (GameObject prefab in prefabs) {
			Debug.Log($"预制体: {prefab.name}");

			// 检查预制体的材质
			Renderer[] renderers = prefab.GetComponentsInChildren<Renderer>();
			foreach (Renderer renderer in renderers) {
				Debug.Log($"\tRenderer: {renderer.name}");
				foreach (Material mat in renderer.sharedMaterials) {
					if (mat != null) {
						Debug.Log($"\t\t材质: {mat.name}");
						foreach (var texName in mat.GetTexturePropertyNames()) {
							Texture tex = mat.GetTexture(texName);
							if (tex != null) {
								Debug.Log($"\t\t\t纹理属性: {texName}, 纹理: {tex.name}");
							}
						}
					}
					else
						Debug.LogWarning($"\t\t材质: NULL!");
				}
			}
		}

		// 加载资源
		slugcatPrefab = bundle.LoadAsset<GameObject>("SlugcatPrefab"); // 按名称
		if (slugcatPrefab == null) {
			Debug.LogError("找不到SlugcatPrefab预制体！");
			return;
		}

		RemotePlayerManager.ProcessPrefabMarkers(slugcatPrefab);

		var slugcatInstance = Instantiate(slugcatPrefab, Vector3.zero, Quaternion.identity);
		Debug.LogError($"Slugcat已在位置 {slugcatInstance.transform.position} 生成");
	}

	public static void CreateSlugcat(string[] args) {
		Vector3 vector3 = new Vector3(0, 0, 0);
		if (args.Length >= 1) {
			vector3.x = float.Parse(args[0]);
		}
		if (args.Length >= 2) {
			vector3.y = float.Parse(args[1]);
		}
		if (args.Length >= 3) {
			vector3.z = float.Parse(args[2]);
		}
		var slugcatInstance = Instantiate(slugcatPrefab, vector3, Quaternion.identity);
	}

	public static void GetGraphicsAPI(string[] args) {
		// 方法1：直接获取当前API
		Debug.Log($"当前图形API: {SystemInfo.graphicsDeviceType}");

		// 方法2：获取详细版本信息
		Debug.Log($"图形API版本: {SystemInfo.graphicsDeviceVersion}");

		// 方法3：获取Shader Model级别
		int smLevel = SystemInfo.graphicsShaderLevel;
		Debug.Log($"Shader Model: {smLevel / 10}.{smLevel % 10}");

		// 方法4：检查具体功能支持
		Debug.Log($"支持计算着色器: {SystemInfo.supportsComputeShaders}");
		Debug.Log($"支持几何着色器: {SystemInfo.supportsGeometryShaders}");
		Debug.Log($"支持曲面细分: {SystemInfo.supportsTessellationShaders}");
		Debug.Log($"支持GPU实例化: {SystemInfo.supportsInstancing}");
	}

}