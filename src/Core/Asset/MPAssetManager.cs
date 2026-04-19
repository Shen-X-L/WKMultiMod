using System;
using System.Collections.Generic;
using System.Security.Principal;
using System.Text;
using UnityEngine;
using WKMPMod.Core;
using WKMPMod.Util;
using static Drawing.Palette.Colorbrewer;

namespace WKMPMod.Asset;

public class MPAssetManager : Singleton<MPAssetManager> {
	public Dictionary<string, GameObject> assetDictionary = new Dictionary<string, GameObject>();

	public const string DAMAGE_OBJECT_NAME = "Gib_Large";	// 受伤特效预制体名
	public const string DEATH_OBJECT_NAME = "Gib_Medium";   // 死亡特效预制体名
	public const string _ = "Gib_Sturge_Large";             // 备用死亡特效预制体名

	public HashSet<string> loadSet =[
		DAMAGE_OBJECT_NAME,	// 受伤特效预制体名
		DEATH_OBJECT_NAME	// 死亡特效预制体名
		];
	public bool IsInitialized { get; private set; } = false;
	public void Initialize() {
		if (IsInitialized) return;

		GameObject[] allObjects = Resources.FindObjectsOfTypeAll<GameObject>();
		foreach (GameObject obj in allObjects) {
			if (!string.IsNullOrEmpty(obj.scene.name)) continue;// 跳过场景对象
			if (!loadSet.TryGetValue(obj.name, out var actualValue)) continue;// 不在列表中
			if (assetDictionary.TryGetValue(obj.name, out var assetObject)) continue;// 已经加载过了

			if (obj.GetComponentInChildren<ParticleSystem>() == null) continue;// 本身及其子对象没有特效组件

			MPMain.LogInfo(Localization.Get("MPAssetManager.LoadAsset",obj.name, actualValue));
			assetDictionary[actualValue] = obj;
		}
		IsInitialized = true;
	}
	public static GameObject? GetAssetGameObject(string name) { 
		if (!Instance.IsInitialized)
			Instance.Initialize();
		return Instance.assetDictionary.TryGetValue(name, out var obj) ? obj : null;
	}
}
