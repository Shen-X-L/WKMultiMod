using DarkMachine.AI;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Unity.Entities;
using UnityEngine;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.NetWork;
using WKMPMod.World;

namespace WKMPMod.Components;

public class TargetSyncFeature : IFrameUpdateFeature, ICustomSyncFeature {
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

	#region[AITargetComponent初始化反射查找]

	//private static readonly Dictionary<Type, FieldInfo> _targetCompFieldCache = new();

	/// <summary>
	/// 沿 GameEntity 的继承链查找 AITargetComponent 字段
	/// </summary>
	//public static bool TryGetAITargetComponent(GameEntity entity, out AITargetComponent targetComponent) {
	//	targetComponent = null;
	//	if (entity == null) return false;
	//	Type type = entity.GetType();
	//	// 查表 或 反射获取 FieldInfo (沿继承链向上查找)
	//	if (!_targetCompFieldCache.TryGetValue(type, out FieldInfo fieldInfo)) {
	//		for (Type current = type; current != null && current != typeof(object); current = current.BaseType) {
	//			FieldInfo[] fields = current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
	//			for (int i = 0; i < fields.Length; i++) {
	//				if (typeof(AITargetComponent).IsAssignableFrom(fields[i].FieldType)) {
	//					fieldInfo = fields[i];
	//					break;
	//				}
	//			}
	//			if (fieldInfo != null) break;
	//		}
	//		_targetCompFieldCache[type] = fieldInfo;
	//	}
	//	// 如果存在匹配字段且成功取到实例, 直接返回 true
	//	if (fieldInfo != null && fieldInfo.GetValue(entity) is AITargetComponent result) {
	//		targetComponent = result;
	//		return true;
	//	}
	//	return false;
	//}

