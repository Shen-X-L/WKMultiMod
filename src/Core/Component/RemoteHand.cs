using Newtonsoft.Json.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using UnityEngine;
using UnityEngine.UIElements;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.Util;
using WKMultiPlayerMod.Shared.Data;
using static UnityEngine.GraphicsBuffer;
using static UnityEngine.UI.Image;
using Vector3 = UnityEngine.Vector3;

namespace WKMPMod.Component;
// MultiPlayerHandComponent: 管理玩家手部的网络同步位置
public class RemoteHand : MonoBehaviour {
	#region[映射后配置字段]

	public byte handType;// 手部索引
	public float teleportThreshold = 50f;// 瞬移阈值: 超过此距离直接传送
	public float fastSmoothDistance = 10f;// 最大平滑距离: 超过此距离使用快速平滑
	public float baseGrabStrength = 0.2f;   // 基础拖拽力度
	public Vector3 grabAttachOffset = Vector3.up * -0.5f;// 拖拽吸附点偏移
	public float maxGrabDistance = 10f; // 拖拽最大有效距离
	private const float GRAB_TIMEOUT_SECONDS = 1.0f;    // 拖拽超时时间 (秒) 

	#endregion

	#region[运行时数据]

	// 玩家ID,用于识别玩家
	public IDType playerId;
	// 状态标志
	private bool _isTeleporting;           // 是否正在传送
	private float _lastGrabTimestamp;      // 上次拖拽时间戳
	private bool _isBeingGrabbed;           // 是否正在被拖拽
	private bool _wasBeingGrabbed;          // 上一帧是否被拖拽 (用于检测状态变化)
	private bool _wasBeingHanged;         // 是否被抓取 (由外部设置, 可能影响位置同步逻辑)

	// 位置同步
	private Vector3 _targetWorldPosition;  // 目标世界坐标
	private Vector3 _smoothVelocity;       // 平滑移动速度


	private float _currentGrabStrength; // 动态力度

	#endregion

	#region[手部物品相关]

	public const string NONE_ITEM_NAME = "None";// 空物品
	public const string ITEM_GLOVE_NAME = "Item_Artifact_EVAGlove";// 手套物品
	public const string ITEM_TEMP = "Item_Temp";// 临时物品
	private Dictionary<string, GameObject> _itemCache = new();// 手持物品缓存字典
	private string _itemPrefabName;// 目前物品预制体名字
	private GameObject _item;// 目前物品实例
	public Dictionary<string, ItemPoseData> itemTransform;// 物品手中的Transform RPContainer负责初始化
	private MeshRenderer _renderer;// 手部模型

	#endregion

	#region[姿态变换]

	public Transform shoulderTransform; // 肩膀 Transform (若无胳膊可留空)
	public Transform bodyTransform;     // 身体 Transform (无胳膊时的备用参照)

	#endregion

	#region[Unity生命周期函数]

	void Start() {
		_renderer = GetComponent<MeshRenderer>();
	}

	// 每帧更新位置
	void LateUpdate() {
		// 如果是传送状态,不进行平滑移动
		if (_isTeleporting) return;

		#region[位置变换]

		// 检查当前位置与目标位置的距离
		float distanceToTarget = Vector3.Distance(transform.position, _targetWorldPosition);

		// 如果距离超过阈值,直接瞬移
		if (distanceToTarget > teleportThreshold) {
			TeleportToPosition(_targetWorldPosition);
			return;
		}

		// 平滑移动到目标位置
		if (transform.position != _targetWorldPosition) {
			float smoothTime = CalculateDynamicSmoothTime(distanceToTarget);

			transform.position = Vector3.SmoothDamp(
				transform.position,     // 当前位置
				_targetWorldPosition,   // 目标位置
				ref _smoothVelocity,    // 速度引用
				smoothTime,             // 平滑时间
				float.MaxValue,         // 最大速度
				Time.deltaTime          // 时间增量
			);

			// 低速强制移动
			if (_smoothVelocity.magnitude < 0.5f && distanceToTarget > 0.05f) {
				Vector3 direction = (_targetWorldPosition - transform.position).normalized;
				_smoothVelocity = direction * 1f;
			}
		}

		#endregion

		#region [旋转变换]

		Quaternion baseRotation;

		if (shoulderTransform != null && (transform.position - shoulderTransform.position).sqrMagnitude > 0.001f) {
			// 有胳膊 肩膀 -> 手
			baseRotation = Quaternion.LookRotation(transform.position - shoulderTransform.position, Vector3.up);
		} else if (bodyTransform != null && (transform.position - bodyTransform.position).sqrMagnitude > 0.001f) {
			// 无胳膊 身体中心 -> 手
			baseRotation = Quaternion.LookRotation(transform.position - bodyTransform.position, Vector3.up);
		} else {
			// 无指向/重合兜底 直接继承父级世界朝向
			baseRotation = transform.parent != null ? transform.parent.rotation : Quaternion.identity;
		}

		// 读取物品握持偏置并统一应用
		Quaternion currentHandOffset = Quaternion.identity;

		if (_itemPrefabName != null && itemTransform?.TryGetValue(_itemPrefabName, out var poseData) == true) {
			var rot = poseData.handRotationOffset;
			if (handType == 0) { // 左手镜像
				var euler = rot.eulerAngles;
				currentHandOffset = Quaternion.Euler(euler.x, -euler.y, -euler.z);
			} else currentHandOffset = rot;
		}

		transform.rotation = baseRotation * currentHandOffset;

		#endregion
	}

