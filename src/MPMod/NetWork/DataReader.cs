using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace WKMultiMod.src.NetWork;

public class DataReader {
	private ReadOnlyMemory<byte> _data;
	private int _position;

	public void SetSource(ArraySegment<byte> source) {
		_data = source;
		_position = 0;
	}

	public int GetInt() {
		int val = BinaryPrimitives.ReadInt32LittleEndian(_data.Span.Slice(_position));
		_position += 4;
		return val;
	}

	public ulong GetULong() {
		ulong val = BinaryPrimitives.ReadUInt64LittleEndian(_data.Span.Slice(_position));
		_position += 8;
		return val;
	}

	public string GetString() {
		int length = GetInt();
		string val = Encoding.UTF8.GetString(_data.Span.Slice(_position, length));
		_position += length;
		return val;
	}
}