	private void EnableRemotePlayerTargeting() {
		// 视线组件修改：添加图层 15 (Creature) 到 AI 视线检测。
		if (Identity.AIEntity?.sight != null)
			Identity.AIEntity.sight.sightMask.value |= 1 << 15;

		// 无人机使用自己的 targetTags
		if (Identity.AIEntity is DEN_Drone drone) {
			AddTag(ref drone.targetTags, MPKeys.REMOTE_PLAYER_TAG);
			return;
		}

		// 气囊使用自己的 explodeTags
		if (Identity.AIEntity is DEN_Gasbag gasbag) {
			AddTag(ref gasbag.explodeTags, MPKeys.REMOTE_PLAYER_TAG);
			return;
		}

		if (TargetComponent == null) return;

		TargetComponent.targetScanMask.value |= 1 << 15;
		AddTag(ref TargetComponent.targetScanTags, MPKeys.REMOTE_PLAYER_TAG);
		AddTag(ref TargetComponent.targetIgnoreTags, $"Ignore:{Identity.networkId}");
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
	public NetworkedGameEntity Identity { get; }

	private IDType _lastTargetPlayerId;
	private Transform? _lastTarget;

	public TargetSyncFeature(AIGameEntity aiGameEntity, NetworkedGameEntity identity) {
		if(aiGameEntity is DEN_RatKing ratKing)
			TargetComponent = ratKing.targetComponent;
		else if(aiGameEntity is DEN_BasicNavigator basicNavigator)
			TargetComponent = basicNavigator.targetComponent;
		else if(aiGameEntity is DEN_Turret turret)
			TargetComponent = turret.targetComponent;
		else TargetComponent = aiGameEntity.targetComponent;
		Identity = identity;
		Register(TargetComponent, this);
		EnableRemotePlayerTargeting();
	}

	#region[ISyncFeature 基础实现]

	public string FeatureId => $"Target";
	public void OnReset() {
		if (_lastTargetPlayerId != default && Identity.IsOwnedByMe) {
			IdentitySync(_lastTargetPlayerId, TargetMessage.Forget);
		}
		_lastTargetPlayerId = default;
		_lastTarget = null;
	}
	public void OnDestroy() {
		OnReset();
		Unregister(TargetComponent); 
	}

	#endregion

	#region[IFrameUpdateFeature 帧更新实现]

	public void OnUpdate(float deltaTime) {
		UpdateTargetState();
	}

	#endregion

	#region[ICustomSyncFeature 信号式交互实现]

	public byte CustomIndex { get; set; }
	public void HandleCustomMessage(ulong senderId,DataReader reader,bool needReply) {
		var message = (TargetMessage)reader.GetByte();
		var player = ENT_Player.GetPlayer();
		switch (message) {
			case TargetMessage.Acquire: {
				TargetComponent?.SetTarget(player.transform);
				Identity.AIEntity?.target = player.transform;
				break;
			}
			case TargetMessage.Forget: {
				// 只清除属于当前实体的目标状态
				TargetComponent?.target.Clear();
				Identity.AIEntity?.target = null;
				break;
			}
		}
	}

	/// <summary>
	/// 收到拒绝, 对方没有该实体, 实体忽略对方
	/// </summary>
	public void HandleCustomReply(IDType senderId) {
		RemotePlayer remotePlayer = null;
		// 类型判断
		if (TargetComponent?.target?.targetEntity is RemotePlayer rp && rp.playerId == senderId)
			remotePlayer = rp;
		else if (TargetComponent?.target?.targetEntity is RemoteProp prop && prop.remoteEntity.playerId == senderId)
			remotePlayer = prop.remoteEntity;
		else return;
		// 设置不被锁定标志位, 然后清空当前目标
		string removeTag = $"Ignore:{Identity.networkId}";
		remotePlayer.container.AddTags(removeTag);
		TargetComponent.target.Clear();
		Identity.AIEntity?.target = null;
		// 启动不被锁定标志位定时清空协程
		remotePlayer.StartCoroutine(ClearIgnoreTag(remotePlayer, removeTag));
	}

	public IEnumerator ClearIgnoreTag(RemotePlayer player, string removeTag) {
		if (!player.GetTagger().HasTag(removeTag)) yield break;
		yield return new WaitForSeconds(5f);
		player.container.RemoveTags(removeTag);
		yield break;
	}

	#endregion

	#region[网络消息]

	private enum TargetMessage : byte {
		Acquire = 0,// 成为锁定目标
		Forget = 1// 作为旧目标遗忘
	}

	public static void OnCheckForTarget(AITargetComponent component) {
		if (component?.target?.targetTagger == null) return;
		// 未锁定远程玩家
		if (!component.target.targetTagger.HasTag(MPKeys.REMOTE_PLAYER_TAG)) return;
		// 玩家没有接管
		if (!TryGetFeature(component, out var feature))return;
		feature.UpdateTargetState();
	}

	private void IdentitySync(IDType targetId, TargetMessage message) {
		EnemySyncModule.Instance.SendCustomFeature(
			targetId,Identity.networkId,CustomIndex,
			writer => writer.Put((byte)message),
			needReply: message == TargetMessage.Acquire
		);
	}

	#endregion

	#region[工具函数]

	private void UpdateTargetState() {
		if (!Identity.IsOwnedByMe || TargetComponent == null) return;
		// 目标更新
		Transform currentTarget = TargetComponent.target?.targetTransform;
		if (currentTarget == _lastTarget) return;
		_lastTarget = currentTarget;
		// 是否有新目标
		bool hasNewTarget = TryGetRemoteTargetId(out var currentTargetId);
		// 无新目标 && 无旧目标 || 有新目标 && 新旧目标相同, 则无需发送消息
		if (hasNewTarget == (_lastTargetPlayerId != default) 
			&& (!hasNewTarget || currentTargetId == _lastTargetPlayerId)) return;
		// 旧玩家目标存在, 发送遗忘
		if (_lastTargetPlayerId != default) {
			// 发送遗忘
			IdentitySync(_lastTargetPlayerId, TargetMessage.Forget);
			_lastTargetPlayerId = default;
		}
		// 有新玩家目标
		if (hasNewTarget) {
			// 发送锁定
			_lastTargetPlayerId = currentTargetId;
			IdentitySync(currentTargetId, TargetMessage.Acquire);
		}
	}

	private bool TryGetRemoteTargetId(out IDType targetId) {
		targetId = default;
		var targetEntity = TargetComponent?.target?.targetEntity;

		if (targetEntity is RemotePlayer remotePlayer) {
			targetId = remotePlayer.playerId;
			return true;
		}

		if (targetEntity is RemoteProp remoteProp) {
			targetId = remoteProp.remoteEntity.playerId;
			return true;
		}

		return false;
	}

	#endregion
}
