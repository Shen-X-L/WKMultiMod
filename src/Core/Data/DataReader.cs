using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using Unity.VisualScripting;
using UnityEngine;
using WKMultiPlayerMod.Data;
using static WKMPMod.Core.MPGameModeManager;

namespace WKMPMod.Data;

public class DataReader {
	private ReadOnlyMemory<byte> _data;
	private int _position;

	public int AvailableBytes => _data.Length - _position;

	// 支持视图 (ArraySegment)
	public void SetSource(ArraySegment<byte> source) {
		_data = source;
		_position = 0;
	}

	// 支持原始数组 (byte[]) - 内部会自动转为 Memory
	public void SetSource(byte[] source) {
		_data = source;
		_position = 0;
	}

	// 支持部分数组
	public void SetSource(byte[] source, int offset, int length) {
		_data = new ReadOnlyMemory<byte>(source, offset, length);
		_position = 0;
	}

	#region[读取基本类型]

	// 读取 bool (1 字节)
	public bool GetBool() {
		bool val = _data.Span[_position] != 0;
		_position += 1;
		return val;
	}

	// 读取 byte (8 位无符号整数)
	public byte GetByte() {
		byte val = _data.Span[_position];
		_position += 1;
		return val;
	}

	// 读取 short (16 位有符号整数)
	public short GetShort() {
		short val = BinaryPrimitives.ReadInt16LittleEndian(_data.Span.Slice(_position));
		_position += 2;
		return val;
	}

	// 读取 ushort (16 位无符号整数)
	public ushort GetUShort() {
		ushort val = BinaryPrimitives.ReadUInt16LittleEndian(_data.Span.Slice(_position));
		_position += 2;
		return val;
	}

	// 读取 int (32 位有符号整数)
	public int GetInt() {
		int val = BinaryPrimitives.ReadInt32LittleEndian(_data.Span.Slice(_position));
		_position += 4;
		return val;
	}

	// 读取 uint (32 位无符号整数)
	public uint GetUInt() {
		uint val = BinaryPrimitives.ReadUInt32LittleEndian(_data.Span.Slice(_position));
		_position += 4;
		return val;
	}

	// 读取 long (64 位有符号整数)
	public long GetLong() {
		long val = BinaryPrimitives.ReadInt64LittleEndian(_data.Span.Slice(_position));
		_position += 8;
		return val;
	}

	// 读取 ulong (64 位无符号整数)
	public ulong GetULong() {
		ulong val = BinaryPrimitives.ReadUInt64LittleEndian(_data.Span.Slice(_position));
		_position += 8;
		return val;
	}

	// 读取 float (32 位单精度浮点数)
	public float GetFloat() {
		int intVal = BinaryPrimitives.ReadInt32LittleEndian(_data.Span.Slice(_position));
		_position += 4;
		// 将 int 的位还原为 float
		return BitConverter.Int32BitsToSingle(intVal);
	}

	// 读取 double (64 位双精度浮点数)
	public double GetDouble() {
		long longVal = BinaryPrimitives.ReadInt64LittleEndian(_data.Span.Slice(_position));
		_position += 8;
		return BitConverter.Int64BitsToDouble(longVal);
	}

	#endregion

	#region[读取可空值类型]

	public byte? GetNullableByte() {
		bool hasValue = GetBool();  // 读取是否有值
		if (!hasValue)
			return null;
		return GetByte();            // 读取实际值
	}

	public int? GetNullableInt() {
		bool hasValue = GetBool();  // 读取是否有值
		if (!hasValue)
			return null;
		return GetInt();            // 读取实际值
	}

	public float? GetNullableFloat() {
		bool hasValue = GetBool();  // 读取是否有值
		if (!hasValue)
			return null;
		return GetFloat();            // 读取实际值
	}

	#endregion

	#region[读取Unity类型]

