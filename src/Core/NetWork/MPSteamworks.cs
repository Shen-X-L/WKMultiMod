
using Steamworks;
using Steamworks.Data;
using System;
using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.Util;

namespace WKMPMod.NetWork;

// 只做连接,不做业务逻辑
public class MPSteamworks : MonoBehaviour, ISocketManager, IConnectionManager {
	/// <summary>
	/// 网络消息结构
	/// </summary>
	public struct NetworkMessage {
		public ulong SenderId;
		public byte[] Data;      // 这是从池里借来的缓冲区
		public int Length;       // 实际有效数据长度
		public DateTime ReceiveTime;
	}

	/// <summary>
	/// 客户端连接信息类
	/// 封装SteamID和连接对象
	/// </summary>
	/// 
	// Debug日志输出间隔
	private TickTimer _debugTick = new TickTimer(5.0f);
	private TickTimer _debugTick1 = new TickTimer(3.0f);
	private TickTimer _debugTick2 = new TickTimer(10.0f);

	// 大厅缓存
	private Lobby _currentLobby;
	// 获取当前大厅Id
	public ulong CurrentLobbyId {
		get { return _currentLobby.Id.Value; }
	}
	// 检查是否在大厅中
	public bool IsInLobby {
		get { return _currentLobby.Id.IsValid; }
	}

	// 本机Id
	public ulong UserSteamId { get; private set; }
	// 之前的主机Id
	public ulong HostSteamId { get; private set; }
	// 广播Id
	public ulong BroadcastId { get; } = 0;
	// 特殊Id (必须解包)
	public ulong SpecialId { get; } = 1;

	// 服务器套接字管理器
	internal SocketManager _socketManager;
	// 客户端连接管理器
	internal ConnectionManager _connectionManager;
	// 已连接客户端字典
	internal Dictionary<SteamId, Connection> _connectedClients;
	// 连接协程句柄
	private Coroutine _connectionRoutine;

	// 是否有链接
	public bool HasConnections { get; private set; }

	// 消息队列
	private ConcurrentQueue<NetworkMessage> _messageQueue = new ConcurrentQueue<NetworkMessage>();
	// 数据池
	private static readonly ArrayPool<byte> _messagePool = ArrayPool<byte>.Shared;

	// 检查是否是大厅所有者
	public bool IsHost {
		get {
			if (_currentLobby.Id == 0) return false;
			return _currentLobby.Owner.Id == SteamClient.SteamId;
		}
	}

	// 获取大厅ID
	public ulong LobbyId {
		get => _currentLobby.Id.Value;
	}

	// 判断玩家是否在大厅
	public bool IsMemberInLobby(SteamId targetId) {
		foreach (var member in _currentLobby.Members) {
			if (member.Id == targetId) return true;
		}
		return false;
	}

	// 获取全部在线玩家
	public IEnumerable<Friend> Friends { get; private set; }

	#region[Unity组件生命周期函数]

	void Awake() {
		//SteamClient.Init(3195790u);

		try {
			if (!SteamClient.IsValid) {
				MPMain.LogError(Localization.Get("MPSteamworks", "SteamworksInitFailed"));
				return;
			}
			// 获取并显示用户Steam ID
			UserSteamId = SteamClient.SteamId;
			MPMain.LogInfo(Localization.Get("MPSteamworks", "SteamworksInitSuccess", SteamClient.Name, SteamClient.SteamId.ToString()));

			// 初始化Steam中继网络访问
			SteamNetworkingUtils.InitRelayNetworkAccess();

			// 订阅大厅事件 大部分只做转发
			// 本机加入大厅
			SteamMatchmaking.OnLobbyEntered += HandleLobbyEntered;
			// 该用户已经加入或正在加入大厅
			SteamMatchmaking.OnLobbyMemberJoined += HandleLobbyMemberJoined;
			// 该用户已离开或即将离开大厅
			SteamMatchmaking.OnLobbyMemberLeave += HandleLobbyMemberLeave;
			// 该用户在未离开大厅的情况下断线
			SteamMatchmaking.OnLobbyMemberDisconnected += HandleLobbyMemberDisconnected;
			// 当大厅成员数据或大厅所有权发生变更
			SteamMatchmaking.OnLobbyMemberDataChanged += HandleLobbyMemberDataChanged;

			// 初始化中继网络(必须调用)
			SteamNetworkingUtils.InitRelayNetworkAccess();

		} catch (Exception ex) {
			MPMain.LogError(Localization.Get("MPSteamworks", "SteamworksInitException", ex.Message));
		}

	}

