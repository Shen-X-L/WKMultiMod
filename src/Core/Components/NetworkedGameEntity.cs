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
		BasicNavigator,	// 包含 DEN_BasicNavigator 完整 AI 运动
		Teeth,          // 包含 DEN_Teeth
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
	private readonly List<ISyncFeature> _features = new();
	// 状态发送模块
	private readonly List<IStateSyncFeature> _stateSyncFeatures = new();
	// 帧更新模块
	private readonly List<IFrameUpdateFeature> _frameUpdateFeatures = new();
	// 自定义消息 类RPC模块
	private readonly List<ICustomSyncFeature> _customSyncFeatures = new();
	private readonly List<IInitialSyncFeature> _initialSyncFeatures = new();
	public IReadOnlyList<ISyncFeature> Features => _features;
	public IReadOnlyList<IStateSyncFeature> StateSyncFeatures => _stateSyncFeatures;
	public IReadOnlyList<ICustomSyncFeature> CustomSyncFeatures => _customSyncFeatures;
	public IReadOnlyList<IInitialSyncFeature> InitialSyncFeatures => _initialSyncFeatures;

	/// <summary>
	/// 检查实体是否有任意子模块是否需要更新
	/// </summary>
	public bool HasMeaningfulChange {
		get {
			for (int i = 0; i < _stateSyncFeatures.Count; i++) 
				if (_stateSyncFeatures[i].IsDirty) return true;
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
		}else if(Entity is DEN_Teeth teeth) {
			AIEntity = teeth;
			Type = EntityType.Teeth;
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
		if (!IsOwnedByMe && IsNetworkControlled)
			if (Time.time - _lastDataRecvTime > TIMEOUT_DURATION) 
				SetNetworkControl(false);

		// 逐帧驱动所有 Feature 自身的 OnUpdate (例如平滑插值)
		OnFrameUpdate(Time.deltaTime);
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
			RegisterFeature(new TargetSyncFeature(AIEntity, this));
		}
		// 扫描注册所有 Animator Feature
		var animators = GetComponentsInChildren<Animator>(true);
		for (byte i = 0; i < animators.Length; i++) {
			var anim = animators[i];
			RegisterFeature(new AnimatorSyncFeature(anim, this));
		}

		switch (Entity) {
			// Phage 特化处理子
			case DEN_Phage phage:
				var phageSubTransform = transform.Find("DEN_Phage");
				RegisterFeature(new PhageSyncFeature(phage, this));
				RegisterFeature(new SubTransformSyncFeature(phageSubTransform,this));
				break;
			// VentThing 特化处理子 Transform (手部)
			case DEN_VentThing:
				var handTransform = transform.Find("Hand_Root");
				if (handTransform != null) RegisterFeature(new SubTransformSyncFeature(handTransform, this));
				break;
		}
	}

	// 注册模块
	private void RegisterFeature(ISyncFeature feature) {
		if (feature == null) return;
		if (_features.Contains(feature)) return;
		// 添加模块
		_features.Add(feature);
		if (feature is IStateSyncFeature stateFeature) {
			stateFeature.StateIndex = (byte)_stateSyncFeatures.Count;
			_stateSyncFeatures.Add(stateFeature);
		}
		if (feature is IFrameUpdateFeature frameFeature) _frameUpdateFeatures.Add(frameFeature);
		if (feature is ICustomSyncFeature customFeature) {
			customFeature.CustomIndex = (byte)_customSyncFeatures.Count;
			_customSyncFeatures.Add(customFeature);
		}
		if (feature is IInitialSyncFeature initialFeature) {
			initialFeature.InitialIndex = (byte)_initialSyncFeatures.Count;
			_initialSyncFeatures.Add(initialFeature);
		}
	}

	// 清理所有模块
	private void ClearAllFeatures() {
		for (int i = 0; i < _features.Count; i++) _features[i]?.OnDestroy();
		_features.Clear();
		_stateSyncFeatures.Clear();
		_frameUpdateFeatures.Clear();
		_customSyncFeatures.Clear();
		_initialSyncFeatures.Clear();
	}

	#endregion

	#region [IState序列化]

	/// <summary>
	/// 只序列化发生变动 (IsDirty == true) 的模块数据
	/// </summary>
	public void WriteSyncState(DataWriter writer) {
		// 统计脏模块数量
		byte dirtyCount = 0;
		for (int i = 0; i < _stateSyncFeatures.Count; i++) 
			if (_stateSyncFeatures[i].IsDirty) dirtyCount++;
		// 写入脏模块总数
		writer.Put(dirtyCount);
		// 稀疏写入: 仅写入 [FeatureIndex + 模块数据]
		for (int i = 0; i < _stateSyncFeatures.Count; i++) {
			var feature = _stateSyncFeatures[i];
			if (!feature.IsDirty) continue;
			// 写入数据
			writer.Put(feature.StateIndex);
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
		if (!IsOwnedByMe && !IsNetworkControlled) SetNetworkControl(true);
		// 反序列化包内包含的变动 Feature 模块
		byte dirtyCount = reader.GetByte();
		for (int i = 0; i < dirtyCount; i++) {
			byte featureIndex = reader.GetByte();
			if (featureIndex < _stateSyncFeatures.Count)
				_stateSyncFeatures[featureIndex].ReadState(reader);
		}
	}

	#endregion

	#region[ICustom查询]

	public bool TryGetCustomFeature(byte index, out ICustomSyncFeature feature) {
		feature = null;
		if (index >= 0 && index < _customSyncFeatures.Count) {
			feature = _customSyncFeatures[index];
			return true;
		}
		return false;
	}

	#endregion

	#region[IFrameUpdate更新]

	public void OnFrameUpdate(float deltaTime) {
		for (int i = 0; i < _frameUpdateFeatures.Count; i++)
			_frameUpdateFeatures[i].OnUpdate(deltaTime);
	}

	#endregion

	#region[IInitial初始化]

	public void WriteInitialSync(DataWriter writer) {
		// 序列化数据
		writer.Put((byte)_initialSyncFeatures.Count);
		for (int i = 0; i < _initialSyncFeatures.Count; i++) {
			var feature = _initialSyncFeatures[i];
			writer.Put(feature.InitialIndex);
			feature.WriteInitialState(writer);
		}
	}

	public void ReadInitialSync(DataReader reader) {
		// 切换为网络接管模式
		if (!IsOwnedByMe && !IsNetworkControlled) SetNetworkControl(true);
		// 反序列化并应用数据
		byte featureCount = reader.GetByte();
		for (int i = 0; i < featureCount; i++) {
			byte featureIndex = reader.GetByte();
			if (featureIndex < _initialSyncFeatures.Count)
				_initialSyncFeatures[featureIndex].ReadInitialState(reader);
		}
	}

	#endregion

	#region [AI 修改]

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
			case EntityType.Teeth:
				if(AIEntity is DEN_Teeth Teeth) Teeth.disableAI = state;
				AIEntity.disableAI = state;
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

	#region[Debug]

	public string Debug() {
		return
			$"GameObjetName: {gameObject.name}, " +
			$"NetworkId: {networkId}, " +
			$"OwnerId: {_currentOwnerId}, " +
			$"HasOwner: {HasOwner}, " +
			$"IsOwnedByMe: {IsOwnedByMe}, " +
			$"EntityType: {Type}";
	}

	#endregion
}