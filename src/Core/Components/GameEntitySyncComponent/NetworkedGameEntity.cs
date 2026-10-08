using DarkMachine.AI;
using System;
using System.Collections.Generic;
using System.Reflection;
using Unity.Entities;
using UnityEngine;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.NetWork;
using WKMPMod.Util;
using WKMPMod.World;

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

	public ulong networkId;

	#region[组件缓存]
	public GameEntity Entity { get; private set; }
	public AIGameEntity AIEntity { get; private set; }
	public DEN_BasicDenizen BasicDenizen { get; private set; }
	public DEN_BasicNavigator BasicNavigator { get; private set; }
	public Rigidbody Rb { get; private set; }

	public EntityType Type { get; private set; }

	#endregion

	#region [网络接管与模块管理]
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

	#region[AITargetComponent反射]

	// 反射缓存
	private static readonly Dictionary<Type, FieldInfo> _targetCompFieldCache = new();

	/// <summary>
	/// 从 GameEntity 及其挂载组件中查找并提取 AITargetComponent
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
						fieldInfo = field;
						break;
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

		// 主机启动生物对远端玩家扩展
		if (MPSteamworks.IsHost) EnableRemotePlayerTargeting();
	}

	private void Update() {
		if (!IsNetworkControlled) return;
		// 逐帧驱动所有 Feature 自身的 Update (例如平滑插值)
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

	private void ClearAllFeatures() {
		for (int i = 0; i < _syncFeatures.Count; i++) 
			_syncFeatures[i]?.OnDestroy();
		_syncFeatures.Clear();
	}

	#endregion

	#region [分模块网络序列化与反序列化]
	/// <summary>
	/// (Host 侧) 只序列化发生变动 (IsDirty == true) 的模块数据
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
	/// (Client 侧) 仅反序列化包内包含的变动模块
	/// </summary>
	public void ReadSyncState(DataReader reader) {
		byte dirtyCount = reader.GetByte();

		for (int i = 0; i < dirtyCount; i++) {
			byte featureIndex = reader.GetByte();
			if (featureIndex < _syncFeatures.Count) 
				_syncFeatures[featureIndex].ReadState(reader);
		}
	}

	#endregion

	#region [AnimatorInterceptor 接口适配]

	public void ApplyRemoteTrigger(byte featureIndex, int hashId) {
		if (featureIndex < _syncFeatures.Count && _syncFeatures[featureIndex] is AnimatorSyncFeature animFeature) 
			animFeature.ApplyTrigger(hashId);
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

	/// <summary>
	/// 使生物可以索敌其他远程玩家
	/// </summary>
	private void EnableRemotePlayerTargeting() {
		// 视线组件修改：添加图层 15(Creature) 到 AI 视线检测
		if (AIEntity?.sight != null) {
			AIEntity.sight.sightMask.value |= (1 << 15);
		}

		// 目标追踪组件修改
		// 无人机特化
		if (AIEntity is DEN_Drone drone) {
			// 添加 "RemotePlayer" 扫描标签
			if (drone.targetTags == null) {
				drone.targetTags = new[] { "RemotePlayer" };
			} else if (Array.IndexOf(drone.targetTags, "RemotePlayer") < 0) {
				int oldLen = drone.targetTags.Length;
				Array.Resize(ref drone.targetTags, oldLen + 1);
				drone.targetTags[oldLen] = "RemotePlayer";
			}
			return;
		}

		// 气囊特化
		if (AIEntity is DEN_Gasbag gasbag) {
			// 添加 "RemotePlayer" 扫描标签
			if (gasbag.explodeTags == null) {
				gasbag.explodeTags = new[] { "RemotePlayer" };
			} else if (Array.IndexOf(gasbag.explodeTags, "RemotePlayer") < 0) {
				int oldLen = gasbag.explodeTags.Length;
				Array.Resize(ref gasbag.explodeTags, oldLen + 1);
				gasbag.explodeTags[oldLen] = "RemotePlayer";
			}
			return;
		}

		// 通用AITargetComponent搜寻修改
		if (TryGetAITargetComponent(Entity, out var targetComp)) {
			targetComp.targetScanMask.value |= (1 << 15);

			// 添加 "RemotePlayer" 扫描标签
			if (targetComp.targetScanTags == null) {
				targetComp.targetScanTags = new[] { "RemotePlayer" };
			} else if (Array.IndexOf(targetComp.targetScanTags, "RemotePlayer") < 0) {
				int oldLen = targetComp.targetScanTags.Length;
				Array.Resize(ref targetComp.targetScanTags, oldLen + 1);
				targetComp.targetScanTags[oldLen] = "RemotePlayer";
			}
			return;
		}
	}

	public bool IsRemoved() {
		if (this == null || gameObject == null || !gameObject.activeInHierarchy) return true;
		float hp = Entity != null ? Entity.health : float.NaN;
		return !float.IsNaN(hp) && hp <= 0f;
	}

	#endregion
}