	void Update() {
		// 关键：在 Update 中持续调用 RunCallbacks
		Steamworks.SteamClient.RunCallbacks();

		// 接收并处理网络数据
		_connectionManager?.Receive(32); // 客户端接收
		_socketManager?.Receive(32);     // 服务器接收

		// 处理数据队列
		ProcessMessageQueue();
	}

	void OnDestroy() {
		// 取消订阅大厅事件 大部分只做转发
		// 本机加入大厅
		SteamMatchmaking.OnLobbyEntered -= HandleLobbyEntered;
		// 该用户已经加入或正在加入大厅
		SteamMatchmaking.OnLobbyMemberJoined -= HandleLobbyMemberJoined;
		// 该用户已离开或即将离开大厅
		SteamMatchmaking.OnLobbyMemberLeave -= HandleLobbyMemberLeave;
		// 该用户在未离开大厅的情况下断线
		SteamMatchmaking.OnLobbyMemberDisconnected -= HandleLobbyMemberDisconnected;
		// 当大厅成员数据或大厅所有权发生变更
		SteamMatchmaking.OnLobbyMemberDataChanged -= HandleLobbyMemberDataChanged;

		DisconnectAll();
	}

	#endregion

	#region[RAII函数]

	/// <summary>
	/// 断开所有连接(清理网络资源)
	/// </summary>
	public void DisconnectAll() {
		// 关闭客户端连接
		_connectionManager?.Close();
		_connectionManager = null; // 必须置空 防止 Update 继续 Receive
								   // 关闭服务器套接字
		_socketManager?.Close();
		_socketManager = null;

		// 清理所有连接记录
		// 字典初始化/清理
		if (_connectedClients == null) _connectedClients = new Dictionary<SteamId, Connection>();
		else _connectedClients.Clear();

		// 状态重置
		HasConnections = false;
		HostSteamId = 0;

		// 离开大厅(如果有)
		if (_currentLobby.Id.IsValid) {
			try {
				_currentLobby.Leave();
			} catch { }
			_currentLobby = default;
		}

		// 清空消息队列
		while (_messageQueue.TryDequeue(out _)) { }

		MPMain.LogInfo(Localization.Get("MPSteamworks", "AllConnectionsDisconnected"));
	}

	#endregion

	#region[发送数据函数]

	/// <summary>
	/// 主机/客户端 发送数据: 本机->目标玩家
	/// </summary>
	public void SendToPeer(ulong targetId, DataWriter writer,
					 SendType sendType = SendType.Reliable, ushort laneIndex = 0) {
		var segment = writer.Data;
		if (IsHost) {
			HandleSendToPeer(targetId, segment.Array, segment.Offset, segment.Count, sendType, laneIndex);
		} else {
			HandleSendToHost(segment.Array, segment.Offset, segment.Count, sendType, laneIndex);
		}
	}

	/// <summary>
	/// 主机/客户端 发送数据: 本机->目标玩家
	/// </summary>
	public void SendToPeer(ulong targetId, byte[] data,
					 SendType sendType = SendType.Reliable, ushort laneIndex = 0) {
		if (IsHost) {
			HandleSendToPeer(targetId, data, sendType, laneIndex);
		} else {
			HandleSendToHost(data, sendType, laneIndex);
		}
	}

