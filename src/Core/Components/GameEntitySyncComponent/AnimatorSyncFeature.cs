using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using WKMPMod.Data;
using WKMPMod.World;

namespace WKMPMod.Components;

public class AnimatorSyncFeature : ISyncFeature {
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

	public byte FeatureIndex { get; set; }
	public string FeatureId => $"Animator_{TargetAnimator?.name}";

	public Animator TargetAnimator { get; private set; }
	public NetworkedGameEntity Entity { get; }

	private readonly Dictionary<int, bool> _dirtyBools = new();
	private readonly Dictionary<int, float> _dirtyFloats = new();
	private readonly Dictionary<int, int> _dirtyInts = new();
	private readonly Dictionary<int, float> _dirtyLayerWeights = new();

	public bool IsDirty => _dirtyBools.Count > 0 || _dirtyFloats.Count > 0 || _dirtyInts.Count > 0 || _dirtyLayerWeights.Count > 0;

	public AnimatorSyncFeature(Animator animator, NetworkedGameEntity entity) {
		TargetAnimator = animator;
		Entity = entity;
	}

	#region[Patch状态设置API]

	public void SetBoolDirty(int hash, bool val) => _dirtyBools[hash] = val;
	public void SetFloatDirty(int hash, float val) => _dirtyFloats[hash] = val;
	public void SetIntDirty(int hash, int val) => _dirtyInts[hash] = val;
	public void SetLayerWeightDirty(int layer, float weight) => _dirtyLayerWeights[layer] = weight;

	#endregion

	#region[接口实现]

	public void WriteState(DataWriter writer) {
		// 1. Bool
		writer.Put((byte)_dirtyBools.Count);
		foreach (var kvp in _dirtyBools) {
			writer.Put(kvp.Key);
			writer.Put(kvp.Value);
		}
		_dirtyBools.Clear();

		// 2. Float
		writer.Put((byte)_dirtyFloats.Count);
		foreach (var kvp in _dirtyFloats) {
			writer.Put(kvp.Key);
			writer.Put(kvp.Value);
		}
		_dirtyFloats.Clear();

		// 3. Int
		writer.Put((byte)_dirtyInts.Count);
		foreach (var kvp in _dirtyInts) {
			writer.Put(kvp.Key);
			writer.Put(kvp.Value);
		}
		_dirtyInts.Clear();

		// 4. LayerWeight
		writer.Put((byte)_dirtyLayerWeights.Count);
		foreach (var kvp in _dirtyLayerWeights) {
			writer.Put(kvp.Key);
			writer.Put(kvp.Value);
		}
		_dirtyLayerWeights.Clear();
	}

	public void ReadState(DataReader reader) {
		if (TargetAnimator == null) return;

		// Bool
		byte boolCount = reader.GetByte();
		for (int i = 0; i < boolCount; i++) {
			TargetAnimator.SetBool(reader.GetInt(), reader.GetBool());
		}

		// Float
		byte floatCount = reader.GetByte();
		for (int i = 0; i < floatCount; i++) {
			TargetAnimator.SetFloat(reader.GetInt(), reader.GetFloat());
		}

		// Int
		byte intCount = reader.GetByte();
		for (int i = 0; i < intCount; i++) {
			TargetAnimator.SetInteger(reader.GetInt(), reader.GetInt());
		}

		// LayerWeight
		byte layerCount = reader.GetByte();
		for (int i = 0; i < layerCount; i++) {
			TargetAnimator.SetLayerWeight(reader.GetInt(), reader.GetFloat());
		}
	}

	public void OnUpdate(float deltaTime) { }

	public void OnReset() {
		_dirtyBools.Clear();
		_dirtyFloats.Clear();
		_dirtyInts.Clear();
		_dirtyLayerWeights.Clear();
	}

	#endregion

	public void ApplyTrigger(int hashId) {
		if (TargetAnimator == null) return;
		TargetAnimator.SetTrigger(hashId);
	}
}