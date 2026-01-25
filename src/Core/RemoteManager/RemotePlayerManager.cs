using Steamworks;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using UnityEngine;
using UnityEngine.Events;
using WKMPMod.Component;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.Shared.MK_Component;
using WKMPMod.Util;
using Object = UnityEngine.Object;

namespace WKMPMod.RemoteManager;

// 生命周期为全局
public class RemotePlayerManager : MonoBehaviour {

	// Debug日志输出间隔
	private TickTimer _debugTick = new TickTimer(5f);
	// 存储所有远程对象
	internal Dictionary<ulong, RemotePlayerContainer> Players = new Dictionary<ulong, RemotePlayerContainer>();
	// 蛞蝓猫预制体对象
	GameObject slugcatPrefab;
	// 蛞蝓猫文件地址
	public const string SLUGCAT_FILE_NAME = "projects_bundle";
	// 蛞蝓猫预制体名称
	public const string SLUGCAT_PREFAB_NAME = "SlugcatPrefab";

	void Awake() {
		// 确保根对象存在
		EnsureRootObject();
	}

	void OnDestroy() {
		ResetAll();
	}

	// 清除全部玩家
	public void ResetAll() {
		foreach (var container in Players.Values) {
			container.Destroy();
		}
		Players.Clear();
	}

	/// <summary>
	/// 确保根对象存在
	/// </summary>
	private void EnsureRootObject() {
		// 直接在MultiplayerCore下查找或创建
		var coreTransform = transform.parent; // MultiplayerCore
		var rootName = "RemotePlayers";

		if (coreTransform.Find(rootName) == null) {
			var rootObj = new GameObject(rootName);
			rootObj.transform.SetParent(coreTransform, false);
		}
	}

	/// <summary>
	/// 获取远程玩家根Transform
	/// </summary>
	private Transform GetRemotePlayersRoot() {
		var coreTransform = transform.parent;
		var rootName = "RemotePlayers";

		var root = coreTransform.Find(rootName);
		if (root == null) {
			// 如果找不到,创建一个(应该不会发生,因为EnsureRootObject已调用)
			root = new GameObject(rootName).transform;
			root.SetParent(coreTransform, false);
		}

		return root;
	}

	// 创建玩家对象
	public RemotePlayerContainer PlayerCreate(ulong playId) {
		// 没有预制体,先创建预制体
		if (slugcatPrefab == null) {
			CreateSlugcatPrefab();
		}

		if (Players.TryGetValue(playId, out RemotePlayerContainer value))
			return value;

		var container = new RemotePlayerContainer(playId);

		// 使用专门的根对象
		container.Initialize(slugcatPrefab, GetRemotePlayersRoot());

		container.UpdatePlayerData(new PlayerData {
			Position = new UnityEngine.Vector3(0.0f, 0.0f, 0.0f)
		});

		Players[playId] = container;
		return container;
	}

	// 创建预制体
	private void CreateSlugcatPrefab() {
		var bundle = AssetBundle.LoadFromFile($"{MPMain.path}/{SLUGCAT_FILE_NAME}");
		if (bundle == null) {
			MPMain.LogError(Localization.Get("RemotePlayerManager", "UnableToLoadResources"));
			return;
		}
		// 加载资源
		slugcatPrefab = bundle.LoadAsset<GameObject>(SLUGCAT_PREFAB_NAME); // 按名称
		if (slugcatPrefab == null) {
			MPMain.LogError(Localization.Get("RemotePlayerManager", "SlugcatPrefabNotFound"));
			return;
		}
		// 替换真正组件
		ProcessPrefabMarkers(slugcatPrefab);
	}

	// 清除特定玩家
	public void PlayerRemove(ulong playId) {
		if (Players.TryGetValue(playId, out var container)) {
			container.Destroy();
			Players.Remove(playId);
		}
	}