	/// <summary>
	/// 主机/客户端 发送数据: 本机->目标玩家
	/// </summary>
	public void SendToPeer(ulong targetId, byte[] data, int offset, int length,
					 SendType sendType = SendType.Reliable, ushort laneIndex = 0) {
		if (IsHost) {
			HandleSendToPeer(targetId, data, offset, length, sendType, laneIndex);
		} else {
			HandleSendToHost(data, offset, length, sendType, laneIndex);
		}
	}

	/// <summary>
	/// 主机/客户端 发送数据: 本机->所有连接玩家
	/// </summary>
	public void Broadcast(DataWriter writer, SendType sendType = SendType.Reliable, ushort laneIndex = 0) {
		var segment = writer.Data;
		if (IsHost) {
			HandleBroadcast(segment.Array, segment.Offset, segment.Count, sendType, laneIndex);
		} else {
			HandleSendToHost(segment.Array, segment.Offset, segment.Count, sendType, laneIndex);
		}
	}

	/// <summary>
	/// 主机/客户端 发送数据: 本机->所有连接玩家
	/// </summary>
	public void Broadcast(byte[] data, SendType sendType = SendType.Reliable, ushort laneIndex = 0) {
		if (IsHost) {
			HandleBroadcast(data, sendType, laneIndex);
		} else {
			HandleSendToHost(data, sendType, laneIndex);
		}
	}

	/// <summary>
	/// 主机/客户端 发送数据: 本机->所有连接玩家
	/// </summary>
	public void Broadcast(byte[] data, int offset, int length,
					 SendType sendType = SendType.Reliable, ushort laneIndex = 0) {
		if (IsHost) {
			HandleBroadcast(data, offset, length, sendType, laneIndex);
		} else {
			HandleSendToHost(data, offset, length, sendType, laneIndex);
		}
	}

	/// <summary>
	/// 仅主机 发送数据: 本机->除个别玩家外所有连接玩家
	/// </summary>
	public void BroadcastExcept(ulong steamId, byte[] data, int offset, int length,
					 SendType sendType = SendType.Reliable, ushort laneIndex = 0) {
		if (IsHost) {
			HandleBroadcastExcept(steamId, data, offset, length, sendType, laneIndex);
		}
	}

	/// <summary>
	/// 仅客户端 发送数据: 本机->主机玩家
	/// </summary>
	private void HandleSendToHost(byte[] data, SendType sendType = SendType.Reliable,
		ushort laneIndex = 0) {

		if (IsHost || _connectionManager == null) {
			return;
		}

		_connectionManager.Connection.SendMessage(data, sendType, laneIndex);
	}

	/// <summary>
	/// 仅客户端 发送数据: 本机->主机玩家
	/// </summary>
	private void HandleSendToHost(byte[] data, int offset, int length,
		SendType sendType = SendType.Reliable, ushort laneIndex = 0) {

		if (IsHost || _connectionManager == null) {
			return;
		}

		_connectionManager.Connection.SendMessage(data, offset, length, sendType, laneIndex);
	}

	/// <summary>
	/// 仅主机 发送数据: 本机->所有连接玩家
	/// </summary>
	private void HandleBroadcast(byte[] data, SendType sendType = SendType.Reliable,
		ushort laneIndex = 0) {

		// Debug
		bool canLog = _debugTick.TryTick();
		if (canLog) {
			MPMain.LogInfo(Localization.Get(
				"MPSteamworks", "StartedBroadcasting", _connectedClients.Count.ToString()));
		}

		foreach (var (steamId, connection) in _connectedClients) {
			try {
				if (canLog) {
					MPMain.LogInfo(Localization.Get(
						"MPSteamworks", "SendingToConnection",
						steamId.ToString(), connection.Id.ToString()));
				}

				connection.SendMessage(data, sendType, laneIndex);
			} catch (Exception ex) {
				MPMain.LogError(Localization.Get(
					"MPSteamworks", "BroadcastingException", ex.Message));
			}
		}
	}

