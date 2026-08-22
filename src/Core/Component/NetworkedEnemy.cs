using UnityEngine;

namespace WKMPMod.Component;

public class NetworkedEnemy : MonoBehaviour {
	private const float PositionEpsilonSqr = 0.01f; // 位置变化阈值平方 (0.1m)
	private const float RotationEpsilonDegrees = 1.0f; // 旋转变化阈值 (度)
	private const float HealthEpsilon = 0.01f; // 生命值变化阈值

	public ulong networkId;
	public float lastHealth = float.NaN;
	public Vector3 lastPosition;
	public Quaternion lastRotation;

	// 缓存组件, 避免频繁 GetComponent / 反射
	public GameEntity Entity { get; private set; }

	private void Awake() {
		Entity = GetComponent<GameEntity>();
		lastPosition = transform.position;
		lastRotation = transform.rotation;
		lastHealth = currentHealth;
	}

	/// <summary>
	/// 获取当前实体生命值
	/// </summary>
	public float currentHealth {
		get {
			if (Entity == null) return float.NaN;
			return Entity.health; // 如果原版 health 是私有的, 可在此处统一使用 AccessTools 读取
		}
		set {
			if (Entity != null) Entity.health = value;
		}
	}

	/// <summary>
	/// 检查敌人状态是否有足够明显的变化需要同步
	/// </summary>
	public bool HasMeaningfulChange() {
		// 位置变化
		if ((transform.position - lastPosition).sqrMagnitude > PositionEpsilonSqr) return true;

		// 旋转变化
		if (Quaternion.Angle(transform.rotation, lastRotation) > RotationEpsilonDegrees) return true;

		// 生命值变化
		float hp = currentHealth;
		if (float.IsNaN(hp) != float.IsNaN(lastHealth)) return true;
		if (!float.IsNaN(hp) && Mathf.Abs(hp - lastHealth) > HealthEpsilon) return true;

		return false;
	}

	/// <summary>
	/// 检查敌人是否已被移除/死亡
	/// </summary>
	public bool IsRemoved() {
		if (this == null || gameObject == null || !gameObject.activeInHierarchy) return true;
		float hp = currentHealth;
		return !float.IsNaN(hp) && hp <= 0f;
	}

	/// <summary>
	/// 刷新上次同步的快照状态
	/// </summary>
	public void RememberState() {
		lastPosition = transform.position;
		lastRotation = transform.rotation;
		lastHealth = currentHealth;
	}

	/// <summary>
	/// 应用网络发来的远程状态
	/// </summary>
	public void ApplyRemoteState(Vector3 position, Quaternion rotation, float health) {
		transform.SetPositionAndRotation(position, rotation);
		if (!float.IsNaN(health)) {
			currentHealth = health;
		}
		if (!gameObject.activeSelf) gameObject.SetActive(true);
		RememberState();
	}
}
