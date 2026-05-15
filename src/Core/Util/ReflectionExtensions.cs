using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace WKMPMod.Util;

public static class ReflectionExtensions {

	/// <summary>
	/// 获取字段值 (支持私有字段和基类字段) 
	/// </summary>
	public static T GetFieldValue<T>(this object obj, string fieldName) {
		if (obj == null) throw new ArgumentNullException(nameof(obj));

		Type type = obj.GetType();
		BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
							 BindingFlags.Instance | BindingFlags.Static;

		// 在类型及其基类中查找字段
		while (type != null) {
			FieldInfo field = type.GetField(fieldName, flags);
			if (field != null) {
				return (T)field.GetValue(obj);
			}
			type = type.BaseType;
		}

		throw new Exception($"Field '{fieldName}' not found in type '{obj.GetType()}'");
	}

	/// <summary>
	/// 设置字段值
	/// </summary>
	public static void SetFieldValue<T>(this object obj, string fieldName, T value) {
		Type type = obj.GetType();
		BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

		FieldInfo field = type.GetField(fieldName, flags);
		if (field != null) {
			field.SetValue(obj, value);
		}
	}
}