	// 处理玩家数据
	public void ProcessPlayerData(ulong playId, PlayerData playerData) {

		// 以后加上时间戳处理
		if (Players.TryGetValue(playId, out var RPcontainer)) {
			RPcontainer.UpdatePlayerData(playerData);
			return;
		} else if (_debugTick.TryTick()) {
			MPMain.LogError(Localization.Get(
				"RemotePlayerManager", "RemotePlayerObjectNotFound", playId.ToString()));
			return;
		}
		return;
	}

	#region[将标记组件替换为真实组件]

	public static void ProcessPrefabMarkers(GameObject prefab) {
		Stack<Transform> stack = new Stack<Transform>();

		stack.Push(prefab.transform);

		while (stack.Count > 0) {
			Transform current = stack.Pop();

			try {
				SetRealComponents(current.gameObject);
			} catch (Exception ex) {
				MPMain.LogError(Localization.Get(
					"RemotePlayerManager", "PrefabProcessingError", current.name, ex.Message));
			}
			// 遍历直接子级
			// 这里不需要 Cast,直接循环最快
			for (int i = 0; i < current.childCount; i++) {
				stack.Push(current.GetChild(i));
			}
		}
		return;
	}

	private static void SetRealComponents(GameObject prefab) {
		MapMarkersToRemoteEntity(prefab);
		MapMarkersToObjectTagger(prefab);
		MapMarkersToCL_Handhold(prefab);
		SetLookAt(prefab);
	}

	private static void MapMarkersToRemoteEntity(GameObject prefab) {
		MK_RemoteEntity mk_component = prefab.GetComponent<MK_RemoteEntity>();
		if (mk_component == null)
			return;
		var component = prefab.AddComponent<RemoteEntity>();
		if (component != null) {
			component.AllActive = MPConfig.AllActive;
			component.HammerActive = MPConfig.HammerActive;
			component.RebarActive = MPConfig.RebarActive;
			component.ReturnRebarActive = MPConfig.ReturnRebarActive;
			component.RebarExplosionActive = MPConfig.RebarExplosionActive;
			component.ExplosionActive = MPConfig.ExplosionActive;
			component.PitonActive = MPConfig.PitonActive;
			component.FlareActive = MPConfig.FlareActive;
			component.IceActive = MPConfig.IceActive;
			component.OtherActive = MPConfig.OtherActive;
		} else {
			MPMain.LogError(Localization.Get("RemotePlayerManager", "RemoteEntityAddFailed"));
		}
		Object.DestroyImmediate(mk_component);
	}

	private static void MapMarkersToObjectTagger(GameObject prefab) {
		MK_ObjectTagger mk_component = prefab.GetComponent<MK_ObjectTagger>();
		if (mk_component == null)
			return;
		// 先找是否已有, 没有再加
		var component = prefab.GetComponent<ObjectTagger>() ?? prefab.AddComponent<ObjectTagger>();
		if (component != null) {
			// 使用for循环添加标签
			foreach (var t in mk_component.tags) {
				if (!component.tags.Contains(t)) {
					component.tags.Add(t);
				}
			}
		} else {
			MPMain.LogError(Localization.Get("RemotePlayerManager", "ObjectTaggerAddFailed"));
		}
		Object.DestroyImmediate(mk_component);
	}

	private static void MapMarkersToCL_Handhold(GameObject prefab) {
		MK_CL_Handhold mk_component = prefab.GetComponent<MK_CL_Handhold>();
		if (mk_component == null)
			return;
		var component = prefab.AddComponent<CL_Handhold>();
		if (component != null) {
			component.activeEvent = mk_component.activeEvent;
			component.stopEvent = mk_component.stopEvent;
			component.handholdRenderer = mk_component.handholdRenderer ?? prefab.GetComponent<Renderer>();
			
		} else {
			MPMain.LogError(Localization.Get("RemotePlayerManager", "CL_HandholdAddFailed"));
		}
		Object.DestroyImmediate(mk_component);
	}

	private static void SetLookAt(GameObject prefab) {
		LookAt lookAt = prefab.GetComponent<LookAt>();
		if (lookAt == null)
			return;
		lookAt.userScale = MPConfig.NameTagScale;
	}

	#endregion
}