	// 稳定帧施加力
	private void FixedUpdate() {
		// 拖拽超时检测
		if (_isBeingGrabbed && Time.time - _lastGrabTimestamp > GRAB_TIMEOUT_SECONDS) {
			_isBeingGrabbed = false;
			MPEventBusGame.NotifyRemoteGrab(playerId, _isBeingGrabbed);
		}

		// 应用拖拽物理
		if (_isBeingGrabbed) {
			ApplyPullPhysics();
		}
	}

	private void OnDestroy() {
		_itemCache.Clear();
	}

	public void Initialize() {
		_renderer = GetComponent<MeshRenderer>();
	}

	#endregion

	/// <summary>
	/// 施加拖拽吸引力
	/// </summary>
	private void ApplyPullPhysics() {
		var player = ENT_Player.GetPlayer();
		if (player == null) return;

		// 计算距离 手部吸附点 -> 玩家
		float distance = Vector3.Distance(
			transform.position + grabAttachOffset,
			player.transform.position);

		// 距离超过10米断开
		if (distance > 10.0f) {
			player.SetGrappled(false);
			_isBeingGrabbed = false;
			return;
		}

		player.SetGrappled(true);

		// 计算距离因子 泊松分布, 距离适中时力度最大
		float k = distance / 2.0f;
		float distanceFactor = k * Mathf.Exp(1.0f - k);
		// 远距离衰减
		if (distance > 4.0f) distanceFactor *= (5.0f - distance) / 2.0f;

		// 平滑力度
		_currentGrabStrength = Mathf.Lerp(
			_currentGrabStrength,
			baseGrabStrength,
			Time.fixedDeltaTime * 0.8f
		);

		// 计算力
		Vector3 pullDirection = (transform.position - player.transform.position).normalized;
		Vector3 finalVelocity = pullDirection * Time.fixedDeltaTime * _currentGrabStrength * distanceFactor;

		player.TonguePull(finalVelocity);
	}

	/// <summary>
	/// 根据距离计算平滑时间
	/// </summary>
	private float CalculateDynamicSmoothTime(float distance) {
		// 如果距离很远,使用更快的平滑
		if (distance > fastSmoothDistance) {
			// 使用对数曲线,距离越远平滑时间越短
			return Mathf.Clamp(Mathf.Log(distance) * 0.1f, 0.05f, 0.3f);
		}

		// 正常距离使用原来的计算方法
		return Mathf.Clamp(distance / 10f, 0.05f, 0.1f);
	}

	/// <summary>
	/// 根据网络数据更新手部位置和状态
	/// </summary>
	/// <param name="handData">手部数据</param>
	/// <param name="targetPlayerId">该玩家ID是否被抓取/被拖拽(一般传入本玩家ID)</param>
	public void UpdateFromHandData(ref PlayerData.HandData handData, IDType targetPlayerId = 0) {
		// 重置传送标志
		_isTeleporting = false;
		// 使用网络传来的世界位置
		_targetWorldPosition = handData.Position;

		if (handData.IsBeGrabbing(targetPlayerId)) {
			_targetWorldPosition = handData.DesiredPosition;
			_isBeingGrabbed = true;
			_lastGrabTimestamp = Time.time;
		} else {
			_isBeingGrabbed = false;
		}

		// 被抓取状态变化通知
		if (handData.IsBeHanging(targetPlayerId) != _wasBeingHanged) {
			MPEventBusGame.NotifyRemoteHang(playerId, !_wasBeingHanged);
			_wasBeingHanged = !_wasBeingHanged;
		}

		// 被拖拽状态变化通知
		if (_isBeingGrabbed != _wasBeingGrabbed) {
			MPEventBusGame.NotifyRemoteGrab(playerId, _isBeingGrabbed);
			_wasBeingGrabbed = _isBeingGrabbed;
		}

		// 更新手持物品
		if (handData.handItemUpdate) UpdateHandItem(ref handData);
	}

	/// <summary>
	/// 更新手持物品
	/// </summary>
	public void UpdateHandItem(ref PlayerData.HandData handData) {
		// 非抓取模式 (!=0) || 物品未变化
		if (handData.interactState != 0 || handData.itemPrefabName == _itemPrefabName) return;
		// 当前引用已经失效
		if (_item == null) _item = null;
		// 空物品
		if (handData.itemPrefabName == NONE_ITEM_NAME) {
			_itemPrefabName = null;
			_item?.SetActive(false);
			_renderer.enabled = true;
			return;
		}
		// 是否是手套 (关闭手模型)
		_renderer.enabled = handData.itemPrefabName != ITEM_GLOVE_NAME;
		// 获取或创建物品实例
		GameObject? item = GetOrCreateItem(handData.itemPrefabName);
		if (item == null) return;
		// 切换物品显示
		if (_item != null && _item != item) _item.SetActive(false);
		item.SetActive(true);
		// 更新缓存
		_item = item;
		_itemPrefabName = handData.itemPrefabName;
	}

