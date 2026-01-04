using LiteNetLib;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using UnityEngine;
using UnityEngine.Events;
using WKMultiMod.src.Component;
using WKMultiMod.src.Data;
using WKMultiMod.src.Util;
using static WKMultiMod.src.Data.PlayerData;
using Vector3 = UnityEngine.Vector3;

namespace WKMultiMod.src.Core;

// 生命周期为全局
public class RemotePlayerManager : MonoBehaviour {

	// Debug日志输出间隔
	private TickTimer _debugTick = new TickTimer(5f);

	// 存储所有远程对象
	internal Dictionary<ulong, RemotePlayerContainer> Players = new Dictionary<ulong, RemotePlayerContainer>();

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
		if (Players.TryGetValue(playId, out RemotePlayerContainer value))
			return value;

		var container = new RemotePlayerContainer(playId);

		// 使用专门的根对象
		container.Initialize(GetRemotePlayersRoot());

		Players[playId] = container;
		return container;
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
			MPMain.LogError(
				$"[RPMan] 未找到远程映射对象 ID: {playId.ToString()}",
				$"[RPMan] Remote player object not found. ID: {playId.ToString()}");
			return;
		}
		return;
	}
}



// 单个玩家的容器类
public class RemotePlayerContainer {

	public ulong PlayerId { get; set; }
	public GameObject PlayerObject { get; private set; }
	public GameObject LeftHandObject { get; private set; }
	public GameObject RightHandObject { get; private set; }
	public GameObject NameTagObject { get; private set; }

	private RemotePlayer _remotePlayer;
	private RemoteHand _remoteLeftHand;
	private RemoteHand _remoteRightHand;
	private RemoteTag _remoteTag;
	private RemoteEntity _remoteEntity;

	public PlayerData PlayerData {
		get {
			var data = new PlayerData {
				playId = this.PlayerId,
				TimestampTicks = DateTime.UtcNow.Ticks,
				IsTeleport = true,
			};
			data.Position = PlayerObject.transform.position;
			data.Rotation = PlayerObject.transform.rotation;

			data.LeftHand = new HandData {};
			data.RightHand = new HandData {};
			return data;
		}
	}

	// 初始化时直接传送玩家
	private float _initializationTime;
	private const float FORCED_TELEPORT_DURATION = 5.0f; // 强制传送持续时间

	// 构造函数 - 只设置基本信息
	public RemotePlayerContainer(ulong playId) {
		PlayerId = playId;
		_initializationTime = Time.time;
	}

	// 初始化方法 - 负责创建所有对象
	public bool Initialize(Transform persistentParent = null) {
		try {
			// 创建对象
			CreatePlayerHierarchy();

			// 设置持久化
			if (persistentParent != null) {
				PlayerObject.transform.SetParent(persistentParent, false);
			}
			// Debug
			MPMain.LogInfo(
				$"[RPCont] 远程玩家映射成功 ID: {PlayerId.ToString()}",
				$"[RPCont] Remote player mapping succeeded ID: {PlayerId.ToString()}");
			return true;
		} catch (Exception ex) {
			// Debug
			MPMain.LogError(
				$"[RPCont] 远程玩家映射失败 ID: {PlayerId.ToString()}, Error: {ex.Message}",
				$"[RPCont] Failed to map remote player ID: {PlayerId.ToString()}, Error: {ex.Message}"); 
			CleanupOnFailure();
			return false;
		}
	}

	// 创建并组装对象
	private void CreatePlayerHierarchy() {
		// 创建主玩家对象
		PlayerObject = CreatePlayerObject();

		// 创建手部对象
		(LeftHandObject, RightHandObject) = CreateHandObjects();

		// 创建名称标签
		NameTagObject = CreateNameTagObject();

		// 设置父子关系
		LeftHandObject.transform.SetParent(PlayerObject.transform, false);
		RightHandObject.transform.SetParent(PlayerObject.transform, false);
		NameTagObject.transform.SetParent(PlayerObject.transform, false);
	}

	// 创建玩家对象
	private GameObject CreatePlayerObject() {
		var player = GameObject.CreatePrimitive(PrimitiveType.Capsule);
		player.name = "RemotePlayer_" + PlayerId;
		// 配置触发器
		var collider = player.GetComponent<CapsuleCollider>();
		if (collider != null) {
			collider.isTrigger = true;
			collider.radius = 0.5f;
			collider.height = 2.0f;
		}

		// 添加物理碰撞器
		var physicsCollider = player.AddComponent<CapsuleCollider>();
		physicsCollider.isTrigger = false;
		physicsCollider.radius = 0.4f;
		physicsCollider.height = 1.8f;
		physicsCollider.center = new Vector3(0, 0.1f, 0);

		// 添加攀爬组件
		AddHandHold(player);
		// 配置标签
		ObjectTagger tagger = player.GetComponent<ObjectTagger>();
		if (tagger != null) {
			tagger.tags.Add("Damageable");  //被伤害标签
			tagger.tags.Add("Entity");      //实体标签
		}
		// 远程玩家组件
		_remotePlayer = player.AddComponent<RemotePlayer>();
		// 远程实体组件
		_remoteEntity = player.AddComponent<RemoteEntity>();
		_remoteEntity.PlayerId = PlayerId;
		// 设置外观
		ConfigurePlayerAppearance(player);

		return player;
	}