	/// <summary>
	/// 仅主机 发送数据: 本机->所有连接玩家
	/// </summary>
	private void HandleBroadcast(byte[] data, int offset, int length,
		SendType sendType = SendType.Reliable, ushort laneIndex = 0) {

		// Debug
		bool canLog = _debugTick.TryTick();
		if (canLog) {
			MPMain.LogInfo(Localization.Get(
				"MPSteamworks", "StartedBroadcasting", _connectedClients.Count.ToString()));
		}

		foreach (var (steamId, connection) in _connectedClients) {
			try {
				if (canLog) {
					MPMain.LogInfo(Localization.Get(
						"MPSteamworks", "SendingToConnection",
						steamId.ToString(), connection.Id.ToString()));
				}

				connection.SendMessage(data, offset, length, sendType, laneIndex);
			} catch (Exception ex) {
				MPMain.LogError(Localization.Get(
					"MPSteamworks", "BroadcastingException", ex.Message));
			}
		}
	}

	/// <summary>
	/// 仅主机 发送数据: 本机->除个别玩家外所有连接玩家
	/// </summary>
	/// <param name="steamId">被排除的玩家</param>
	private void HandleBroadcastExcept(ulong steamId, byte[] data,
		SendType sendType = SendType.Reliable, ushort laneIndex = 0) {

		// Debug
		bool canLog = _debugTick.TryTick();
		if (canLog) {
			MPMain.LogInfo(Localization.Get(
				"MPSteamworks", "StartedBroadcasting", _connectedClients.Count.ToString()));
		}

		foreach (var (tempSteamId, connection) in _connectedClients) {
			if (steamId == tempSteamId)
				continue;
			try {
				if (canLog) {
					MPMain.LogInfo(Localization.Get(
						"MPSteamworks", "SendingToConnection",
						steamId.ToString(), connection.Id.ToString()));
				}

				connection.SendMessage(data, sendType, laneIndex);
			} catch (Exception ex) {
				MPMain.LogError(Localization.Get(
					"MPSteamworks", "BroadcastingException", ex.Message));
			}
		}
	}

	/// <summary>
	/// 仅主机 发送数据: 本机->除个别玩家外所有连接玩家
	/// </summary>
	/// <param name="steamId">被排除的玩家</param>
	private void HandleBroadcastExcept(ulong steamId, byte[] data, int offset, int length,
		SendType sendType = SendType.Reliable, ushort laneIndex = 0) {

		// Debug
		bool canLog = _debugTick.TryTick();
		if (canLog) {
			MPMain.LogInfo(Localization.Get(
				"MPSteamworks", "StartedBroadcasting", _connectedClients.Count.ToString()));
		}

		foreach (var (tempSteamId, connection) in _connectedClients) {
			if (steamId == tempSteamId)
				continue;
			try {
				if (canLog) {
					MPMain.LogInfo(Localization.Get(
						"MPSteamworks", "SendingToConnection",
						steamId.ToString(), connection.Id.ToString()));
				}
				connection.SendMessage(data, offset, length, sendType, laneIndex);
			} catch (Exception ex) {
				MPMain.LogError(Localization.Get(
					"MPSteamworks", "BroadcastingException", ex.Message));
			}
		}
	}

	/// <summary>
	/// 仅主机 发送数据: 本机->特定玩家
	/// </summary>
	private void HandleSendToPeer(ulong steamId, byte[] data,
		SendType sendType = SendType.Reliable, ushort laneIndex = 0) {

		try {
			_connectedClients[steamId].SendMessage(data, sendType, laneIndex);
		} catch (Exception ex) {
			MPMain.LogError(Localization.Get(
				"MPSteamworks", "UnicastException", ex.Message, steamId.ToString()));
		}
	}

	/// <summary>
	/// 仅主机 发送数据: 本机->特定玩家
	/// </summary>
	private void HandleSendToPeer(ulong steamId, byte[] data, int offset, int length,
		SendType sendType = SendType.Reliable, ushort laneIndex = 0) {

		try {
			_connectedClients[steamId].SendMessage(data, offset, length, sendType, laneIndex);
		} catch (Exception ex) {
			MPMain.LogError(Localization.Get(
				"MPSteamworks", "UnicastException", ex.Message, steamId.ToString()));
		}
	}
	#endregion

