using Unity.Entities;
using UnityEngine;
using UnityEngine.UIElements;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.Util;
using WKMPMod.World;
using static WKMPMod.Data.PlayerData;

namespace WKMPMod.Component;

public class NetworkedClimable : MonoBehaviour {
	private const float positionEpsilonSqr = 0.0004f;       // 位置变化阈值平方
	private const float rotationEpsilon = 0.5f;             // 旋转变化阈值 (度)
	private const float secureAmountEpsilon = 0.01f;        // 加固值变化阈值

	public ClimbableData data = new();
	public CL_Handhold Handhold { get; private set; }

	public ulong NetworkId => data?.networkId ?? 0;
	public bool IsValid => NetworkId != 0;

	private void Awake() {
		Handhold = GetComponent<CL_Handhold>() ?? GetComponentInChildren<CL_Handhold>(true);
		ClimbableSyncModule.RegisterLookup(Handhold, this);
	}

	private void Start() {
		transform.SetPositionAndRotation(data.position, data.rotation);
		if (Handhold != null) {
			Handhold.Initialize();
			Handhold.secureAmount = data.secureAmount;
			Handhold.secure = data.secure;
		}
	}

	private void OnDestroy() {
		ClimbableSyncModule.UnregisterLookup(Handhold);
	}

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
	public bool HasMeaningfulChange() {
		// 位置变化
		if ((transform.position - data.position).sqrMagnitude > positionEpsilonSqr) return true;

		// 旋转变化
		if (Quaternion.Angle(transform.rotation, data.rotation) > rotationEpsilon) return true;

		// 生命值变化
		if (Handhold != null) {
			if (Mathf.Abs(data.secureAmount - Handhold.secureAmount) > secureAmountEpsilon) return true;
			if (data.secure != Handhold.secure) return true;
		}

		return false;
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

/// <summary>
/// 可攀爬物体的持久化数据结构 (脱离 MonoBehaviour 独立存在)
/// </summary>
public class ClimbableData: INetworkSerializable {
	public ulong networkId;
	public string prefabKey;
	public ulong ownerId;

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
		writer.Put(networkId);
		writer.Put(prefabKey);
		writer.Put(ownerId);
		writer.Put(position);
		writer.Put(rotation);
		writer.Put(secureAmount);
		writer.Put(secure);
	}

	public void Deserialize(DataReader reader) {
		this.networkId = reader.GetULong();
		this.prefabKey = reader.GetString();
		this.ownerId = reader.GetULong();
		this.position = reader.GetVector3();
		this.rotation = reader.GetQuaternion();
		this.secureAmount = reader.GetFloat();
		this.secure = reader.GetBool();
	}
}