	/// <summary>
	/// 读取 Vector3
	/// </summary>
	public Vector3 GetVector3() {
		// 获取当前位置的 Span 视图
		var span = _data.Span.Slice(_position);

		float x = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(span));
		float y = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(span.Slice(4)));
		float z = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(span.Slice(8)));

		_position += 12;
		return new Vector3(x, y, z);
	}

	/// <summary>
	/// 读取 Quaternion
	/// </summary>
	public Quaternion GetQuaternion() {
		var span = _data.Span.Slice(_position);

		float x = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(span));
		float y = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(span.Slice(4)));
		float z = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(span.Slice(8)));
		float w = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(span.Slice(12)));

		_position += 16;
		return new Quaternion(x, y, z, w);
	}

	#endregion

	#region[读取复合类型]

	// 获取字符串 (先读取长度 再读取内容)
	public string GetString() {
		int length = GetInt();
		if (length <= 0) return string.Empty;
		if (length > AvailableBytes) throw new Exception("String length out of range");
		string val = Encoding.UTF8.GetString(_data.Span.Slice(_position, length));
		_position += length;
		return val;
	}

	// 获取 Span<byte> 在栈上的切片 无法保留
	public ReadOnlySpan<byte> GetBytes() {
		int length = GetInt(); // 获取之前存入的长度
		var result = _data.Span.Slice(_position, length);
		_position += length;
		return result;
	}

	// 获取 Memory<byte> 在堆上的切片 可保留
	public ReadOnlyMemory<byte> GetMemory() {
		int length = GetInt();
		var result = _data.Slice(_position, length); // Memory 的 Slice 返回的还是 Memory
		_position += length;
		return result;
	}

	#endregion

	#region[读取自定义类型]

	/// <summary>
	/// 获取 Dictionary&lt;string, byte&gt; 用于(物品名称,数量)
	/// </summary>
	public Dictionary<string, byte> GetStringByteDict() { 
		int length = GetInt();
		var val = new Dictionary<string, byte>();
		for (int i = 0; i < length; i++) {
			var stringKey = GetString();
			var ushortValue = GetByte();
			val[stringKey] = ushortValue;
		}
		return val;
	}

	/// <summary>
	/// 获取<see cref="PlayerData"/> 玩家数据
	/// </summary>
	public PlayerData GetPlayerData() {
		var data = new PlayerData();
		// 基础信息(id,时间戳)
		data.playId = GetULong();
		data.TimestampTicks = GetLong();

		// 位置信息
		data.Position = GetVector3();

		// 角度信息
		data.Rotation = GetQuaternion();

		// 左手数据
		data.LeftHand.Position = GetVector3();

		// 右手数据
		data.RightHand.Position = GetVector3();

		// 状态标志
		data.IsTeleport = GetBool();

		return data;
	}

	/// <summary>
	/// 获取 List&lt;string&gt; 用于tags等List<string>
	/// </summary>
	public List<string> GetStringList() { 
		var data = new List<string>();
		var i = GetInt();
		for (int j = 0; j < i; j++) {
			data.Add(GetString());
		}
		return data;
	}
	#endregion

	#region[读取泛型类型]

	/// <summary>
	/// 读取单个实现了接口的对象
	/// </summary>
	public T Get<T>() where T : INetworkSerializable, new() {
		T obj = new T();
		obj.Deserialize(this);
		return obj;
	}

	/// <summary>
	/// 读取实现了接口的对象列表
	/// </summary>
	public List<T> GetList<T>() where T : INetworkSerializable, new() {
		int count = GetInt();
		if (count <= 0) return new List<T>(0);

		List<T> list = new List<T>(count);
		for (int i = 0; i < count; i++) {
			T item = new T();
			item.Deserialize(this);
			list.Add(item);
		}
		return list;
	}

	/// <summary>
	/// 填充现有列表, 避免重新分配 List 内存
	/// </summary>
	public void GetList<T>(List<T> existingList) where T : INetworkSerializable, new() {
		int count = GetInt();
		existingList.Clear();
		for (int i = 0; i < count; i++) {
			T item = new T();
			item.Deserialize(this);
			existingList.Add(item);
		}
	}

	#endregion
}