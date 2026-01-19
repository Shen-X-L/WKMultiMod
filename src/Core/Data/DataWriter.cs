using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace WKMPMod.Data;

public class DataWriter : IDisposable {
	private byte[] _buffer;
	private int _position;
	private readonly ArrayPool<byte> _pool;

	public ArraySegment<byte> Data => new ArraySegment<byte>(_buffer, 0, _position);


	public DataWriter(int initialCapacity = 1024) {
		_pool = ArrayPool<byte>.Shared;
		_buffer = _pool.Rent(initialCapacity);
		_position = 0;
	}

	// 获取已写入数据的副本
	public byte[] ToArray() {
		// 仅拷贝已写入的部分
		byte[] result = new byte[_position];
		Array.Copy(_buffer, 0, result, 0, _position);
		return result;
	}

	// 将数据复制到目标数组
	public void CopyDataTo(byte[] target, int targetOffset = 0) {
		if (target.Length - targetOffset < _position)
			throw new ArgumentException("Target buffer is too small");

		Array.Copy(_buffer, 0, target, targetOffset, _position);
	}

	// 重置写入位置,准备重新写入
	public void Reset() {
		_position = 0;
		// 注意:通常不需要清除 _buffer 里的旧数据,
		// 因为新的写入会通过 _position 覆盖旧数据。
	}

	#region[写入基本类型函数]
	// 写入 bool (1 字节)
	public DataWriter Put(bool value) {
		EnsureCapacity(1);
		_buffer[_position] = (byte)(value ? 1 : 0);
		_position += 1;
		return this;
	}

	// 写入 byte (8 位无符号整数)
	public DataWriter Put(byte value) {
		EnsureCapacity(1);
		_buffer[_position] = value;
		_position += 1;
		return this;
	}

	// 写入 int (32 位有符号整数)
	public DataWriter Put(int value) {
		EnsureCapacity(4);
		BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(_position), value);
		_position += 4;
		return this;
	}

	// 写入 uint (32 位无符号整数)
	public DataWriter Put(uint value) {
		EnsureCapacity(4);
		BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(_position), value);
		_position += 4;
		return this;
	}

	// 写入 long (64 位有符号整数)
	public DataWriter Put(long value) {
		EnsureCapacity(8);
		BinaryPrimitives.WriteInt64LittleEndian(_buffer.AsSpan(_position), value);
		_position += 8;
		return this;
	}

	// 写入 ulong (64 位无符号整数)
	public DataWriter Put(ulong value) {
		EnsureCapacity(8);
		BinaryPrimitives.WriteUInt64LittleEndian(_buffer.AsSpan(_position), value);
		_position += 8;
		return this;
	}

	// 写入 float (32 位浮点数)
	public DataWriter Put(float value) {
		EnsureCapacity(4);
		// 关键:将 float 的 32 位数据完全不动地解释为 int
		// 在旧版 .NET 中使用 SingleToInt32Bits
		int intVal = BitConverter.SingleToInt32Bits(value);
		BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(_position), intVal);
		_position += 4;
		return this;
	}

	// 写入 double (64 位浮点数)
	public DataWriter Put(double value) {
		EnsureCapacity(8);
		// 将 double 解释为 long (64位)
		long longVal = BitConverter.DoubleToInt64Bits(value);
		BinaryPrimitives.WriteInt64LittleEndian(_buffer.AsSpan(_position), longVal);
		_position += 8;
		return this;
	}
	#endregion

	#region[写入复合类型函数]
	// 写入全量数组
	public DataWriter Put(byte[] value) {
		if (value == null) return Put(0);
		return Put(value, 0, value.Length);
	}

	// 写入数组的一部分(最常用,避免外部 Slice 产生开销)
	public DataWriter Put(byte[] value, int offset, int length) {
		if (value == null) {
			EnsureCapacity(4);
			BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(_position), 0);
			_position += 4;
			return this;
		}

		// 写入长度前缀(可选,根据你的协议决定是否需要先写长度)
		Put(length);

		EnsureCapacity(length);
		Array.Copy(value, offset, _buffer, _position, length);
		_position += length;
		return this;
	}

	// 写入另一个只读视图 (ReadOnlySpan)
	public DataWriter Put(ReadOnlySpan<byte> value) {
		Put(value.Length);
		EnsureCapacity(value.Length);
		value.CopyTo(_buffer.AsSpan(_position));
		_position += value.Length;
		return this;
	}

	// 写入字符串(UTF-8 编码,前缀长度)
	public DataWriter Put(string value) {
		if (string.IsNullOrEmpty(value)) {
			Put(0);
			return this;
		}

		// 预估最大长度
		int maxByteCount = Encoding.UTF8.GetMaxByteCount(value.Length);
		EnsureCapacity(maxByteCount + 4); // +4 是为了预留长度信息的位置

		// 先空出 4 字节存长度
		int lengthPos = _position;
		_position += 4;

		// 直接编码
		int actualBytes = Encoding.UTF8.GetBytes(value, 0, value.Length, _buffer, _position);

		// 回头填入真实的字节长度
		BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(lengthPos), actualBytes);

		_position += actualBytes;
		return this;
	}
	#endregion

	// 确保缓冲区有足够的空间
	private void EnsureCapacity(int additional) {
		// 如果当前缓冲区不够大,生成一个更大的
		if (_position + additional > _buffer.Length) {
			byte[] newBuffer = _pool.Rent((_position + additional) * 2);
			Array.Copy(_buffer, 0, newBuffer, 0, _position);
			_pool.Return(_buffer);
			_buffer = newBuffer;
		}
	}

	public void Dispose() {
		if (_buffer != null) {
			_pool.Return(_buffer);
			_buffer = null; // 设置为 null,防止重复 Return
			_position = 0;
		}
	}

}

