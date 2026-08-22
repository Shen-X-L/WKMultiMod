global using IDType = ulong;
using System;
using System.Buffers.Binary;

namespace WKMPMod.Data;
/// <summary>
/// 协议头结构: <br/>
/// 0-7字节: 发送者ID (ulong) <br/>
/// 8-15字节: 目标ID (ulong) <br/>
/// 16-17字节: 数据包类型 (ushort) <br/>
/// <see cref="MPWriterPool"/> 和 <see cref="MPReaderPool"/> 已经封装了读写协议头的逻辑,使用时无需关心具体偏移,只需调用相应方法即可. <br/>
/// </summary>
public static class MPProtocol {
	public const int SenderIdOffset = 0;
	public const int TargetIdOffset = 8;
	public const int PacketTypeOffset = 16;
	public const int HeaderSize = 18;
	// 广播Id
	public const IDType BroadcastId = 0;
	// 特殊Id (必须解包)
	public const IDType SpecialId = 1;
}

public static class MPReaderPool {
	// 为每个线程创建一个独立的 Reader 实例
	[ThreadStatic]
	private static DataReader _threadReader;

	/// <summary>
	/// 仅解析头部信息,不移动任何指针
	/// </summary>
	public static (ulong senderId, ulong targetId, ushort packetType) PeekHeader(ArraySegment<byte> data) {
		ReadOnlySpan<byte> span = data;
		return (
			BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(MPProtocol.SenderIdOffset)),
			BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(MPProtocol.TargetIdOffset)),
			BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(MPProtocol.PacketTypeOffset))
		);
	}

	public static DataReader GetReader(ArraySegment<byte> payload) {
		// 如果当前线程还没创建过 Reader,则创建一个
		if (_threadReader == null) {
			_threadReader = new DataReader();
		}

		// 装填数据并重置指针
		_threadReader.SetSource(payload.Array, payload.Offset, payload.Count);
		return _threadReader;
	}

	public static DataReader GetReader(byte[] data) {
		if (data == null) return null;

		// 将整个 byte[] 包装成 ArraySegment,Offset 为 0,长度为 data.Length
		return GetReader(new ArraySegment<byte>(data));
	}
}

public static class MPWriterPool {
	[ThreadStatic]
	private static DataWriter _threadWriter;

	public static DataWriter GetWriter() {
		if (_threadWriter == null) {
			_threadWriter = new DataWriter();
		}
		_threadWriter.Reset(); // 清空之前的数据,准备重新写入
		return _threadWriter;
	}

	public static DataWriter GetWriter(ulong senderId, ulong targetId, PacketType type) {
		if (_threadWriter == null) {
			_threadWriter = new DataWriter();
		}
		_threadWriter.Reset(); // 清空之前的数据,准备重新写入
		_threadWriter.Put(senderId);
		_threadWriter.Put(targetId);
		_threadWriter.Put((ushort)type);
		return _threadWriter;
	}
}