	#region[消息处理函数]
	/// <summary>
	/// 接收数据: 任意玩家->消息队列
	/// </summary>
	private void HandleIncomingRawData(SteamId senderId, IntPtr data, int size) {
		// 1. 从池里借出一块内存。注意：buffer.Length 可能 >= size
		byte[] buffer = _messagePool.Rent(size);

		// 2. 将非托管指针数据拷贝到借来的数组中
		System.Runtime.InteropServices.Marshal.Copy(data, buffer, 0, size);

		// 3. 入队
		_messageQueue.Enqueue(new NetworkMessage {
			SenderId = senderId.Value,
			Data = buffer,
			Length = size,
			ReceiveTime = DateTime.UtcNow
		});
	}

	/// <summary>
	/// 处理消息队列: 消息队列->ReceiveSteamData总线
	/// </summary>
	private void ProcessMessageQueue() {
		int processedCount = 0;
		const int maxMessagesPerFrame = 50;

		while (processedCount < maxMessagesPerFrame && _messageQueue.TryDequeue(out var message)) {
			try {
				// 使用 ArraySegment 包装
				var segment = new ArraySegment<byte>(message.Data, 0, message.Length);

				// 触发总线
				MPEventBusNet.NotifyReceive(message.SenderId, segment);

				processedCount++;
			} catch (Exception ex) {
				MPMain.LogError(Localization.Get("MPSteamworks", "MessageQueueException", ex.Message));
			} finally {
				// 数据归还缓冲区
				_messagePool.Return(message.Data);
			}
		}
	}

	#endregion

	#region[连接/断连 回调函数]

	/// <summary>
	/// 接收数据: 玩家断开连接 -> PlayerDisconnected总线
	/// </summary>
	private void OnPlayerDisconnected(ulong steamId) {
		if (_connectedClients.ContainsKey(steamId)) {
			_connectedClients.Remove(steamId);

			// 重连检测
			if (IsHost && IsMemberInLobby(steamId))
				//StartCoroutine(ConnectionController(steamId, true));

				MPMain.LogInfo(Localization.Get(
					"MPSteamworks", "PlayerDisconnectedCleaned", steamId.ToString()));
			// 检查是否还有剩余连接
			HasConnections = _connectedClients.Count > 0;

			// 重连失败,触发业务层销毁玩家
			if (!_connectedClients.ContainsKey(steamId))
				MPEventBusNet.NotifyPlayerDisconnected(steamId);

		}
	}
	#endregion

	#region[连接器管理函数]

	/// <summary>
	/// 主动连接到主机
	/// </summary>
	public void ConnectToHost() {
		SteamId hostId = _currentLobby.Owner.Id;
		if (IsHost) {
			return;
		}
		_connectionManager = SteamNetworkingSockets.ConnectRelay<ConnectionManager>(hostId, 1);
		_connectionManager.Interface = this; // 设置回调接口
	}

	/// <summary>
	/// 协程中尝试连接主机
	/// </summary>
	public void TryConnectToHost() {
		// 1. 如果已有连接尝试，先停止它，防止多个重连逻辑冲突
		if (_connectionRoutine != null) {
			StopCoroutine(_connectionRoutine);
		}

		// 2. 开启新的连接流程
		_connectionRoutine = StartCoroutine(DoConnectToHost());
	}

