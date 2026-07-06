using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Principal;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using WKMPMod.Core;
using WKMPMod.Util;
using static Drawing.Palette.Colorbrewer;

namespace WKMPMod.Asset;

public class MPAssetManager : Singleton<MPAssetManager> {
	public readonly Dictionary<string, GameObject> FX_PrefabDic = new ();
	public readonly Dictionary<string, GameObject> handholdPrefabDic = new ();
	public readonly Dictionary<string, GameObject> projectilePrefabDic = new ();

	public const string DAMAGE_OBJECT_NAME = "Gib_Large";	// 受伤特效预制体名
	public const string DEATH_OBJECT_NAME = "Gib_Medium";   // 死亡特效预制体名
	public const string _ = "Gib_Sturge_Large";             // 备用死亡特效预制体名

	public static Sprite grubSprite = LoadSprite(MPMain.path, "grub.png", true);
	public static Sprite hangSprite = LoadSprite(MPMain.path, "hang.png", true);

	public HashSet<string> loadSet =[
		DAMAGE_OBJECT_NAME,	// 受伤特效预制体名
		DEATH_OBJECT_NAME	// 死亡特效预制体名
	];

	public bool IsInitialized { get; private set; } = false;

	public void Initialize() {
		if (IsInitialized) return;

		// 获取特效
		foreach (GameObject obj in Resources.FindObjectsOfTypeAll<GameObject>()) {
			if (!string.IsNullOrEmpty(obj.scene.name)) continue;// 跳过场景对象
			if (!loadSet.TryGetValue(obj.name, out var actualValue)) continue;// 不在列表中
			if (FX_PrefabDic.TryGetValue(obj.name, out var _)) continue;// 已经加载过了
			if (obj.GetComponentInChildren<ParticleSystem>() == null) continue;// 本身及其子对象没有特效组件

			MPMain.LogInfo(Localization.Get("MPAssetManager.LoadAsset",obj.name, actualValue));
			FX_PrefabDic[actualValue] = obj;
		}

		// 获取玩家生成可抓物
		foreach (var piton in Resources.FindObjectsOfTypeAll<HandItem_Piton>()) {
			if (piton == null) continue;
			var key = MPUtil.CleanCloneName(piton.pitonWorldObject.name);
			if (string.IsNullOrEmpty(key)) continue;
			if (handholdPrefabDic.ContainsKey(key)) continue;

			MPMain.LogInfo(Localization.Get("MPAssetManager.LoadAsset", "HandItem_Piton", key));
			handholdPrefabDic[key] = piton.pitonWorldObject;
		}

		// 投射物 和 投射物的生成可抓物 
		foreach (var projectile in Resources.FindObjectsOfTypeAll<Projectile>()) {
			if (projectile == null) continue;
			var key1 = MPUtil.CleanCloneName(projectile.name);
			if (string.IsNullOrEmpty(key1)) continue;
			if (projectilePrefabDic.ContainsKey(key1)) continue;

			MPMain.LogInfo(Localization.Get("MPAssetManager.LoadAsset", "Projectile", key1));
			projectilePrefabDic[key1] = projectile.gameObject;

			if (projectile.hitEffect == null) continue;
			if (!projectile.hitEffect.TryGetComponent<CL_Handhold>(out var _)) continue;
			var key2 = MPUtil.CleanCloneName(projectile.hitEffect.name);
			if (string.IsNullOrEmpty(key2)) continue;
			if (handholdPrefabDic.ContainsKey(key2)) continue;

			MPMain.LogInfo(Localization.Get("MPAssetManager.LoadAsset", "HitEffect", key2));
			handholdPrefabDic[key2] = projectile.hitEffect;
		}


		IsInitialized = true;
	}

	public static GameObject? GetFXPrefab(string name) { 
		if (!Instance.IsInitialized)
			Instance.Initialize();
		return Instance.FX_PrefabDic.TryGetValue(name, out var obj) ? obj : null;
	}

	public static GameObject? GetHandholdPrefab(string name) {
		if (!Instance.IsInitialized)
			Instance.Initialize();
		return Instance.handholdPrefabDic.TryGetValue(name, out var obj) ? obj : null;
	}

	/// <summary>
	/// 读取png转为
	/// </summary>
	public static Sprite LoadSprite(string dir, string filename, bool makePersistent = false) {
		string fullPath = Path.Combine(dir, filename);
		if (!File.Exists(fullPath)) {
			MPMain.LogTest($"icon not found: {fullPath}");
			return null;
		}
		try {
			// 加载图片文件
			byte[] imageData = File.ReadAllBytes(fullPath);

			// 生成纹理
			Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
			texture.name = $"{filename}_Texture";

			texture.LoadImage(imageData);

			texture.filterMode = FilterMode.Point;// 过滤模式 线性临近
			texture.wrapMode = TextureWrapMode.Clamp;// UV 超出时拉伸边缘像素

			// 生成精灵图
			Sprite sprite = Sprite.Create(texture,new Rect(0, 0, texture.width, texture.height),
				new Vector2(0.5f, 0.5f), 16f);
			sprite.name = filename;

			// 持久化
			if (makePersistent) {
				GameObject.DontDestroyOnLoad(sprite);
				GameObject.DontDestroyOnLoad(texture);
			}

			return sprite;
		} catch (Exception ex) {
			MPMain.LogTest($"icon load failed for {filename}: {ex.Message}");
			return null;
		}
	}
}
