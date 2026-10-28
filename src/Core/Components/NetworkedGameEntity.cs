using DarkMachine.AI;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.NetWork;

namespace WKMPMod.Components;
public class NetworkedGameEntity : MonoBehaviour {
	#region [类型信息类]
	public enum EntityType {
		GenericEntity,  // 仅 GameEntity
		AIEntity,       // 继承 AIGameEntity
		BasicDenizen,   // 包含 DEN_BasicDenizen 完整 AI 运动
		BasicNavigator  // 包含 DEN_BasicNavigator 完整 AI 运动
	}
	#endregion

	#region [网络标签与身份判断]

	public ulong networkId;
	// 默认为无主状态 (0 或特定的无效 ID)
	public IDType CurrentOwnerId { 
		get => _currentOwnerId; 
		set {
			_currentOwnerId = value; 
			IsOwnedByMe = value == MPSteamworks.UserSteamId; 
		} }
	private IDType _currentOwnerId = 0;
	// 便捷判断属性
	public bool HasOwner => _currentOwnerId != 0;
	public bool IsOwnedByMe { get; private set; } = false;

	#endregion

	#region[组件缓存]
	public GameEntity Entity { get; private set; }
	public AIGameEntity AIEntity { get; private set; }
	public DEN_BasicDenizen BasicDenizen { get; private set; }
	public DEN_BasicNavigator BasicNavigator { get; private set; }
	public Rigidbody Rb { get; private set; }

	public EntityType Type { get; private set; }

	#endregion

	#region [网络接管与模块管理]
	private const float TIMEOUT_DURATION = 2.0f; // 2 秒未收到任何网络包则超时
	private float _lastDataRecvTime;
	public bool IsNetworkControlled { get; private set; } = false;
	private bool _lastUseGravity = false;

	// 统一同步模块列表
	private readonly List<ISyncFeature> _syncFeatures = new();
	public IReadOnlyList<ISyncFeature> SyncFeatures => _syncFeatures;

	/// <summary>
	/// 检查实体是否有任意子模块是否需要更新
	/// </summary>
	public bool HasMeaningfulChange {
		get {
			for (int i = 0; i < _syncFeatures.Count; i++) {
				if (_syncFeatures[i].IsDirty) return true;
			}
			return false;
		}
	}
	#endregion

	#region[Unity生命周期]

	private void Awake() {
		Entity = GetComponent<GameEntity>();

		if (Entity is DEN_BasicDenizen basicDenizen) {
			BasicDenizen = basicDenizen;
			AIEntity = basicDenizen;
			Type = EntityType.BasicDenizen;
		} else if (Entity is DEN_BasicNavigator basicNavigator) {
			BasicNavigator = basicNavigator;
			AIEntity = basicNavigator;
			Type = EntityType.BasicNavigator;
		} else if (Entity is AIGameEntity aiGameEntity) {
			AIEntity = aiGameEntity;
			Type = EntityType.AIEntity;
		} else if (Entity != null) {
			Type = EntityType.GenericEntity;
		} else {
			MPMain.LogError("NetworkedEnemy.Awake: not have GameEntity");
			return;
		}

		Rb = GetComponent<Rigidbody>();
	}

	private void Update() {
		// 超时检测: 如果超过 TIMEOUT_DURATION 秒未收到任何网络包, 则取消网络接管
		if (!MPSteamworks.IsHost && IsNetworkControlled)
			if (Time.time - _lastDataRecvTime > TIMEOUT_DURATION) 
				SetNetworkControl(false);

		// 逐帧驱动所有 Feature 自身的 OnUpdate (例如平滑插值)
		float dt = Time.deltaTime;
		for (int i = 0; i < _syncFeatures.Count; i++) {
			_syncFeatures[i].OnUpdate(dt);
		}

	}

	private void OnDestroy() {
		// 实体被销毁时触发所有 Feature 的注销逻辑
		ClearAllFeatures();
	}

	#endregion

	#region [模块化注册]

