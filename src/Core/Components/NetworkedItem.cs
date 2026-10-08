using UnityEngine;
using WKMPMod.World;

namespace WKMPMod.Components;

public class NetworkedItem : MonoBehaviour {
	public ulong networkId;
	public string prefabKey = string.Empty;
	public ulong ownerId;

	/// <summary>
	/// 1是场景物品 (SceneItemManager.SCENE_ITEM)
	/// 2是丢弃物品 (DroppedItemManager.DROPPED_ITEM)
	/// </summary>
	public ItemType itemCreateType;

	#region[本地缓存组件]

	public Item_Object ItemObject { get; private set; }
	public Rigidbody RigidBody { get; private set; }

	#endregion

	#region[	位置更新]
	private Vector3 _lastSyncPosition;
	private Quaternion _lastSyncRotation;
	private Vector3 _lastSyncVelocity;

	public bool IsRemoteControlled { get; private set; }
	public bool HasMeaningfulChange {
		get {
			if ((transform.position - _lastSyncPosition).sqrMagnitude> POSITION_EPSILON_SQR)
				return true;

			if (Quaternion.Angle(transform.rotation,_lastSyncRotation) > ROTATION_EPSILON)
				return true;

			if ((CurrentVelocity - _lastSyncVelocity).sqrMagnitude> VELOCITY_EPSILON_SQR)
				return true;

			return false;
		}
	}

	private const float POSITION_EPSILON_SQR = 0.0004f;     // 位置变化阈值平方
	private const float ROTATION_EPSILON = 0.5f;            // 旋转变化阈值 (度)
	private const float VELOCITY_EPSILON_SQR = 0.0025f;     // 约 0.05 m/s 阈值

	#endregion

	#region[生命周期与初始化]

	private void Awake() {
		CacheComponents();
	}

	private void OnEnable() {
		RigidBody?.useGravity = true;
	}

	/// <summary>
	/// 缓存常用组件, 避免频繁 GetComponent / 递归查找
	/// </summary>
	public void CacheComponents() {
		if (ItemObject == null) ItemObject = GetComponent<Item_Object>();
		if (RigidBody == null) RigidBody = GetComponent<Rigidbody>() ?? GetComponentInChildren<Rigidbody>();
	}

	/// <summary>
	/// 初始化或刷新网络身份
	/// </summary>
	public void SetupIdentity(ulong networkId, string prefabKey, ulong ownerId, ItemType sceneOrDropped, bool isRemote) {
		this.networkId = networkId;
		this.prefabKey = prefabKey;
		this.ownerId = ownerId;
		this.itemCreateType = sceneOrDropped;
		CacheComponents();
	}

	/// <summary>
	/// 重置身份状态
	/// </summary>
	public void ResetIdentity() {
		networkId = 0;
		prefabKey = string.Empty;
		ownerId = 0;
		itemCreateType = ItemType.NoneItem;
	}

	#endregion

	#region[状态判断与数据提取]

	/// <summary>
	/// 获取当前物理刚体的有效速度, 低于阈值返回 Vector3.zero
	/// </summary>
	public Vector3 CurrentVelocity {
		get {
			if (RigidBody == null) return Vector3.zero;
			return RigidBody.linearVelocity.sqrMagnitude > VELOCITY_EPSILON_SQR ? RigidBody.linearVelocity : Vector3.zero;
		}
	}

	/// <summary>
	/// 检查该物品是否处于有效的同步状态
	/// </summary>
	public bool IsValidSyncItem() {
		if (this == null || gameObject == null || !gameObject.activeInHierarchy) return false;
		if (ItemObject == null || ItemObject.itemData == null) return false;
		if (ItemObject.itemData.inBag) return false;
		return true;
	}

