using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace WKMultiMod.src.NetWork;


public class DataWriter : IDisposable {
	private byte[] _buffer;
	private int _position;
	private readonly ArrayPool<byte> _pool;

	public DataWriter(int initialCapacity = 1024) {
		_pool = ArrayPool<byte>.Shared;
		_buffer = _pool.Rent(initialCapacity);
		_position = 0;
	}

	// 写入整数示例：使用 BinaryPrimitives 保证字节序
	public void Put(int value) {
		EnsureCapacity(4);
		BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(_position), value);
		_position += 4;
	}

	public void Put(ulong value) {
		EnsureCapacity(8);
		BinaryPrimitives.WriteUInt64LittleEndian(_buffer.AsSpan(_position), value);
		_position += 8;
	}

	public void Put(string value) {
		if (value == null) {
			Put(0);
			return;
		}

		// 计算 UTF8 编码需要的最大字节数
		int maxByteCount = Encoding.UTF8.GetByteCount(value);
		Put(maxByteCount); // 写入长度

		EnsureCapacity(maxByteCount);

		// 直接将字符串编码到缓冲区的 Span 中
		// 这不会产生临时 byte[]
		int actualBytes = Encoding.UTF8.GetBytes(value, 0, value.Length, _buffer, _position);
		_position += actualBytes;
	}

	// 确保缓冲区有足够的空间
	private void EnsureCapacity(int additional) {
		// 如果当前缓冲区不够大,租用一个更大的
		if (_position + additional > _buffer.Length) {
			byte[] newBuffer = _pool.Rent((_position + additional) * 2);
			Array.Copy(_buffer, 0, newBuffer, 0, _position);
			_pool.Return(_buffer);
			_buffer = newBuffer;
		}
	}

	public ArraySegment<byte> Data => new ArraySegment<byte>(_buffer, 0, _position);

	public void Dispose() {
		if (_buffer != null) {
			_pool.Return(_buffer);
			_buffer = null; // 设置为 null,防止重复 Return
			_position = 0;
		}
	}
}