using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Text;

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
}