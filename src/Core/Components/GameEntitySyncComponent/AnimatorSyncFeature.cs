using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using WKMPMod.Data;
using WKMPMod.NetWork;
using WKMPMod.World;

namespace WKMPMod.Components;

public class AnimatorSyncFeature : IStateSyncFeature, ICustomSyncFeature {
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

	public Animator TargetAnimator { get; private set; }
	public NetworkedGameEntity Entity { get; }
	private readonly Dictionary<int, bool> _dirtyBools = new();
	private readonly Dictionary<int, float> _dirtyFloats = new();
	private readonly Dictionary<int, int> _dirtyInts = new();
	private readonly Dictionary<int, float> _dirtyLayerWeights = new();

	public AnimatorSyncFeature(Animator animator, NetworkedGameEntity entity) {
		TargetAnimator = animator;
		Entity = entity;
		Register(TargetAnimator, this);
	}

	#region[Patch状态设置API]

	public void SetBoolDirty(int hash, bool val) => _dirtyBools[hash] = val;
	public void SetFloatDirty(int hash, float val) => _dirtyFloats[hash] = val;
	public void SetIntDirty(int hash, int val) => _dirtyInts[hash] = val;
	public void SetLayerWeightDirty(int layer, float weight) => _dirtyLayerWeights[layer] = weight;

	#endregion

	#region[ISyncFeature 基础实现]

	public string FeatureId => $"Animator";
	public void OnReset() {
		_dirtyBools.Clear();
		_dirtyFloats.Clear();
		_dirtyInts.Clear();
		_dirtyLayerWeights.Clear();
	}
	public void OnDestroy() {
		Unregister(TargetAnimator);
		OnReset();
	}

	#endregion

	#region[IStateSyncFeature 状态同步实现]

	public byte StateIndex { get; set; }
	public bool IsDirty => _dirtyBools.Count > 0 || _dirtyFloats.Count > 0 || _dirtyInts.Count > 0 || _dirtyLayerWeights.Count > 0;

	public void WriteState(DataWriter writer) {
		// 1. Bool
		writer.Put((byte)_dirtyBools.Count);
		foreach (var kvp in _dirtyBools) { writer.Put(kvp.Key); writer.Put(kvp.Value); }
		_dirtyBools.Clear();
		// 2. Float
		writer.Put((byte)_dirtyFloats.Count);
		foreach (var kvp in _dirtyFloats) { writer.Put(kvp.Key); writer.Put(kvp.Value); }
		_dirtyFloats.Clear();
		// 3. Int
		writer.Put((byte)_dirtyInts.Count);
		foreach (var kvp in _dirtyInts) { writer.Put(kvp.Key); writer.Put(kvp.Value); }
		_dirtyInts.Clear();
		// 4. LayerWeight
		writer.Put((byte)_dirtyLayerWeights.Count);
		foreach (var kvp in _dirtyLayerWeights) { writer.Put(kvp.Key); writer.Put(kvp.Value); }
		_dirtyLayerWeights.Clear();
	}

	public void ReadState(DataReader reader) {
		if (TargetAnimator == null) return;
		byte boolCount = reader.GetByte();
		for (int i = 0; i < boolCount; i++) TargetAnimator.SetBool(reader.GetInt(), reader.GetBool());
		byte floatCount = reader.GetByte();
		for (int i = 0; i < floatCount; i++) TargetAnimator.SetFloat(reader.GetInt(), reader.GetFloat());
		byte intCount = reader.GetByte();
		for (int i = 0; i < intCount; i++) TargetAnimator.SetInteger(reader.GetInt(), reader.GetInt());
		byte layerCount = reader.GetByte();
		for (int i = 0; i < layerCount; i++) TargetAnimator.SetLayerWeight(reader.GetInt(), reader.GetFloat());
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

	#region[网络消息]

	private enum AnimatorCommand : byte {
		Trigger, Play
	}

	public void SetTrigger(int hashId) {
		if (TargetAnimator == null)return;
		TargetAnimator.SetTrigger(hashId);
		if (Entity.IsOwnedByMe) {
			EnemySyncModule.Instance.BroadcastCustomFeature(
				Entity.networkId,CustomIndex,
				writer => {
					writer.Put((byte)AnimatorCommand.Trigger);
					writer.Put(hashId);});
		}
	}

	public void Play(int stateHash, int layer, float normalizedTime) {
		if (TargetAnimator == null)return;
		TargetAnimator.Play(stateHash, layer, normalizedTime);
		if (Entity.IsOwnedByMe) {
			EnemySyncModule.Instance.BroadcastCustomFeature(
				Entity.networkId,CustomIndex,
				writer => {
					writer.Put((byte)AnimatorCommand.Play);
					writer.Put(stateHash);
					writer.Put(layer);
					writer.Put(normalizedTime);
				});
		}
	}

	public void ApplyTrigger(int hashId) {
		if (TargetAnimator == null) return;
		TargetAnimator.SetTrigger(hashId);
	}

	public void ApplyPlay(int stateHash, int layer, float normalizedTime) {
		if (TargetAnimator == null)return;
		TargetAnimator.Play(stateHash, layer, normalizedTime);
	}
	#endregion
}