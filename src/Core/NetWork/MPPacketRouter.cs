using Steamworks.Data;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.Util;
using static WKMPMod.Data.MPReaderPool;
using static System.Buffers.Binary.BinaryPrimitives;

namespace WKMPMod.NetWork;

public class MPPacketRouter {
	// 路由表
	private static readonly Action<ulong, DataReader>[] FastHandlers = new Action<ulong, DataReader>[64];
	public static TickTimer DebugTick = new TickTimer(1);

	#region[初始化路由表]
	/// <summary>
	/// 初始化字典
	/// </summary>
	static MPPacketRouter() {
		// 反射MPPacketService类
		// 返回迭代器<PacketType, Action<ulong, DataReader>>
		var handlerEntries = typeof(MPPacketHandlers)
			// 获取所有静态方法, 包括非公共的
			.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
			// 获取带有 MPPacketHandlerAttribute 特性的 方法. false表示不搜索继承链, 只看当前方法
			.Where(method => method.GetCustomAttributes(typeof(MPPacketHandlerAttribute), false).Length > 0)
			// 对每个方法进行处理, 其生成多个IEnumerable<(PacketType, Delegate)>并扁平化
			.SelectMany(method => ProcessMethod(method));

		int count = 0;

		// 将反射获取的结果存入数组
		foreach (var (packetType, action) in handlerEntries) { 
			ushort handlerId = (ushort)packetType;

			if (handlerId >= 0 && handlerId < FastHandlers.Length) {
				if (FastHandlers[handlerId] != null) {
					Localization.Get("MPPacketRouter.PacketHandlerOverridden", 
						packetType, FastHandlers[handlerId].Method.Name, action.Method.Name);
				}

				// 将 Action 存入数组
				FastHandlers[handlerId] = (Action<ulong, DataReader>)action;
				count++;

				//MPMain.LogInfo(Localization.GetByPath("MPPacketRouter.ShowAllRouteTable", packetType, action.Method.Name));
			} else {
				Localization.Get("MPPacketRouter.PacketTypeOutOfRange", (ushort)packetType, FastHandlers.Length - 1);
			}

		}
	}

	/// <summary>
	/// 处理反射获取的方法,转为IEnumerable &lt;(PacketType, Action&lt;ulong, DataReader&gt;)&gt; <br/>迭代器 &lt;包类型,无返回值委托&gt;
	/// </summary>
	private static IEnumerable<(PacketType, Action<ulong, DataReader>)> ProcessMethod(MethodInfo method) {
		// 获取方法上的所有 MPPacketHandlerAttribute 特性
		var attrs = method.GetCustomAttributes<MPPacketHandlerAttribute>();
		// 获取方法的参数信息
		var parameters = method.GetParameters();

		var action = CreateAction(method, parameters);
		if (action == null) yield break;

		foreach (var attr in attrs) {
			yield return (attr.packetType, action);
		}
	}

	/// <summary>
	/// 将方法转为委托
	/// </summary>
	private static Action<ulong, DataReader> CreateAction(MethodInfo method, ParameterInfo[] parameters) {
		try {
			// 验证参数签名
			if (parameters.Length == 2 &&
				parameters[0].ParameterType == typeof(ulong) &&
				parameters[1].ParameterType == typeof(DataReader)) {

				// 创建委托并强转为 Action
				// Delegate.CreateDelegate 的第一个参数是委托的类型
				return (Action<ulong, DataReader>)Delegate.CreateDelegate(
					typeof(Action<ulong, DataReader>),
					null, // 如果是静态方法,这里传 null
					method
				);
			}
			throw new InvalidOperationException($"Unsupported parameter signature for {method.Name}. Expected (ulong, DataReader).");
		} catch (Exception e) {
			MPMain.LogError(Localization.Get(
				"MPPacketRouter.FailedToBind", method.Name, e.Message));
			return null;
		}
	}
	#endregion

	#region[注册路由接口]

	public static void RegisterRoute(PacketType packetType, Action<ulong, DataReader> handler) {
		ushort handlerId = (ushort)packetType;
		// 前64个ID保留给内置处理器,用户注册的处理器必须从64开始
		if (handlerId >= 64 && handlerId < FastHandlers.Length) {
			if (FastHandlers[handlerId] != null) {
				Localization.Get("MPPacketRouter.PacketHandlerOverridden",
					packetType, FastHandlers[handlerId].Method.Name, handler.Method.Name);
			}
			FastHandlers[handlerId] = handler;
			MPMain.LogInfo(Localization.Get("MPPacketRouter.RegisterRouteSuccess", packetType, handler.Method.Name));
		} else {
			Localization.Get("MPPacketRouter.PacketTypeOutOfRange", (ushort)packetType, FastHandlers.Length - 1);
		}
	}

	public static void RegisterRoute(MethodInfo method) {
		var handlers = ProcessMethod(method);
		foreach (var (packetType, handler) in handlers) {
			RegisterRoute(packetType, handler);
		}
	}