	// 配置玩家简易外观
	private void ConfigurePlayerAppearance(GameObject player) {
		// 设置材质
		Material bodyMaterial = new Material(Shader.Find("Unlit/Color"));
		bodyMaterial.color = Color.gray;

		Renderer renderer = player.GetComponent<Renderer>();
		if (renderer != null) {
			renderer.material = bodyMaterial;
		}

		// 创建左眼
		GameObject leftEye = GameObject.CreatePrimitive(PrimitiveType.Sphere);
		leftEye.name = "RemotePlayer_LeftEye_" + PlayerId;
		leftEye.transform.SetParent(player.transform);
		leftEye.transform.localScale = new Vector3(0.2f, 0.2f, 0.2f);
		leftEye.transform.localPosition = new Vector3(-0.15f, 0.5f, 0.45f);

		// 设置左眼材质
		Material leftEyeMaterial = new Material(Shader.Find("Unlit/Color"));
		leftEyeMaterial.color = Color.white;
		leftEye.GetComponent<Renderer>().material = leftEyeMaterial;

		// 创建右眼
		GameObject rightEye = GameObject.CreatePrimitive(PrimitiveType.Sphere);
		rightEye.name = "RemotePlayer_RightEye_" + PlayerId;
		rightEye.transform.SetParent(player.transform);
		rightEye.transform.localScale = new Vector3(0.2f, 0.2f, 0.2f);
		rightEye.transform.localPosition = new Vector3(0.15f, 0.5f, 0.45f);

		// 设置右眼材质
		Material rightEyeMaterial = new Material(Shader.Find("Unlit/Color"));
		rightEyeMaterial.color = Color.white;
		rightEye.GetComponent<Renderer>().material = rightEyeMaterial;
	}

	// 创建手部对象
	private (GameObject leftHand, GameObject rightHand) CreateHandObjects() {
		// 创建左手
		GameObject leftHand = GameObject.CreatePrimitive(PrimitiveType.Sphere);
		leftHand.name = "RemotePlayer_LeftHand_" + PlayerId;
		leftHand.transform.localScale = new Vector3(0.2f, 0.2f, 0.2f);

		// 创建右手
		GameObject rightHand = GameObject.CreatePrimitive(PrimitiveType.Sphere);
		rightHand.name = "RemotePlayer_RightHand_" + PlayerId;
		rightHand.transform.localScale = new Vector3(0.2f, 0.2f, 0.2f);

		// 配置触发器
		var leftCollider = leftHand.GetComponent<SphereCollider>();
		if (leftCollider != null) {
			leftCollider.isTrigger = true;
			leftCollider.radius = 0.2f;
		}
		var rightCollider = rightHand.GetComponent<SphereCollider>();
		if (rightCollider != null) {
			rightCollider.isTrigger = true;
			rightCollider.radius = 0.2f;
		}

		// 添加攀爬组件
		AddHandHold(leftHand);
		AddHandHold(rightHand);
		// 远程手部组件
		_remoteLeftHand = leftHand.AddComponent<RemoteHand>();
		_remoteLeftHand.hand = HandType.Left;
		_remoteRightHand = rightHand.AddComponent<RemoteHand>();
		_remoteRightHand.hand = HandType.Right;

		// 配置手部外观
		ConfigureHandAppearance(leftHand, rightHand);

		return (leftHand, rightHand);
	}

	// 配置手部简易外观
	private void ConfigureHandAppearance(GameObject leftHand, GameObject rightHand) {
		// 设置左手材质
		Material leftHandMaterial = new Material(Shader.Find("Unlit/Color"));
		leftHandMaterial.color = Color.white;

		Renderer leftRenderer = leftHand.GetComponent<Renderer>();
		if (leftRenderer != null) {
			leftRenderer.material = leftHandMaterial;
		}

		// 设置右手材质
		Material rightHandMaterial = new Material(Shader.Find("Unlit/Color"));
		rightHandMaterial.color = Color.white;

		Renderer rightRenderer = rightHand.GetComponent<Renderer>();
		if (rightRenderer != null) {
			rightRenderer.material = rightHandMaterial;
		}
	}

	// 创建文本框
	private GameObject CreateNameTagObject() {
		var textObject = new GameObject("PlayerID_Text_" + PlayerId);
		textObject.transform.localPosition = new Vector3(0f, 1.5f, 0f);

		var textMesh = textObject.AddComponent<TextMesh>();
		textMesh.text = "Player: " + PlayerId;
		textMesh.fontSize = 20;
		textMesh.characterSize = 1.0f;
		textMesh.anchor = TextAnchor.MiddleCenter;
		textMesh.color = new Color(1f, 1f, 1f, 0.85f);
		textMesh.fontStyle = FontStyle.Bold;
		textMesh.font = Resources.GetBuiltinResource<Font>("Arial.ttf");

		// 添加看板与缩放组件
		var billboard = textObject.AddComponent<LootAt>();

		// 挂载管理组件并初始化
		_remoteTag = textObject.AddComponent<RemoteTag>();
		_remoteTag.Initialize(PlayerId); // 传入 SteamID

		return textObject;
	}

