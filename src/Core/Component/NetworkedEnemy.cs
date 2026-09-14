using DarkMachine.AI;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Unity.Entities;
using UnityEngine;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.NetWork;

namespace WKMPMod.Components;

public class NetworkedEnemy : MonoBehaviour {
	#region[类型信息类]
	public enum EntityType {
		GenericEntity,  // 仅 GameEntity
		AIEntity,       // 继承 AIGameEntity
		BasicDenizen,   // 包含 DEN_BasicDenizen 完整 AI 运动
		BasicNavigator  // 包含 DEN_BasicNavigator 完整 AI 运动
	}
	#endregion

	public ulong networkId;

	#region[位置/血量 变动记录]

	private const float PositionEpsilonSqr = 0.01f; // 位置变化阈值平方 (0.1m)
	private const float RotationEpsilonDegrees = 1.0f; // 旋转变化阈值 (度)
	private const float HealthEpsilon = 0.01f; // 生命值变化阈值
	public float lastHealth = float.NaN;
	public Vector3 lastPosition;
	public Quaternion lastRotation;

	// 获取当前实体生命值
	public float CurrentHealth {
		get {
			if (Entity == null) return float.NaN;
			return Entity.health;
		}
	}

	// 检查敌人状态是否有足够明显的变化需要同步
	public bool HasMeaningfulChange => (transform.position - lastPosition).sqrMagnitude > PositionEpsilonSqr
			|| Quaternion.Angle(transform.rotation, lastRotation) > RotationEpsilonDegrees
			|| float.IsNaN(CurrentHealth) != float.IsNaN(lastHealth)
			|| (!float.IsNaN(CurrentHealth) && Mathf.Abs(CurrentHealth - lastHealth) > HealthEpsilon);

	#endregion

	#region[Animator 状态 变动记录]

	// 记录本地修改但尚未同步给客户端的 Animator 变动
	private readonly Dictionary<int, bool> _dirtyBools = new();
	private readonly Dictionary<int, float> _dirtyFloats = new();
	private readonly Dictionary<int, int> _dirtyInts = new();
	private readonly Dictionary<int, float> _dirtyLayerWeights = new();

	public bool HasAnimDirty => _dirtyBools.Count > 0 || _dirtyFloats.Count > 0 || _dirtyInts.Count > 0 || _dirtyLayerWeights.Count > 0;

	#endregion

	#region[组件缓存]
	public GameEntity Entity { get; private set; }
	public AIGameEntity AIEntity { get; private set; }
	public DEN_BasicDenizen BasicDenizen { get; private set; }
	public DEN_BasicNavigator BasicNavigator { get; private set; }
	public Animator EntityAnimator { get; private set; }
	public Rigidbody Rb { get; private set; }

	public EntityType Type { get; private set; }

	#endregion

	#region[网络接管]

	private const float TIMEOUT_DURATION = 2.0f;    // 接管时间
	private bool _lastUseGravity = false;
	private float _lastDataRecvTime;
	public bool IsNetworkControlled { get; private set; }   // 是否由网络接管控制

	#endregion

	#region[插值数据]
	private Vector3 _targetPosition;
	private Quaternion _targetRotation;
	private const float LERP_SPEED = 15f;
	#endregion

	#region[AITargetComponent反射]

	// 反射缓存
	private static readonly Dictionary<Type, FieldInfo> _targetCompFieldCache = new();