	/// <summary>
	/// 连接主机协程
	/// </summary>
	private IEnumerator DoConnectToHost() {
		SteamId hostId = _currentLobby.Owner.Id;
		int attempts = 0;

		// 自己是主机,停止连接流程
		if (hostId == SteamClient.SteamId) {
			yield break;
		}
		// 如果已经有旧连接,先彻底清理
		if (_connectionManager != null) {
			_connectionManager.Connection.Close();
			_connectionManager = null;
		}

		yield return new WaitForSeconds(0.5f);

		while (attempts < 5) {
			attempts++;
			MPMain.LogWarning(Localization.Get("MPSteamworks", "AttemptingToConnect", attempts));

			// 这就是你原本的逻辑
			_connectionManager = SteamNetworkingSockets.ConnectRelay<ConnectionManager>(hostId, 1);
			_connectionManager.Interface = this;

			// 给底层网络一点点时间（比如 2 秒）去建立握手
			yield return new WaitForSeconds(2.0f);

			// 检查连接状态（假设你的 ConnectionManager 能获取 Connection 状态）
			if (_connectionManager.ConnectionInfo.State == ConnectionState.Connected) {
				yield break;
			}

			// 如果走到这里,说明这一轮连接失败了 需要清理连接
			if (_connectionManager != null) {
				_connectionManager.Connection.Close();
				_connectionManager = null;
			}
			yield return new WaitForSeconds(1.0f); // 等待一下再重试
		}
		MPMain.LogError(Localization.Get("MPSteamworks", "ConnectToHostFailed"));
	}

	/// <summary>
	/// 创建监听socket
	/// </summary>
	public void CreateListeningSocket() {
		if (!IsHost) {
			return;
		}
		try {
			_socketManager = SteamNetworkingSockets.CreateRelaySocket<SocketManager>(1);
			_socketManager.Interface = this;
		} catch (Exception socketEx) {
			MPMain.LogError(Localization.Get(
				"MPSteamworks", "SocketCreateException", socketEx.Message));
		}
	}

	#endregion

	#region[创建/加入大厅函数]

	/// <summary>
	/// 创建大厅(主机模式)- 异步版本
	/// </summary>
	public async Task<bool> CreateRoomAsync(string roomName, int maxPlayers) {
		// 清理全部连接
		DisconnectAll();
		await Task.Yield();

		try {
			if (!SteamClient.IsValid) {
				MPMain.LogError(Localization.Get("MPSteamworks", "SteamClientInvalid"));
				return false;
			}

			// 核心：直接 await 任务
			Lobby? lobbyResult = await SteamMatchmaking.CreateLobbyAsync(maxPlayers);

			// 只检查结果并返回,移除所有同步大厅设置和 Socket 创建！
			if (!lobbyResult.HasValue) {
				MPMain.LogError(Localization.Get("MPSteamworks", "CreateLobbyFailed"));
				return false;
			}

			_currentLobby = lobbyResult.Value;

			MPMain.LogInfo(Localization.Get("MPSteamworks", "LobbyCreatedSuccess", _currentLobby.Id.ToString()));

			// 设置大厅信息
			_currentLobby.SetData("name", roomName);
			_currentLobby.SetData("game_version", Application.version);
			_currentLobby.SetData("owner", SteamClient.SteamId.ToString());
			_currentLobby.SetPublic();
			_currentLobby.SetJoinable(true);
			_currentLobby.Owner = new Friend(SteamClient.SteamId);

			// 获取Socket
			CreateListeningSocket();

			return true; // 成功
		} catch (Exception ex) {
			MPMain.LogError(Localization.Get("MPSteamworks", "CreateLobbyException", ex.Message));
			return false; // 失败
		}
	}

	/// <summary>
	/// CreateRoom 异步启动包装器
	/// </summary>
	public void CreateRoom(string roomName, int maxPlayers, Action<bool> callback) {
		// 启动异步
		StartCoroutine(RunAsync(CreateRoomAsync(roomName, maxPlayers), callback));
	}

	/// <summary>
	/// 加入大厅(客户端模式)- 异步版本
	/// </summary>
	public async Task<bool> JoinRoomAsync(Lobby lobby) {
		// 清理全部连接
		DisconnectAll();

		try {
			// 核心改变：直接 await 任务
			RoomEnter result = await lobby.Join();

			// 检查 RoomEnter 结果
			if (result != RoomEnter.Success) {
				throw new Exception($"[MPSW] Failed to join Steam lobby: {result.ToString()}");
			}

			_currentLobby = lobby;
			string roomName = _currentLobby.GetData("name")
				?? Localization.Get("MPSteamworks", "NullLobbyName");
			MPMain.LogInfo(Localization.Get("MPSteamworks", "JoinLobbySuccess", roomName));

			return true;
		} catch (Exception ex) {
			MPMain.LogError(Localization.Get("MPSteamworks", "JoinLobbyException", ex.Message));
			return false;
		}
	}

