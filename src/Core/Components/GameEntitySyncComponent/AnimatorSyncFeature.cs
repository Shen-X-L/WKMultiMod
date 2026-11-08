using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using WKMPMod.Data;
using WKMPMod.NetWork;
using WKMPMod.World;

namespace WKMPMod.Components;

public class AnimatorSyncFeature : IStateSyncFeature, ICustomSyncFeature, IInitialSyncFeature {

	public enum AnimatorParametersType : byte {
		Bool = 0,
		Float = 1,
		Integer = 2,
		LayerWeight = 3
	}

	public struct ParamChange {
		public int id;                  // 参数 Hash 或 Layer 索引
		public AnimatorParametersType type;
		public bool boolVal;
		public float floatVal;
		public int intVal;
	}

	#region[查找表]

	// 静态快速查找表：Animator -> 对应的 AnimatorSyncFeature
	private static readonly Dictionary<Animator, AnimatorSyncFeature> _animatorMap = new();
	public static void Register(Animator anim, AnimatorSyncFeature feature) {
		if (anim != null) _animatorMap[anim] = feature;
	}

	public static void Unregister(Animator anim) {
		if (anim != null) _animatorMap.Remove(anim);
	}

	public static void ClearAll() {
		_animatorMap.Clear();
	}

	public static bool TryGetFeature(Animator anim, out AnimatorSyncFeature feature) {
		return _animatorMap.TryGetValue(anim, out feature);
	}

	#endregion

	#region[运行时状态]

	// 引用
	public Animator TargetAnimator { get; private set; }
	public NetworkedGameEntity Entity { get; }
	// 缓存
	private readonly Dictionary<int, ParamChange> _dirtyChanges = new();

	#endregion

	public AnimatorSyncFeature(Animator animator, NetworkedGameEntity entity) {
		TargetAnimator = animator;
		Entity = entity;
		Register(TargetAnimator, this);
	}

	#region[Patch状态设置API]

	public void SetBoolDirty(int hash, bool val) {
		_dirtyChanges[hash] = new ParamChange { id = hash, type = AnimatorParametersType.Bool, boolVal = val };
	}
	public void SetFloatDirty(int hash, float val) {
		_dirtyChanges[hash] = new ParamChange { id = hash, type = AnimatorParametersType.Float, floatVal = val };
	}
	public void SetIntDirty(int hash, int val) {
		_dirtyChanges[hash] = new ParamChange { id = hash, type = AnimatorParametersType.Integer, intVal = val };
	}
	public void SetLayerWeightDirty(int layer, float weight) {
		// 防止 Layer 索引的 int 和 Parameter 的 Hash 发生数值碰撞冲突
		int key = layer ^ 0x7FFFF000;
		_dirtyChanges[key] = new ParamChange { id = layer, type = AnimatorParametersType.LayerWeight, floatVal = weight };
	}

	#endregion

	#region[ISyncFeature 基础实现]

	public string FeatureId => $"Animator";
	public void OnReset() {
		_dirtyChanges.Clear();
	}
	public void OnDestroy() {
		Unregister(TargetAnimator);
		OnReset();
	}

	#endregion

	#region[IStateSyncFeature 状态同步实现]

	public byte StateIndex { get; set; }
	public bool IsDirty => _dirtyChanges.Count > 0;

	public void WriteState(DataWriter writer) {
		// 写入总变更数量
		writer.Put((ushort)_dirtyChanges.Count);
		if (_dirtyChanges.Count == 0) return;
		// 写入每一个变动项 [ID + Type + Value]
		foreach (var change in _dirtyChanges.Values) {
			writer.Put(change.id);
			writer.Put((byte)change.type);
			switch (change.type) {
				case AnimatorParametersType.Bool:
					writer.Put(change.boolVal);
					break;
				case AnimatorParametersType.Float:
				case AnimatorParametersType.LayerWeight:
					writer.Put(change.floatVal);
					break;
				case AnimatorParametersType.Integer:
					writer.Put(change.intVal);
					break;
			}
		}
		// 清除当前帧数据
		_dirtyChanges.Clear();
	}