	public static void RegisterRoute(MethodInfo method, params PacketType[] packetTypes) {
		var parameters = method.GetParameters();
		var action = CreateAction(method, parameters);
		if (action == null) return;
		foreach (var packetType in packetTypes) {
			RegisterRoute(packetType, action);
		}
	}

	#endregion

	#region[生命周期函数]
	public static void Initialize() {
		MPEventBusNet.OnReceiveData += Route;
	}
	#endregion

	#region[数据转换+路由]
	public static void Route(ulong connectionId, ArraySegment<byte> data) {
		// 确保数据足够读取一个整数(数据包类型)
		if (data.Array == null || data.Count < 18) return;

		// 直接解析头部
		ReadOnlySpan<byte> span = data;
		// 发送方ID
		var (senderId, targetId, packetType) = PeekHeader(data);

		// Debug
		//string hexString2 = BitConverter.ToString(data.Array, data.Offset + 18, data.Count - 18);
		//MPMain.LogInfo($"[MP Debug] 16进制数据: {hexString2}");

		// 转发:目标不是我,也不是广播,也不是特殊判断ID
		if (targetId != MPSteamworks.Instance.UserSteamId
			&& targetId != MPProtocol.BroadcastId
			&& targetId != MPProtocol.SpecialId) {

			ProcessForwardToPeer(targetId, (PacketType)packetType, data);
			return; // 结束
		}

		// 广播:如果是广播,且不是我发出的
		if (targetId == MPProtocol.BroadcastId
			&& senderId != MPSteamworks.Instance.UserSteamId) {

			// P2P直连模式不需要进行广播包转发
			//ProcessBroadcastExcept(senderId, data);
			// 继续执行,因为主机也要处理广播包
		}

		if (packetType < 0 || packetType >= FastHandlers.Length || FastHandlers[packetType] == null) {
			MPMain.LogError(Localization.Get("MPPacketRouter.NoServiceFound", packetType));
			return;
		}

		var reader = GetReader(data.Slice(MPProtocol.HeaderSize));

		try {
			FastHandlers[packetType](senderId, reader);
		} catch (Exception e) {
			if (DebugTick.TryTick()) {
				MPMain.LogError(Localization.Get("MPPacketRouter.HandlerException", packetType, e.Message));
				MPMain.LogError($"[MP Debug] receive package. connectionId: {connectionId} senderId: {senderId} targetId: {targetId} packetType: {packetType}");
				MPMain.LogError($"[MP Debug] Hexadecimal data: {BitConverter.ToString(data.Array, data.Offset + 18, data.Count - 18)}");
			}
		}
	}
	#endregion

	#region[网络发送工具类]
	/// <summary>
	/// 转发网络数据包到指定的客户端
	/// </summary>
	private static void ProcessForwardToPeer(ulong targetId, PacketType type,ArraySegment<byte> data) {
		// 解析类型
		SendType st = (type == PacketType.PlayerDataUpdate)
			? SendType.Unreliable : SendType.Reliable;
		// 直接从 segment 获取偏移和长度
		MPSteamworks.Instance.SendToPeer(targetId, data.Array, data.Offset, data.Count, st);
	}

	/// <summary>
	/// 广播数据包到所有客户端
	/// </summary>
	public static void ProcessBroadcast(ArraySegment<byte> data) {
		// 直接从 segment 获取偏移和长度
		int offset = data.Offset;
		int count = data.Count;

		// 解析类型
		PacketType type = (PacketType)ReadUInt16LittleEndian(data.AsSpan(16, 2));

		SendType st = (type == PacketType.PlayerDataUpdate)
			? SendType.Unreliable : SendType.Reliable;

		// 将全套参数传给底层
		MPSteamworks.Instance.Broadcast(data.Array, offset, count, st);
	}

	/// <summary>
	/// 广播数据包到所有客户端 (除了发送者)
	/// </summary>
	/// <param name="senderId">发送方ID</param>
	public static void ProcessBroadcastExcept(ulong senderId, ArraySegment<byte> data) {
		// 直接从 segment 获取偏移和长度
		int offset = data.Offset;
		int count = data.Count;

		// 解析类型
		PacketType type = (PacketType)ReadUInt16LittleEndian(data.AsSpan(16, 2));

		SendType st = (type == PacketType.PlayerDataUpdate)
			? SendType.Unreliable : SendType.Reliable;

		// 将全套参数传给底层
		MPSteamworks.Instance.BroadcastExcept(senderId, data.Array, offset, count, st);
	}
	#endregion

}

// AttributeTargets ValidOn: 作用类型 AttributeTargets.Method: 作用于方法
// AllowMultiple: 是否允许同一方法上使用多个该属性,这里设置为 true 以支持一个方法处理多个消息类型
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public class MPPacketHandlerAttribute : Attribute {
	public PacketType packetType { get; }
	public MPPacketHandlerAttribute(PacketType packetType) {
		this.packetType = packetType;
	}
}