	/// <summary>
	/// JoinRoom 异步启动包装器
	/// </summary>
	public void JoinRoom(ulong lobbyId, Action<bool> callback) {
		Lobby lobby = new Lobby(lobbyId);
		// 使用 Unity 的扩展方法来启动 async Task
		StartCoroutine(RunAsync(JoinRoomAsync(lobby), callback));
	}

	#endregion

	#region[SteamMatchmaking事件处理函数]

	/// <summary>
	/// 接收数据: 进入到大厅->LobbyEntered总线
	/// </summary>
	private void HandleLobbyEntered(Lobby lobby) {
		_currentLobby = lobby;
		HostSteamId = lobby.Owner.Id;
		MPMain.LogInfo(Localization.Get("MPSteamworks", "EnteredLobby", lobby.Id.ToString()));
		// 连接主机
		if (!IsHost) {
			ConnectToHost();
		}
		// 发布事件到总线
		MPEventBusNet.NotifyLobbyEntered(lobby);
	}

	/// <summary>
	/// 接收数据: 大厅有成员加入->LobbyMemberJoined总线->连接新玩家
	/// </summary>
	private void HandleLobbyMemberJoined(Lobby lobby, Friend friend) {
		if (lobby.Id == _currentLobby.Id) {
			MPMain.LogInfo(Localization.Get("MPSteamworks", "PlayerJoinedRoom", friend.Name));

			// 发布事件到总线
			MPEventBusNet.NotifyLobbyMemberJoined(friend.Id);
		}
	}

	/// <summary>
	/// 接收数据: 大厅有成员离开->LobbyMemberLeft总线
	/// </summary>
	private void HandleLobbyMemberLeave(Lobby lobby, Friend friend) {
		if (lobby.Id == _currentLobby.Id) {
			MPMain.LogInfo(Localization.Get("MPSteamworks", "PlayerLeftRoom", friend.Name));

			// 发布事件到总线
			MPEventBusNet.NotifyLobbyMemberLeft(friend.Id);

			// 只在这里处理连接清理
			OnPlayerDisconnected(friend.Id);
		}
	}

	/// <summary>
	/// 接收数据: 大厅有成员断开连接->LobbyMemberLeft总线
	/// </summary>
	private void HandleLobbyMemberDisconnected(Lobby lobby, Friend friend) {
		if (lobby.Id == _currentLobby.Id) {
			MPMain.LogInfo(Localization.Get("MPSteamworks", "PlayerDisconnectedFromLobby", friend.Name));

			// 发布事件到总线
			//MPEventBusNet.NotifyLobbyMemberLeft(friend.Id);

			// 只在这里处理连接清理
			//OnPlayerDisconnected(friend.Id);
		}
	}

	/// <summary>
	/// 接收数据: 大厅数据变更->
	/// 主机变更->LobbyHostChanged总线
	/// </summary>
	private void HandleLobbyMemberDataChanged(Lobby lobby, Friend friend) {
		// 大厅变更
		if (lobby.Id != _currentLobby.Id) {
			// 更新部分大厅数据

			MPMain.LogInfo(Localization.Get(
	"			MPSteamworks", "LobbyIdChanged", _currentLobby.Id.ToString(), lobby.Id.ToString()));
			_currentLobby = lobby;
			return;
		}
		// 原大厅 更新部分大厅数据
		_currentLobby = lobby;
		// 获取当前大厅真正的主机(Owner)
		SteamId currentOwnerId = lobby.Owner.Id;
		// 检查所有权是否发生了变更
		if (HostSteamId != 0 && HostSteamId != currentOwnerId) {
			if (currentOwnerId == SteamClient.SteamId) {
				// 重新创建监听Socket
				CreateListeningSocket();
			} else {
				// 连接主机
				TryConnectToHost();
			}
			MPMain.LogInfo(Localization.Get(
				"MPSteamworks", "HostChanged", HostSteamId.ToString(), currentOwnerId.ToString()));

			// 触发主机变更总线
			MPEventBusNet.NotifyLobbyHostChanged(lobby, HostSteamId);
			// 更新主机Id
			HostSteamId = currentOwnerId;
		}
	}
	#endregion

