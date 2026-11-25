using UnityEngine;
using UnityEngine.UIElements;
using WKMPMod.Data;
using WKMPMod.World;

namespace WKMPMod.Components;

/// <summary>
/// 可攀爬物体的持久化数据结构 (脱离 MonoBehaviour 独立存在)
/// </summary>
public struct ClimbableData : INetworkSerializable {
	public NetworkObjectId NetworkId;
	public string prefabKey;

	// 状态数据
	public Vector3 position;
	public Quaternion rotation;
	public float secureAmount;
	public bool secure;

	/// <summary>
	/// 初始化并绑定持久化数据结构
	/// </summary>
	public void BindData(Vector3 position, Quaternion rotation, float secureAmount, bool secure) {
		this.position = position;
		this.rotation = rotation;
		this.secure = secure;
		this.secureAmount = secureAmount;
	}

	public void Serialize(DataWriter writer) {
		writer.Put(NetworkId.InstanceId);
		writer.Put(NetworkId.OwnerId);
		writer.Put(prefabKey);
		writer.Put(position);
		writer.Put(rotation);
		writer.Put(secureAmount);
		writer.Put(secure);
	}

	public void Deserialize(DataReader reader) {
		var networkId = reader.GetInt();
		var ownerId = reader.GetULong();
		this.NetworkId = new NetworkObjectId(ownerId, networkId);
		this.prefabKey = reader.GetString();
		this.position = reader.GetVector3();
		this.rotation = reader.GetQuaternion();
		this.secureAmount = reader.GetFloat();
		this.secure = reader.GetBool();
	}
}

public class NetworkedClimable : MonoBehaviour {
	private const float POSITION_EPSILON_SQR = 0.0004f;       // 位置变化阈值平方
	private const float ROTATION_EPSILON = 0.5f;             // 旋转变化阈值 (度)
	private const float SECURE_AMOUNT_EPSILON = 0.01f;        // 加固值变化阈值
	// 引用
	public ClimbableData data = new();
	public CL_Handhold Handhold { get; private set; }
	// 访问简化属性
	public NetworkObjectId NetworkId => data.NetworkId;
	public IDType OwnerId => data.NetworkId.OwnerId;
	public int InstanceId => data.NetworkId.InstanceId;
	public bool IsValid => data.NetworkId.InstanceId != 0;

	#region[Unity生命周期]

	private void Awake() {
		Handhold = GetComponent<CL_Handhold>() ?? GetComponentInChildren<CL_Handhold>(true);
		ClimbableSyncModule.RegisterLookup(Handhold, this);
	}

	private void OnDestroy() {
		ClimbableSyncModule.UnregisterLookup(Handhold);
	}

	#endregion

	/// <summary>
	/// 初始化并绑定持久化数据结构
	/// </summary>
	public void BindData(ClimbableData data) {
		this.data = data;
		transform.SetPositionAndRotation(data.position, data.rotation);
		if (Handhold != null) {
			Handhold.Initialize();
			Handhold.secureAmount = data.secureAmount;
			Handhold.secure = data.secure;
		}
	}

	/// <summary>
	/// 检查敌人状态是否有足够明显的变化需要同步
	/// </summary>
	public bool HasMeaningfulChange {
		get {
			// 位置变化
			if ((transform.position - data.position).sqrMagnitude > POSITION_EPSILON_SQR) return true;
			// 旋转变化
			if (Quaternion.Angle(transform.rotation, data.rotation) > ROTATION_EPSILON) return true;
			// 生命值变化
			if (Handhold != null) {
				if (Mathf.Abs(data.secureAmount - Handhold.secureAmount) > SECURE_AMOUNT_EPSILON) return true;
				if (data.secure != Handhold.secure) return true;
			}

			return false;
		}
	}

	/// <summary>
	/// 刷新上次同步的快照状态
	/// </summary>
	public void RememberState() {
		data.position = transform.position;
		data.rotation = transform.rotation;
		if (Handhold != null) {
			data.secure = Handhold.secure;
			data.secureAmount = Handhold.secureAmount;
		}
	}
}