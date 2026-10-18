using HarmonyLib;
using System;
using System.Collections.Generic;
using WKMPMod.Data;
using UnityEngine;

namespace WKMPMod.Components;

public class FieldSyncFeature<T, F> : ISyncFeature {
	#region [接口字段]

	public byte FeatureIndex { get; set; }
	public string FeatureId { get; }
	public bool IsDirty => !AreEquals(_fieldAccessor(_target), _lastValue);

	/// <summary>
	/// 相等性比较
	/// </summary>
	private static bool AreEquals(F a, F b) {
		if (IsUnityObjectType) {
			// 通过 object 中转安全转换为 UnityEngine.Object, 并调用 Unity 的 op_Equality
			UnityEngine.Object objA = a as object as UnityEngine.Object;
			UnityEngine.Object objB = b as object as UnityEngine.Object;
			return objA == objB;
		} else {
			return EqualityComparer<F>.Default.Equals(a, b);
		}
	}

	#endregion

	#region[反射/序列化]

	// 静态缓存字段访问器, 避免每次创建新的委托
	private static readonly Dictionary<string, AccessTools.FieldRef<T, F>> _accessorCache = new();
	// 字段访问器委托,用于获取和设置字段值					 
	private AccessTools.FieldRef<T, F> _fieldAccessor;
	// 在类型加载时确定 F 是否属于 Unity 对象体系
	private static readonly bool IsUnityObjectType = typeof(UnityEngine.Object).IsAssignableFrom(typeof(F));
	private static readonly Action<DataWriter, F> _writerAction = CreateWriterAction();
	private static readonly Func<DataReader, F> _readerAction = CreateReaderAction();

	private static Action<DataWriter, F> CreateWriterAction() {
		Type type = typeof(F);

		// 基础类型与常用 Unity 类型分发
		if (type == typeof(int)) return (writer, val) => writer.Put((int)(object)val!);
		if (type == typeof(float)) return (writer, val) => writer.Put((float)(object)val!);
		if (type == typeof(bool)) return (writer, val) => writer.Put((bool)(object)val!);
		if (type == typeof(string)) return (writer, val) => writer.Put((string)(object)val!);
		if (type == typeof(Vector3)) return (writer, val) => writer.Put((Vector3)(object)val!);
		if (type == typeof(Quaternion)) return (writer, val) => writer.Put((Quaternion)(object)val!);
		// 实现了 INetworkSerializable 的自定义类型
		if (typeof(INetworkSerializable).IsAssignableFrom(type)) {
			return (writer, val) => {
				if (val is INetworkSerializable netObj) netObj.Serialize(writer);
			};
		}
		throw new NotSupportedException($"FieldSyncFeature<T, F> does not support serializing type: {type.Name}");
	}

	private static Func<DataReader, F> CreateReaderAction() {
		Type type = typeof(F);
		if (type == typeof(int)) return reader => (F)(object)reader.GetInt();
		if (type == typeof(float)) return reader => (F)(object)reader.GetFloat();
		if (type == typeof(bool)) return reader => (F)(object)reader.GetBool();
		if (type == typeof(string)) return reader => (F)(object)reader.GetString();
		if (type == typeof(Vector3)) return reader => (F)(object)reader.GetVector3();
		if (type == typeof(Quaternion)) return reader => (F)(object)reader.GetQuaternion();
		// 实现了 INetworkSerializable 的自定义类型
		if (typeof(INetworkSerializable).IsAssignableFrom(type)) {
			return reader => {
				F instance = Activator.CreateInstance<F>();
				if (instance is INetworkSerializable netObj) netObj.Deserialize(reader);
				return instance;
			};
		}
		throw new NotSupportedException($"FieldSyncFeature<T, F> does not support deserializing type: {type.Name}");
	}

	#endregion

	private T _target;// 目标对象
	private F _lastValue;// 上一帧的字段值

	public FieldSyncFeature(T targetObject, string fieldName) {
		_target = targetObject;
		FeatureId = $"FieldSync_{fieldName}";
		if (!_accessorCache.TryGetValue(fieldName, out _fieldAccessor)) {
			_fieldAccessor = AccessTools.FieldRefAccess<T, F>(fieldName);
			_accessorCache[fieldName] = _fieldAccessor;
		}
		_lastValue = _fieldAccessor(targetObject);
	}

	public void WriteState(DataWriter writer) {
		_lastValue = _fieldAccessor(_target);
		_writerAction(writer, _lastValue);
	}

	public void ReadState(DataReader reader) { 
		_lastValue = _readerAction(reader);
		_fieldAccessor(_target) = _lastValue;
	}

	public void OnUpdate(float deltaTime) { }

	public void OnReset() {
		if (_target != null) _lastValue = _fieldAccessor(_target);
	}

	public void OnDestroy() { }
}