	/// <summary>
	/// 单函数完成: 从 GameEntity 及其挂载组件中查找并提取 AITargetComponent
	/// </summary>
	public static bool TryGetAITargetComponent(GameEntity entity, out AITargetComponent targetComp) {
		targetComp = null;
		if (entity == null) return false;
		Type type = entity.GetType();

		// 查表或反射获取 FieldInfo (沿继承链向上查找)
		if (!_targetCompFieldCache.TryGetValue(type, out var fieldInfo)) {
			for (var current = type; current != null && current != typeof(object); current = current.BaseType) {
				foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)) {
					if (typeof(AITargetComponent).IsAssignableFrom(field.FieldType)) {
						fieldInfo = field; break;
					}
				}
				if (fieldInfo != null) break;
			}
			_targetCompFieldCache[type] = fieldInfo;
		}

		// 如果存在匹配字段且成功取到实例, 直接返回 true
		if (fieldInfo != null && fieldInfo.GetValue(entity) is AITargetComponent result) {
			targetComp = result;
			return true;
		}
		return false;
	}

	#endregion

	#region[Unity生命周期]

	private void Awake() {
		Entity = GetComponent<GameEntity>();
		AIEntity = GetComponent<AIGameEntity>();
		BasicDenizen = GetComponent<DEN_BasicDenizen>();
		BasicNavigator = GetComponent<DEN_BasicNavigator>();
		EntityAnimator = GetComponentInChildren<Animator>();
		Rb = GetComponent<Rigidbody>();

		// 判断类型并缓存
		if (BasicDenizen != null) {
			Type = EntityType.BasicDenizen;
		} else if (BasicNavigator != null) {
			Type = EntityType.BasicNavigator;
		} else if (AIEntity != null) {
			Type = EntityType.AIEntity;
		} else if (Entity != null) {
			Type = EntityType.GenericEntity;
		} else {
			MPMain.LogError("NetworkedEnemy.Awake: not have GameEntity");
			return;
		}
		lastPosition = transform.position;
		lastRotation = transform.rotation;
		lastHealth = CurrentHealth;

		// 视线组件修改
		if (AIEntity != null && AIEntity.sight != null) {
			// 添加自定义图层 15(Creature) 到 AI 视线检测
			AIEntity.sight.sightMask.value |= (1 << 15);
		}
		// 目标追踪组件修改
		if (TryGetAITargetComponent(Entity,out var targetComp)) {
			// 添加自定义图层 15(Creature) 到 AI 视线检测
			targetComp.targetScanMask.value |= (1 << 15);
			// 添加RemotePlayer作为目标扫描标签
			if (targetComp.targetScanTags == null) {
				targetComp.targetScanTags = new string[] { "RemotePlayer" };
			} else if (Array.IndexOf(targetComp.targetScanTags, "RemotePlayer") < 0) {
				string[] currentTags = targetComp.targetScanTags;
				string[] newTags = new string[currentTags.Length + 1];
				Array.Copy(currentTags, newTags, currentTags.Length);
				newTags[currentTags.Length] = "RemotePlayer";
				targetComp.targetScanTags = newTags;
			}
		}
	}

	private void Update() {
		if (!MPCore.CanSync) return;
		// 主机不需要同步
		if (!MPSteamworks.IsHost) {
			// 数据超时恢复控制
			if (IsNetworkControlled && Time.time - _lastDataRecvTime > TIMEOUT_DURATION)
				SetNetworkControl(false);
			// 进行数据插值
			if (IsNetworkControlled) {
				transform.position = Vector3.Lerp(transform.position, _targetPosition, Time.deltaTime * LERP_SPEED);
				transform.rotation = Quaternion.Slerp(transform.rotation, _targetRotation, Time.deltaTime * LERP_SPEED);
			}
		} else {

		}
	}

	#endregion

	#region[Animator 序列化与数据应用]

	public void SetAnimBoolDirty(int hash, bool value) {
		_dirtyBools[hash] = value;
	}

	public void SetAnimFloatDirty(int hash, float value) {
		_dirtyFloats[hash] = value;
	}

	public void SetAnimIntDirty(int hash, int value) {
		_dirtyInts[hash] = value;
	}

	public void SetAnimLayerWeightDirty(int layer, float weight) {
		_dirtyLayerWeights[layer] = weight;
	}

	/// <summary>
	/// 主机序列化并清空 Dirty 状态
	/// </summary>
	public void WriteAndClearAnimState(DataWriter writer) {
		// Bool
		writer.Put((byte)_dirtyBools.Count);
		foreach (var kvp in _dirtyBools) {
			writer.Put(kvp.Key);
			writer.Put(kvp.Value);
		}
		_dirtyBools.Clear();

		// Float
		writer.Put((byte)_dirtyFloats.Count);
		foreach (var kvp in _dirtyFloats) {
			writer.Put(kvp.Key);
			writer.Put(kvp.Value);
		}
		_dirtyFloats.Clear();

		// Int
		writer.Put((byte)_dirtyInts.Count);
		foreach (var kvp in _dirtyInts) {
			writer.Put(kvp.Key);
			writer.Put(kvp.Value);
		}
		_dirtyInts.Clear();

		// LayerWeight
		writer.Put((byte)_dirtyLayerWeights.Count);
		foreach (var kvp in _dirtyLayerWeights) {
			writer.Put(kvp.Key); // layer index
			writer.Put(kvp.Value); // weight
		}
		_dirtyLayerWeights.Clear();
	}

	/// <summary>
	/// 客机接收并应用远程 Animator 状态
	/// </summary>
	public void ApplyRemoteAnimState(DataReader reader) {
		if (EntityAnimator == null) return;

		// Bool
		byte boolCount = reader.GetByte();
		for (int i = 0; i < boolCount; i++) {
			int hash = reader.GetInt();
			bool val = reader.GetBool();
			EntityAnimator.SetBool(hash, val);
		}

		// Float
		byte floatCount = reader.GetByte();
		for (int i = 0; i < floatCount; i++) {
			int hash = reader.GetInt();
			float val = reader.GetFloat();
			EntityAnimator.SetFloat(hash, val);
		}

		// Int
		byte intCount = reader.GetByte();
		for (int i = 0; i < intCount; i++) {
			int hash = reader.GetInt();
			int val = reader.GetInt();
			EntityAnimator.SetInteger(hash, val);
		}

		// LayerWeight
		byte layerCount = reader.GetByte();
		for (int i = 0; i < layerCount; i++) {
			int layer = reader.GetInt();
			float weight = reader.GetFloat();
			EntityAnimator.SetLayerWeight(layer, weight);
		}
	}

	#endregion

	/// <summary>
	/// 检查敌人是否已被移除/死亡
	/// </summary>
	public bool IsRemoved() {
		if (this == null || gameObject == null || !gameObject.activeInHierarchy) return true;
		float hp = CurrentHealth;
		return !float.IsNaN(hp) && hp <= 0f;
	}

	/// <summary>
	/// 刷新上次同步的快照状态
	/// </summary>
	public void RememberState() {
		lastPosition = transform.position;
		lastRotation = transform.rotation;
		lastHealth = CurrentHealth;
	}

	/// <summary>
	/// 应用网络发来的远程状态
	/// </summary>
	public void ApplyRemoteState(Vector3 position, Quaternion rotation, float health) {
		_lastDataRecvTime = Time.time;

		if (!IsNetworkControlled && !MPSteamworks.IsHost) SetNetworkControl(true);

		_targetPosition = position;
		_targetRotation = rotation;

		RememberState();

		if (Entity != null && !float.IsNaN(health)) {
			Entity.health = health;
		}

		if (!gameObject.activeSelf) gameObject.SetActive(true);
	}

	/// <summary>
	/// 设置/切换网络控制接管状态
	/// true 为关闭本地 AI 与物理控制,由网络数据驱动运动
	/// </summary>
	public void SetNetworkControl(bool state) {
		IsNetworkControlled = state;

		// 根据缓存的类型执行差异化接管
		switch (Type) {
			case EntityType.BasicDenizen:
				// 关闭 BasicDenizen 内部 active 开关,阻止运动模块改写 Transform
				BasicDenizen.active = !state;
				BasicDenizen.disableAI = state;
				break;
			case EntityType.BasicNavigator:
				BasicNavigator.locked = state;
				BasicNavigator.disableAI = state;
				break;
			case EntityType.AIEntity:
				// AIGameEntity 提供了原生 disableAI 字段
				AIEntity.disableAI = state;
				break;
			case EntityType.GenericEntity:
				// 普通 GameEntity 无需特殊 AI 关闭操作
				break;
		}

		// 统一关闭刚体物理影响, 防止网络同步与本地碰撞拉扯
		if (Rb != null) {
			if (state) {
				_lastUseGravity = Rb.useGravity;
				Rb.useGravity = false;
			} else Rb.useGravity = _lastUseGravity;
			Rb.isKinematic = state;
		}
	}
}
