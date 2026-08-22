using UnityEngine;

namespace WKMPMod.Component;

public class NetworkedItem : MonoBehaviour {
    public ulong networkId;
    public string prefabKey = string.Empty;
    public ulong ownerId;
    public bool isRemote;
	/// <summary>
	/// 1是场景物品 (SceneItemManager.SCENE_ITEM)
	/// 2是丢弃物品 (DroppedItemManager.DROPPED_ITEM)
	/// </summary>
	public byte sceneOrDropped;

	#region[本地缓存组件]

	public Item_Object ItemObject { get; private set; }
	public Rigidbody RigidBody { get; private set; }

	private const float VELOCITY_EQSILON_SQR = 0.0025f; // 约 0.05 m/s 阈值

	#endregion

	#region[生命周期与初始化]

	private void Awake() {
		CacheComponents();
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
	public void SetupIdentity(ulong networkId, string prefabKey, ulong ownerId, byte sceneOrDropped, bool isRemote) {
		this.networkId = networkId;
		this.prefabKey = prefabKey;
		this.ownerId = ownerId;
		this.sceneOrDropped = sceneOrDropped;
		this.isRemote = isRemote;
		CacheComponents();
	}

	/// <summary>
	/// 重置身份状态
	/// </summary>
	public void ResetIdentity() {
		networkId = 0;
		prefabKey = string.Empty;
		ownerId = 0;
		isRemote = false;
		sceneOrDropped = 0;
	}

	#endregion

	#region[状态判断与数据提取]

	/// <summary>
	/// 获取当前物理刚体的有效速度, 低于阈值返回 Vector3.zero
	/// </summary>
	public Vector3 CurrentVelocity {
		get {
			if (RigidBody == null) return Vector3.zero;
			return RigidBody.velocity.sqrMagnitude > VELOCITY_EQSILON_SQR ? RigidBody.velocity : Vector3.zero;
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

	/// <summary>
	/// 应用远程发来的 Transform 与物理速度
	/// </summary>
	public void ApplyRemoteState(Vector3 position, Quaternion rotation, Vector3 velocity) {
		transform.SetPositionAndRotation(position, rotation);

		if (RigidBody != null) {
			RigidBody.isKinematic = false;
			RigidBody.velocity = velocity;
		}

		if (!gameObject.activeSelf) gameObject.SetActive(true);
	}

	/// <summary>
	/// 强制执行自我清理：包含清理本地玩家背包内对应的 Item 数据, 并根据参数销毁世界实体
	/// </summary>
	public void ForceCleanup(bool destroyObject) {
		if (networkId == 0) return;

		// 1. 从背包中擦除对应数据
		RemoveFromPlayerInventory();

		// 2. 处理世界物理实体
		gameObject.SetActive(false);
		if (destroyObject) Destroy(gameObject);
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