	public void ReadState(DataReader reader) {
		if (TargetAnimator == null) return;
		ushort count = reader.GetUShort();
		for (int i = 0; i < count; i++) {
			int id = reader.GetInt();
			var type = (AnimatorParametersType)reader.GetByte();
			switch (type) {
				case AnimatorParametersType.Bool:
					TargetAnimator.SetBool(id, reader.GetBool());
					break;
				case AnimatorParametersType.Float:
					TargetAnimator.SetFloat(id, reader.GetFloat());
					break;
				case AnimatorParametersType.Integer:
					TargetAnimator.SetInteger(id, reader.GetInt());
					break;
				case AnimatorParametersType.LayerWeight:
					TargetAnimator.SetLayerWeight(id, reader.GetFloat());
					break;
			}
		}
	}

	#endregion

	#region[ICustomSyncFeature 信号式交互实现]

	public byte CustomIndex { get; set; }
	public void HandleCustomMessage(ulong senderId, DataReader reader, bool needReply) {
		if (Entity.IsOwnedByMe) return;
		var command = (AnimatorCommand)reader.GetByte();

		switch (command) {
			case AnimatorCommand.Trigger: {
				int hashId = reader.GetInt();
				ApplyTrigger(hashId);
				break;
			}

			case AnimatorCommand.Play: {
				int stateHash = reader.GetInt();
				int layer = reader.GetInt();
				float normalizedTime = reader.GetFloat();
				ApplyPlay(stateHash, layer, normalizedTime);
				break;
			}
		}
	}

	public void HandleCustomReply(IDType senderId) {

	}

	#endregion

	#region[IInitialSyncFeature 初始化状态同步]

	public byte InitialIndex { get; set; }

	/// <summary>
	/// 写入 Animator 完整状态快照
	/// 仅用于新订阅者初始化, 不影响 Dirty 缓存
	/// </summary>
	public void WriteInitialState(DataWriter writer) {
		Animator animator = TargetAnimator;
		// Animator 播放速度
		writer.Put(animator.speed);
		// Layer 状态
		int layerCount = animator.layerCount;
		writer.Put((ushort)layerCount);
		for (int i = 0; i < layerCount; i++) {
			// Transition 中使用目标状态，而不是当前转出状态
			AnimatorStateInfo state = animator.IsInTransition(i)
				? animator.GetNextAnimatorStateInfo(i)
				: animator.GetCurrentAnimatorStateInfo(i);
			writer.Put(state.fullPathHash);
			writer.Put(state.normalizedTime);
			writer.Put(animator.GetLayerWeight(i));
		}
		// Animator Parameters
		// Animator.parameters 返回 Controller 中定义的参数
		AnimatorControllerParameter[] parameters = animator.parameters;
		// 先统计实际需要同步的参数数量
		ushort parameterCount = 0;
		foreach (var parameter in parameters) {
			if (IsInitialSyncParameter(animator, parameter)) parameterCount++;
		}
		writer.Put(parameterCount);
		// 写入参数 Hash、类型和值
		foreach (var parameter in parameters) {
			if (!IsInitialSyncParameter(animator, parameter)) continue;
			writer.Put(parameter.nameHash);
			switch (parameter.type) {
				case AnimatorControllerParameterType.Bool:
					writer.Put((byte)AnimatorParametersType.Bool);
					writer.Put(animator.GetBool(parameter.nameHash));
					break;
				case AnimatorControllerParameterType.Float:
					writer.Put((byte)AnimatorParametersType.Float);
					writer.Put(animator.GetFloat(parameter.nameHash));
					break;
				case AnimatorControllerParameterType.Int:
					writer.Put((byte)AnimatorParametersType.Integer);
					writer.Put(animator.GetInteger(parameter.nameHash));
					break;
			}
		}
	}

