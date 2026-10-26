using DarkMachine.AI;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Unity.Entities;
using UnityEngine;
using WKMPMod.Data;
using WKMPMod.NetWork;
using WKMPMod.World;

namespace WKMPMod.Components;

internal class TargetSyncFeature : ISyncFeature, ICustomSyncFeature {
	#region[Component对应Feature查找表]

	// 静态快速查找表：AITargetComponent -> 对应的 TargetSyncFeature
	private static readonly Dictionary<AITargetComponent, TargetSyncFeature> _targetComponentMap = new();

	public static void Register(AITargetComponent component, TargetSyncFeature feature) {
		if (component != null) _targetComponentMap[component] = feature;
	}

	public static void Unregister(AITargetComponent component) {
		if (component != null) _targetComponentMap.Remove(component);
	}

	public static void ClearAll() => _targetComponentMap.Clear();

	public static bool TryGetFeature(AITargetComponent component, out TargetSyncFeature feature) {
		feature = null;
		return component != null && _targetComponentMap.TryGetValue(component, out feature);
	}

	#endregion

	#region[AITargetComponent初始化配置]

	private static readonly Dictionary<Type, FieldInfo> _targetCompFieldCache = new();

	/// <summary>
	/// 沿 GameEntity 的继承链查找 AITargetComponent 字段
	/// </summary>
	public static bool TryGetAITargetComponent(GameEntity entity, out AITargetComponent targetComponent) {
		targetComponent = null;
		if (entity == null) return false;
		Type type = entity.GetType();
		// 查表 或 反射获取 FieldInfo (沿继承链向上查找)
		if (!_targetCompFieldCache.TryGetValue(type, out FieldInfo fieldInfo)) {
			for (Type current = type; current != null && current != typeof(object); current = current.BaseType) {
				FieldInfo[] fields = current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
				for (int i = 0; i < fields.Length; i++) {
					if (typeof(AITargetComponent).IsAssignableFrom(fields[i].FieldType)) {
						fieldInfo = fields[i];
						break;
					}
				}
				if (fieldInfo != null) break;
			}
			_targetCompFieldCache[type] = fieldInfo;
		}
		// 如果存在匹配字段且成功取到实例, 直接返回 true
		if (fieldInfo != null && fieldInfo.GetValue(entity) is AITargetComponent result) {
			targetComponent = result;
			return true;
		}
		return false;
	}

	private void EnableRemotePlayerTargeting() {
		// 视线组件修改：添加图层 15 (Creature) 到 AI 视线检测。
		if (Entity.AIEntity?.sight != null)
			Entity.AIEntity.sight.sightMask.value |= 1 << 15;

		// 无人机使用自己的 targetTags
		if (Entity.AIEntity is DEN_Drone drone) {
			AddTag(ref drone.targetTags, MPKeys.REMOTE_PLAYER_TAG);
			return;
		}

		// 气囊使用自己的 explodeTags
		if (Entity.AIEntity is DEN_Gasbag gasbag) {
			AddTag(ref gasbag.explodeTags, MPKeys.REMOTE_PLAYER_TAG);
			return;
		}

		if (TargetComponent == null) return;

		TargetComponent.targetScanMask.value |= 1 << 15;
		AddTag(ref TargetComponent.targetScanTags, MPKeys.REMOTE_PLAYER_TAG;
		AddTag(ref TargetComponent.targetIgnoreTags, $"Ignore:{Entity.networkId}");
	}

	private static void AddTag(ref string[] tags, string tag) {
		if (tags == null) {
			tags = new[] { tag };
			return;
		}
		if (Array.IndexOf(tags, tag) >= 0) return;
		Array.Resize(ref tags, tags.Length + 1);
		tags[tags.Length - 1] = tag;
	}

	#endregion

	public AITargetComponent TargetComponent { get; private set; }
	public NetworkedGameEntity Entity { get; }
	public TargetSyncFeature(AITargetComponent component, NetworkedGameEntity entity) {
		TargetComponent = component;
		Entity = entity;
		Register(TargetComponent, this);
		EnableRemotePlayerTargeting();
	}

	#region[ISyncFeature接口实现]

	public byte FeatureIndex { get; set; }

	public string FeatureId => $"TargetComponent_{Entity?.name}";

	public bool IsDirty => false;

	// 该 Feature 当前不参与常规状态序列化 完整同步时也必须安全地写入/读取空状态
	public void WriteState(DataWriter writer) { }
	public void ReadState(DataReader reader) { }
	public void OnUpdate(float deltaTime) { }
	public void OnReset() { }

	public void OnDestroy() {
		Unregister(TargetComponent);
	}

	#endregion

	#region[ICustomSyncFeature接口实现]

	public void HandleCustomMessage(ulong senderId, DataReader reader, bool needReply) {
		if (TargetComponent == null) return;
		var player = ENT_Player.GetPlayer();
		if (player != null) TargetComponent.SetTarget(player.transform);
	}

	public void HandleCustomReply(IDType senderId) {
		if (TargetComponent?.target?.targetEntity is not RemotePlayer remotePlayer
			|| remotePlayer.playerId != senderId) return;
		string removeTag = $"Ignore:{Entity.networkId}";
		// 设置不被锁定标志位，然后清空当前目标。
		TargetComponent.target.targetTagger.AddTag(removeTag);
		TargetComponent.target.Clear();
		// 启动不被锁定标志位定时清空协程
		remotePlayer.StartCoroutine(ClearIgnoreTag(remotePlayer, remotePlayer.GetTagger(), removeTag));
	}

	public IEnumerator ClearIgnoreTag(RemotePlayer player,ObjectTagger tagger,string removeTag) {
		if (!tagger.HasTag(removeTag)) yield break;
		yield return new WaitForSeconds(2f);
		tagger.RemoveTag(removeTag);
	}

	#endregion

	public void SetRPAsTarget(IDType targetId) {
		if (TargetComponent == null) return;
		if (Entity.IsOwnedByMe)
			EnemySyncModule.Instance.SendCustomFeature(targetId, Entity.networkId, FeatureIndex,writer => { }, needReply: true);
	}
}