	/// <summary>
	/// 检查当前物品是否已经在本地玩家的背包或手中
	/// </summary>
	public bool IsInLocalInventory(System.Func<Item, bool> inHandDelegate) {
		if (ItemObject == null || ItemObject.itemData == null) return false;

		bool inBag = ItemObject.itemData.inBag;
		bool inHand = inHandDelegate != null && inHandDelegate(ItemObject.itemData);

		return inBag || inHand;
	}

	#endregion

	#region[状态应用与清理]

	public void RememberSyncState() {
		_lastSyncPosition = transform.position;
		_lastSyncRotation = transform.rotation;
		_lastSyncVelocity = CurrentVelocity;
	}

	/// <summary>
	/// 设置为是否是远程接管状态
	/// </summary>
	public void SetRemoteControlled(bool state) {
		IsRemoteControlled = state;
		if (RigidBody != null) RigidBody.isKinematic = state;
	}

	/// <summary>
	/// 应用远程发来的 Transform 与物理速度
	/// </summary>
	public void ApplyRemoteState(Vector3 position, Quaternion rotation, Vector3 velocity) {
		transform.SetPositionAndRotation(position, rotation);

		if (RigidBody != null && RigidBody.useGravity) RigidBody.linearVelocity = velocity;
		
		if (!gameObject.activeSelf) gameObject.SetActive(true);
	}

	/// <summary>
	/// 强制执行自我清理：包含清理本地玩家背包内对应的 Item 数据, 并根据参数销毁世界实体
	/// </summary>
	public void ForceCleanup() {
		if (networkId == 0) return;

		// 1. 从背包中擦除对应数据
		RemoveFromPlayerInventory();

		// 2. 处理世界物理实体
		gameObject.SetActive(false);
		Destroy(gameObject);
	}

	/// <summary>
	/// 遍历玩家背包, 将具有相同 NetworkId 的数据项移除并失效
	/// </summary>
	private void RemoveFromPlayerInventory() {
		var inventory = ENT_Player.GetInventory();
		if (inventory == null) return;

		// 辅助清理闭包：检查 Item 对应的物理实体 Identity 并执行销毁标记
		bool ProcessItem(Item item) {
			if (item == null) return false;
			var dropObj = item.GetDropObject(false);
			if (dropObj == null) return false;

			if (dropObj.TryGetComponent<NetworkedItem>(out var identity) && identity.networkId == networkId) {
				item.hasBeenDestroyed = true;
				return true;
			}
			return false;
		}

		// 1. 手部
		if (inventory.itemHands != null) {
			foreach (var handSlot in inventory.itemHands) {
				if (handSlot?.currentItem != null && ProcessItem(handSlot.currentItem)) {
					inventory.ClearItemFromHand(handSlot.currentItem);
					return;
				}
			}
		}

		// 2. 快捷口袋 (Pockets)
		if (inventory.pockets != null) {
			foreach (var pocket in inventory.pockets) {
				if (pocket?.pouch?.pouchItems == null) continue;
				var list = pocket.pouch.pouchItems;
				for (int i = list.Count - 1; i >= 0; i--) {
					if (ProcessItem(list[i])) {
						list.RemoveAt(i);
						inventory.RescanInventory();
						return;
					}
				}
			}
		}

		// 3. 主背包 (BagItems)
		if (inventory.bagItems != null) {
			for (int i = inventory.bagItems.Count - 1; i >= 0; i--) {
				if (ProcessItem(inventory.bagItems[i])) {
					inventory.bagItems.RemoveAt(i);
					inventory.RescanInventory();
					return;
				}
			}
		}

		// 4. 额外口袋 (ExtraPouches)
		if (inventory.extraPouches != null) {
			foreach (var pouch in inventory.extraPouches) {
				if (pouch?.pouchItems == null) continue;
				var list = pouch.pouchItems;
				for (int i = list.Count - 1; i >= 0; i--) {
					if (ProcessItem(list[i])) {
						list.RemoveAt(i);
						inventory.RescanInventory();
						return;
					}
				}
			}
		}
	}

	#endregion
}