	// 清理整个对象
	private void CleanupOnFailure() {
		// 清理已创建的对象
		SafeDestroy(PlayerObject);
		SafeDestroy(LeftHandObject);
		SafeDestroy(RightHandObject);
		SafeDestroy(NameTagObject);
	}

	// 清理单个对象
	private void SafeDestroy(GameObject obj) {
		if (obj != null) {
			if (Application.isPlaying) {
				GameObject.Destroy(obj);
			} else {
				GameObject.DestroyImmediate(obj);
			}
		}
	}

	// 销毁方法 - 清理所有资源
	public void Destroy() {
		SafeDestroy(PlayerObject);
		SafeDestroy(LeftHandObject);
		SafeDestroy(RightHandObject);
		SafeDestroy(NameTagObject);

		// 清理引用
		PlayerObject = null;
		LeftHandObject = null;
		RightHandObject = null;
		NameTagObject = null;
		_remotePlayer = null;
		_remoteLeftHand = null;
		_remoteRightHand = null;
		_remoteTag = null;
	}

	// 通过数据进行更新
	public void UpdatePlayerData(PlayerData playerData) {

		// 缺少部分对象
		if (PlayerObject == null || LeftHandObject == null
			|| RightHandObject == null || NameTagObject == null)
			return;

		// 检查组件是否存在,如果不存在尝试获取
		if (_remotePlayer == null) {
			_remotePlayer = PlayerObject.GetComponent<RemotePlayer>();
			if (_remotePlayer == null) {
				// Debug
				MPMain.LogError(
					"[RPCont] PlayerObject的组件未添加",
					"[RPCont] PlayerObject component not added");
				return;
			}
		}

		if (_remoteLeftHand == null) {
			_remoteLeftHand = LeftHandObject.GetComponent<RemoteHand>();
			if (_remoteLeftHand == null) {
				// Debug
				MPMain.LogError(
					"[RPCont] LeftHandObject的组件未添加",
					"[RPCont] LeftHandObject component not added");
				return;
			}
		}

		if (_remoteRightHand == null) {
			_remoteRightHand = RightHandObject.GetComponent<RemoteHand>();
			if (_remoteRightHand == null) {
				// Debug
				MPMain.LogError(
					"[RPCont] RightHandObject的组件未添加",
					"[RPCont] RightHandObject component not added");
				return;
			}
		}

		// 判断是否处于初始化 5 秒内
		bool isInInitPhase = (Time.time - _initializationTime) < FORCED_TELEPORT_DURATION;

		if (playerData.IsTeleport || isInInitPhase) {
			// 使用组件的传送方法
			_remotePlayer.Teleport(playerData.Position, playerData.Rotation);
			Vector3 leftTarget = playerData.LeftHand.Position;
			_remoteLeftHand.Teleport(leftTarget);

			// 3. 处理右手传送
			Vector3 rightTarget = playerData.RightHand.Position;
			_remoteRightHand.Teleport(rightTarget);
		} else {
			// 使用插值更新
			_remotePlayer.UpdatePosition(playerData.Position);
			_remotePlayer.UpdateRotation(playerData.Rotation);
			_remoteLeftHand.UpdateFromHandData(playerData.LeftHand);
			_remoteRightHand.UpdateFromHandData(playerData.RightHand);
		}
	}

	// 进行头部文字更新
	public void UpdateNameTag(string text) {
		if (string.IsNullOrEmpty(text)) { return; }
		if (_remoteTag == null) {
			MPMain.LogError(
				"[RPCont] PlayerNameTag的组件未添加",
				"[RPCont] PlayerNameTag component not added");
			return;
		}
		_remoteTag.SetDynamicMessage(text);
		return;
	}

	// 赋予可攀爬组件
	public static void AddHandHold(GameObject gameObject) {
		// 添加 ObjectTagger 组件
		ObjectTagger tagger = gameObject.AddComponent<ObjectTagger>();
		if (tagger != null) {
			tagger.tags.Add("Handhold");    //攀爬标签
		}

		// 添加 CL_Handhold 组件 (攀爬逻辑)
		CL_Handhold handholdComponent = gameObject.AddComponent<CL_Handhold>();
		if (handholdComponent != null) {
			// 添加停止和激活事件
			handholdComponent.stopEvent = new UnityEvent();
			handholdComponent.activeEvent = new UnityEvent();
		}

		// 确保 渲染器 被赋值, 否则 材质 设置会崩溃
		Renderer objectRenderer = gameObject.GetComponent<Renderer>();
		if (objectRenderer != null) {
			gameObject.GetComponent<CL_Handhold>().handholdRenderer = objectRenderer;
		}
	}
}