	/// <summary>
	/// 初始化并自动注册各子模块 Feature
	/// </summary>
	public void Initialize(ulong id) {
		networkId = id;

		ClearAllFeatures();
		// 根 Transform 与血量同步
		RegisterFeature(new RootTransformSyncFeature(this));
		// AI 目标行为统一由 TargetSyncFeature 管理
		if (AIEntity != null) {
			TargetSyncFeature.TryGetAITargetComponent(Entity, out var targetComponent);
			RegisterFeature(new TargetSyncFeature(targetComponent, this));
		}
		// 扫描注册所有 Animator Feature
		var animators = GetComponentsInChildren<Animator>(true);
		for (byte i = 0; i < animators.Length; i++) {
			var anim = animators[i];
			var animFeature = new AnimatorSyncFeature(anim, this);
			RegisterFeature(animFeature);
		}
		// VentThing 特化处理子 Transform (手部)
		if (Entity is DEN_VentThing) {
			var handTransform = transform.Find("Hand_Root");
			if (handTransform != null) {
				RegisterFeature(new SubTransformSyncFeature(handTransform));
			}
		}
	}

	// 注册模块
	private void RegisterFeature(ISyncFeature feature) {
		feature.FeatureIndex = (byte)_syncFeatures.Count;
		_syncFeatures.Add(feature);
	}

	// 清理所有模块
	private void ClearAllFeatures() {
		for (int i = 0; i < _syncFeatures.Count; i++) 
			_syncFeatures[i]?.OnDestroy();
		_syncFeatures.Clear();
	}

	#endregion

	#region [分模块网络序列化与反序列化]
	/// <summary>
	/// 只序列化发生变动 (IsDirty == true) 的模块数据
	/// </summary>
	public void WriteSyncState(DataWriter writer) {
		// 统计脏模块数量
		byte dirtyCount = 0;
		for (int i = 0; i < _syncFeatures.Count; i++) {
			if (_syncFeatures[i].IsDirty) dirtyCount++;
		}

		// 写入脏模块总数
		writer.Put(dirtyCount);

		// 稀疏写入：仅写入 [FeatureIndex + 模块数据]
		for (int i = 0; i < _syncFeatures.Count; i++) {
			var feature = _syncFeatures[i];
			if (feature.IsDirty) {
				writer.Put(feature.FeatureIndex);
				feature.WriteState(writer);
			}
		}
	}

	/// <summary>
	/// 序列化所有模块的完整数据
	/// </summary>
	public void WriteFullSyncState(DataWriter writer) {
		// 写入全部模块总数
		writer.Put((byte)_syncFeatures.Count);

		// 全量写入：无视 IsDirty，强制写入所有 [FeatureIndex + 模块数据]
		for (int i = 0; i < _syncFeatures.Count; i++) {
			var feature = _syncFeatures[i];
			writer.Put(feature.FeatureIndex);
			feature.WriteState(writer);
		}
	}

	/// <summary>
	/// 仅反序列化包内包含的变动模块
	/// </summary>
	public void ReadSyncState(DataReader reader) {
		// 刷新数据接收时间
		_lastDataRecvTime = Time.time;
		// 客机端开启网络接管
		if (!IsOwnedByMe && !IsNetworkControlled) {
			SetNetworkControl(true);
		}
		// 反序列化包内包含的变动 Feature 模块
		byte dirtyCount = reader.GetByte();

		for (int i = 0; i < dirtyCount; i++) {
			byte featureIndex = reader.GetByte();
			if (featureIndex < _syncFeatures.Count) 
				_syncFeatures[featureIndex].ReadState(reader);
		}
	}

	#endregion

	#region [网络控制与 AI 修改]

	/// <summary>
	/// 设置/切换网络控制接管状态
	/// true 为关闭本地 AI 与物理控制,由网络数据驱动运动
	/// </summary>
	public void SetNetworkControl(bool state) {
		if (IsNetworkControlled == state) return;

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

	public bool IsRemoved() {
		if (this == null || gameObject == null || !gameObject.activeInHierarchy) return true;
		float hp = Entity != null ? Entity.health : float.NaN;
		return !float.IsNaN(hp) && hp <= 0f;
	}

	#endregion
}