	#region[SocketManager接口实现]
	// 仅主机: 有玩家正在接入
	void ISocketManager.OnConnecting(Connection connection, ConnectionInfo info) {
		MPMain.LogInfo(Localization.Get(
			"MPSteamworks", "PlayerConnecting", info.Identity.SteamId.ToString()));
		connection.Accept();
	}

	// 仅主机: 有玩家已经接入
	void ISocketManager.OnConnected(Connection connection, ConnectionInfo info) {
		var steamId = info.Identity.SteamId;
		MPMain.LogInfo(Localization.Get(
			"MPSteamworks", "PlayerConnected", steamId.ToString(), connection.Id, info.State));

		if (!_connectedClients.ContainsKey(steamId)) {
			_connectedClients.Add(steamId, connection);
			MPEventBusNet.NotifyPlayerConnected(steamId);
			HasConnections = true;
		}
		return;
	}

	// 仅主机: 连接被本地或远程关闭
	void ISocketManager.OnDisconnected(Connection connection, ConnectionInfo info) {
		if (_connectedClients.Remove(info.Identity.SteamId)) {
			MPMain.LogError(Localization.Get("MPSteamworks", "DisconnectedDetails", info.ToString()));
			connection.Close();
			OnPlayerDisconnected(info.Identity.SteamId);
		}
	}

	// 仅主机: 接收消息
	void ISocketManager.OnMessage(Connection connection, NetIdentity identity,
								  IntPtr data, int size, long messageNum,
								  long recvTime, int channel) {
		HandleIncomingRawData(identity.SteamId, data, size);
	}
	#endregion

	#region[ConnectionManager接口实现]
	// 仅客户端: 正在去连接
	void IConnectionManager.OnConnecting(ConnectionInfo info) { }

	// 仅客户端: 连接已建立
	void IConnectionManager.OnConnected(ConnectionInfo info) {
		SteamId steamId = info.Identity.SteamId;
		MPMain.LogInfo(Localization.Get(
			"MPSteamworks", "AlreadyActiveConnected", steamId.ToString(), info.State));
		HasConnections = true;
	}

	// 仅客户端: 接收消息
	void IConnectionManager.OnMessage(IntPtr data, int size, long messageNum, long recvTime, int channel) {
		HandleIncomingRawData(HostSteamId, data, size);
	}

	// 仅客户端: 连接被本地或远程关闭
	void IConnectionManager.OnDisconnected(ConnectionInfo info) {
		OnPlayerDisconnected(info.Identity.SteamId);
	}
	#endregion

	#region[工具函数]
	/// <summary>
	/// 这是一个通用的辅助方法,用于将 async Task<bool> 包装到 Unity 的 StartCoroutine 中,
	/// 并将结果传递给 Action<bool> 回调.
	/// </summary>
	private IEnumerator RunAsync(Task<bool> task, Action<bool> callback) {
		// 等待 Task 完成
		yield return new WaitWhile(() => !task.IsCompleted);

		// 强制等待一帧,确保 Task 内部的上下文完全释放
		yield return null;

		if (task.IsFaulted) {
			MPMain.LogError(Localization.Get(
				"MPSteamworks", "AsyncTaskFailed", task.Exception.InnerException.Message));
			callback?.Invoke(false);
		} else {
			// Task.Result 即为异步方法的返回值 (bool)
			callback?.Invoke(task.Result);
		}
	}
	#endregion
}