	/// <summary>
	/// 获取或创建手持物品实例
	/// </summary>
	/// <param name="prefabName"></param>
	/// <returns></returns>
	private GameObject? GetOrCreateItem(string prefabName) {
		if (_itemCache.TryGetValue(prefabName, out var obj)) {
			if (obj != null) return obj;
			// Unity 对象已 Destroy
			_itemCache.Remove(prefabName);
		}
		// 重新构建物品实例
		obj = BuildItem(prefabName);
		if (obj != null) _itemCache[prefabName] = obj;
		return obj;
	}

	/// <summary>
	/// 构建手持物品实例
	/// </summary>
	private GameObject? BuildItem(string prefabName) {
		if (!MPUtil.TryGetItemPrefab(prefabName, out var itemPrefab)) {
			MPMain.LogError(Localization.Get("MPMessageHandlers.PrefabDoesNotExist", prefabName));
			if (!MPUtil.TryGetItemPrefab(ITEM_TEMP, out var itemPrefabTemp)) {
				MPMain.LogError(Localization.Get("MPMessageHandlers.PrefabDoesNotExist", ITEM_TEMP));
				return null;
			}
			itemPrefab = itemPrefabTemp;
		}

		// 构建物品实例
		var obj = GameObject.Instantiate(itemPrefab, transform).gameObject;

		// 移除所有 MeshCollider(镜像后会失效)
		bool needBuildCollider = false;
		foreach (var meshCol in obj.GetComponentsInChildren<MeshCollider>(true)) {
			meshCol.enabled = false;
			Destroy(meshCol);
			needBuildCollider = true;
		}
		// 构建BoxCollider
		var meshFilter = obj.GetComponentInChildren<MeshFilter>();
		if (needBuildCollider && meshFilter != null) {
			var box = obj.AddComponent<BoxCollider>();
			box.isTrigger = true;

			Bounds meshBounds = meshFilter.sharedMesh.bounds;
			// MeshFilter 就挂在 gameObject 本身节点上
			if (meshFilter.gameObject == obj) {
				box.center = meshBounds.center;
				box.size = meshBounds.size;
			} else {
				// MeshFilter 挂在 gameObject 的子节点上
				Transform childT = meshFilter.transform;
				// 将子节点的本地 center 转换到 parent(gameObject) 的本地空间
				box.center = obj.transform.InverseTransformPoint(childT.TransformPoint(meshBounds.center));
				// 考虑子节点自身的相对 localScale
				box.size = Vector3.Scale(meshBounds.size, childT.localScale);
			}
		}
		// 删除生物组件 
		var denizen = obj.GetComponent<Denizen>();
		if (denizen != null) {
			denizen.enabled = false;
			Destroy(denizen);
		}
		// 禁用物理受力(仅碰撞)
		var rb = obj.GetComponent<Rigidbody>();
		if (rb != null)
			rb.isKinematic = true;
		// 修正位置
		if (itemTransform.TryGetValue(prefabName, out var poseData)) {
			// 沿x轴对称
			if (handType == 0) {
				obj.transform.localScale = new Vector3(
					-poseData.itemScale.x,
					poseData.itemScale.y,
					poseData.itemScale.z);

				Vector3 euler = poseData.itemRotation.eulerAngles;
				obj.transform.localRotation =
					Quaternion.Euler(euler.x, -euler.y, -euler.z);

				obj.transform.localPosition = new Vector3(
					-poseData.itemPosition.x,
					poseData.itemPosition.y,
					poseData.itemPosition.z);
			} else {
				obj.transform.localScale = poseData.itemScale;
				obj.transform.localRotation = poseData.itemRotation;
				obj.transform.localPosition = poseData.itemPosition;
			}
		} else {
			obj.transform.localScale =
				handType == 0 ? new Vector3(1, -1, 1) : Vector3.one;
			obj.transform.localRotation = Quaternion.identity;
			obj.transform.localPosition = Vector3.zero;
		}
		// 修改标签
		var tags = obj.GetComponent<ObjectTagger>();
		if (tags != null) {
			tags.RemoveTag("Item");
			tags.RemoveTag("Prop");
			tags.AddTag("ItemLocked");
		}

		obj.SetActive(false);

		return obj;
	}

	/// <summary>
	/// 立即传送
	/// </summary>
	/// <param name="worldPosition"></param>
	public void TeleportToPosition(Vector3 worldPosition) {
		_isTeleporting = true;

		// 立即设置位置 重置速度
		transform.position = worldPosition;
		_targetWorldPosition = worldPosition;
		_smoothVelocity = Vector3.zero;

		// 传送完成后重置状态(延迟一帧确保不会立即开始平滑)
		StartCoroutine(ResetTeleportFlag());

		/// <summary>
		/// 传送结束后等待一帧
		/// </summary>
		IEnumerator ResetTeleportFlag() {
			yield return null;
			_isTeleporting = false;
		}
	}

}
