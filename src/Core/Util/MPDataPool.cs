using System;
using System.Collections.Generic;
using System.Text;
using WKMPMod.Data;
namespace WKMPMod.Util;

public static class MPReaderPool {
	// 为每个线程创建一个独立的 Reader 实例
	[ThreadStatic]
	private static DataReader _threadReader;

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
}