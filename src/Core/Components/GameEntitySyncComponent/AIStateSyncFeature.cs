using DarkMachine.AI;
using HarmonyLib;
using System;
using Unity.Entities;
using WKMPMod.Data;
using WKMPMod.World;


namespace WKMPMod.Components;

public class AIStateSyncFeature : ICustomSyncFeature, IInitialSyncFeature {
	#region[静态反射缓存]

	private static readonly AccessTools.FieldRef<AIGameEntity, AIStateComponent> _currentStateField =
		AccessTools.FieldRefAccess<AIGameEntity, AIStateComponent>("currentState");
	private static readonly AccessTools.FieldRef<AIGameEntity, Action<string>> _onChangeStateField =
		AccessTools.FieldRefAccess<AIGameEntity, Action<string>>("_OnChangeState");

	#endregion

	#region[运行时状态]

	// 引用字段
	private AIGameEntity _aiGameEntity;
	private NetworkedGameEntity _netEntity;
	// 缓存
	private string _currentStateName;

	#endregion

	public AIStateSyncFeature(AIGameEntity entity,NetworkedGameEntity identity) {
		_aiGameEntity = entity;
		_netEntity = identity;
		_currentStateName = GetCurrentStateName();
		_onChangeStateField(entity) += OnStateChanged;
	}

	public void OnStateChanged(string newState) {
		_currentStateName = newState ?? string.Empty;
		// 只有实体所有者负责广播 AI 状态变化
		if (_netEntity == null || !_netEntity.IsOwnedByMe)return;
		EnemySyncModule.Instance.BroadcastCustomFeature(
			_netEntity.networkId, CustomIndex,
			writer => writer.Put(_currentStateName),
			needReply: false
		);

	}

	private string GetCurrentStateName() {
		AIStateComponent state = _currentStateField(_aiGameEntity);
		return state != null ? state.name : string.Empty;
	}

	#region[ISyncFeature 基础实现]

	public string FeatureId => "AIState";

	public void OnDestroy() {
		if (_aiGameEntity != null) 
			_onChangeStateField(_aiGameEntity) -= OnStateChanged;

		_aiGameEntity = null;
		_netEntity = null;
	}

	public void OnReset() {
		_currentStateName = GetCurrentStateName();
	}

	#endregion

	#region[ICustomSyncFeature 信号式交互实现]

	public byte CustomIndex { get; set; }

	public void HandleCustomMessage(ulong senderId,DataReader reader,bool needReply) {
		// 发送者非所有者 || 玩家是所有者
		if (senderId != _netEntity.CurrentOwnerId|| _netEntity.IsOwnedByMe) return;
		string stateName = reader.GetString();
		if (string.IsNullOrEmpty(stateName)) return;
		// 避免重复切换状态
		if (_currentStateName == stateName) return;
		_currentStateName = stateName;
		_aiGameEntity.ChangeState(stateName);
	}

	public void HandleCustomReply(ulong senderId) {
		// 当前模块不需要处理自定义消息失败响应
	}

	#endregion

	#region[IInitialSyncFeature 初始化状态实现]

	public byte InitialIndex { get; set; }

	public void WriteInitialState(DataWriter writer) {
		writer.Put(GetCurrentStateName());
	}

	public void ReadInitialState(DataReader reader) {
		string stateName = reader.GetString();
		if (string.IsNullOrEmpty(stateName)) return;
		_currentStateName = stateName;
		_aiGameEntity.ChangeState(stateName);
	}

	#endregion
}