	/// <summary>
	/// 判断参数是否需要进入初始快照
	/// Trigger 无法读取当前值, 因此不参与快照
	/// 被动画曲线控制的参数也不参与同步
	/// </summary>
	private static bool IsInitialSyncParameter(Animator animator, AnimatorControllerParameter parameter) {
		if (animator.IsParameterControlledByCurve(parameter.nameHash)) return false;

		return parameter.type == AnimatorControllerParameterType.Bool
			|| parameter.type == AnimatorControllerParameterType.Float
			|| parameter.type == AnimatorControllerParameterType.Int;
	}

	/// <summary>
	/// 读取并应用 Animator 完整状态快照
	/// 即使 Animator 不存在或已禁用, 也必须完整消费 Reader
	/// </summary>
	public void ReadInitialState(DataReader reader) {
		float speed = reader.GetFloat();
		// Layer 状态
		ushort layerCount = reader.GetUShort();
		Animator animator = TargetAnimator;
		for (int i = 0; i < layerCount; i++) {
			int stateHash = reader.GetInt();
			float normalizedTime = reader.GetFloat();
			float weight = reader.GetFloat();
			// 即使本地 Layer 数量不同，也必须继续读取
			if (i >= animator.layerCount) continue;
			animator.SetLayerWeight(i, weight);
			if (stateHash != 0)
				animator.Play(stateHash, i, normalizedTime);
		}
		// Parameters
		ushort parameterCount = reader.GetUShort();
		for (int i = 0; i < parameterCount; i++) {
			int hash = reader.GetInt();
			var type = (AnimatorParametersType)reader.GetByte();
			switch (type) {
				case AnimatorParametersType.Bool: {
					bool value = reader.GetBool();
					animator.SetBool(hash, value);
					break;
				}
				case AnimatorParametersType.Float: {
					float value = reader.GetFloat();
					animator.SetFloat(hash, value);
					break;
				}
				case AnimatorParametersType.Integer: {
					int value = reader.GetInt();
					animator.SetInteger(hash, value);
					break;
				}
			}
		}
		// Animator Speed
		animator.speed = speed;
	}

	#endregion

	#region[网络消息]

	private enum AnimatorCommand : byte {
		Trigger, Play
	}

	/// <summary>
	/// 发送RPC数据
	/// </summary>
	public void SetTrigger(int hashId) {
		if (TargetAnimator == null) return;
		TargetAnimator.SetTrigger(hashId);
		if (Entity.IsOwnedByMe) {
			EnemySyncModule.Instance.BroadcastCustomFeature(
				Entity.networkId, CustomIndex,
				writer => {
					writer.Put((byte)AnimatorCommand.Trigger);
					writer.Put(hashId);
				});
		}
	}

	/// <summary>
	/// 发送RPC数据
	/// </summary>
	public void Play(int stateHash, int layer, float normalizedTime) {
		if (TargetAnimator == null) return;
		TargetAnimator.Play(stateHash, layer, normalizedTime);
		if (Entity.IsOwnedByMe) {
			EnemySyncModule.Instance.BroadcastCustomFeature(
				Entity.networkId, CustomIndex,
				writer => {
					writer.Put((byte)AnimatorCommand.Play);
					writer.Put(stateHash);
					writer.Put(layer);
					writer.Put(normalizedTime);
				});
		}
	}

	/// <summary>
	/// 接受RPC数据并调用
	/// </summary>
	public void ApplyTrigger(int hashId) {
		if (TargetAnimator == null) return;
		TargetAnimator.SetTrigger(hashId);
	}

	/// <summary>
	/// 接受RPC数据并调用
	/// </summary>
	public void ApplyPlay(int stateHash, int layer, float normalizedTime) {
		if (TargetAnimator == null) return;
		TargetAnimator.Play(stateHash, layer, normalizedTime);
	}

	#endregion
}