using Newtonsoft.Json;
using Steamworks;
using Steamworks.Data;
using System;
using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.Util;

namespace WKMPMod.NetWork;

// 只做连接,不做业务逻辑
public class MPSteamworks : MonoSingleton<MPSteamworks>, ISocketManager {
	/// <summary>
	/// 网络消息结构
	/// </summary>
	private struct NetworkMessage {
		public SteamId SenderId;
		public byte[] Data;
		public int Length;
		public DateTime ReceiveTime;
	}

	#region[字段和属性 - 大厅信息]

	// 大厅Id
	public Lobby _currentLobby;

	// 获取大厅相关信息
	public ulong LobbyId { get => _currentLobby.Id.Value; }
	public string LobbyName { get { return _currentLobby.GetData(MPKeys.LOBBY_NAME); } }
	public int LobbySize { get { return _currentLobby.MaxMembers; } }
	public Dictionary<string, string> LobbyData { get; private set; }


	// 本地缓存已存在的 Key, 避免每次 SetMemberData 都去读取和解析索引字符串
	private readonly HashSet<string> _knownKeysCache = new HashSet<string>();

	// 获取全部在线玩家
	public IEnumerable<Friend> Members { get => _currentLobby.Members; }

	// 检查是否在大厅中
	public bool IsInLobby {
		get { return _currentLobby.Id.IsValid; }
	}

	#endregion

	#region[字段和属性 - 玩家信息]

	// 本机Id
	public static ulong UserSteamId { get => SteamClient.SteamId; }
	// 主机Id													
	public ulong HostSteamId { get; private set; }

	// 检查是否是大厅所有者
	public static bool IsHost {
		get {
			if (Instance._currentLobby.Id == 0) return false;
			return Instance._currentLobby.Owner.Id == UserSteamId;
		}
	}

	#endregion

	#region[字段和属性 - steamConnection]

	// 监听socket
	internal SocketManager _socketManager;
	// 出站连接池
	internal Dictionary<SteamId, SteamConnectionManager> _outgoingConnections = new();
	// 已经建立成功的连接池
	internal Dictionary<SteamId, Connection> _allConnections = new();
	// 是否有链接
	public bool HasConnections { get; private set; }

	// 消息队列
	private ConcurrentQueue<NetworkMessage> _messageQueue = new();
	// 数据池
	private static readonly ArrayPool<byte> _messagePool = ArrayPool<byte>.Shared;

	#endregion

	#region[字段和属性 - 扫描协程和时间轴对齐]

	// 重连清理等待
	private const float CONN_CLEANUP_RECONNECT = 1.5f;
	// 首次连接等待
	private const float CONN_CLEANUP_FIRST = 0.2f;
	// 最大尝试次数
	private const int MAX_ATTEMPTS = 3;
	// 每次重试最大等待 2 秒
	private const float RETRY_INTERVAL = 2.0f;
	// 接收方等待发起方连接的最大时间 (3次 * 2秒) + 0.5秒 = 6.5秒
	private const float RECEIVER_WAIT_INITIATOR = (MAX_ATTEMPTS * RETRY_INTERVAL);

	// 接收方最坏情况:清理时间 (1.5秒) + 双方发起方结束时间 (6.5秒 * 2次) = 14.5秒
	private const float RECEIVER_WCS = CONN_CLEANUP_RECONNECT + RECEIVER_WAIT_INITIATOR * 2;

	// 扫描间隔必须大于最长链路(RECEIVER_WCS) 外加一个安全缓冲 14.5秒 + 3.5秒 = 18秒
	// 这样可以确保在上一次对端超时放弃前, 绝对不会触发下一轮扫描
	private static readonly float SCAN_INTERVAL = RECEIVER_WCS + 3.5f;

	// 连接协程
	private Dictionary<SteamId, Coroutine> _connectionCoroutines = new();
	// 扫描协程
	private Coroutine _scanCoroutines = null;
	// 在协程外部或类初始化时缓存, 避免在循环中重复 new 产生 GC
	public readonly WaitForSecondsRealtime Wait200ms = new WaitForSecondsRealtime(0.2f);
	public readonly WaitForSecondsRealtime Wait1000ms = new WaitForSecondsRealtime(1.0f);
	public readonly WaitForSecondsRealtime Wait500ms = new WaitForSecondsRealtime(0.5f);

	#endregion

	#region[字段和属性 - 大厅检索]

	// 判断玩家是否在大厅
	public bool IsMemberInLobby(SteamId targetId) {
		foreach (var member in _currentLobby.Members) {
			if (member.Id == targetId) return true;
		}
		return false;
	}

	// 全部大厅
	public List<Lobby> LastFetchedLobbies { get; private set; } = new List<Lobby>();    // 上次查询大厅列表
	private TickTimer _autoRefreshTimer = new TickTimer(30f,true);// 计时器
	private float _lastRealFetchTime = -999f;           // 上次真正发起网络请求的时间	
	private const float CACHE_PROTECTION_TIME = 5f;     // 5秒刷新冷却
	private Task<List<Lobby>> _currentRefreshTask;       // 当前正在执行的刷新任务
	private readonly object _taskLock = new object();    // 锁

	#endregion

	#region[Unity组件生命周期函数]

	protected override void Awake() {
		base.Awake();
		try {
			if (!SteamClient.IsValid) {

				MPMain.LogError(Localization.Get("MPSteamworks.SteamworksInitFailed"));
				return;
			}

			MPMain.LogInfo(Localization.Get("MPSteamworks.SteamworksInitSuccess", SteamClient.Name, SteamClient.SteamId.ToString()));
		} catch (Exception ex) {
			MPMain.LogError(Localization.Get("MPSteamworks.SteamworksInitException", ex.Message));
		}
	}

	void Start() {

		// 订阅大厅事件 大部分只做转发
		// 本机加入大厅
		SteamMatchmaking.OnLobbyEntered += HandleLobbyEntered;
		// 该用户已经加入或正在加入大厅
		SteamMatchmaking.OnLobbyMemberJoined += HandleLobbyMemberJoined;
		// 该用户已离开或即将离开大厅
		SteamMatchmaking.OnLobbyMemberLeave += HandleLobbyMemberLeave;
		// 该用户在未离开大厅的情况下断线
		SteamMatchmaking.OnLobbyMemberDisconnected += HandleLobbyMemberDisconnected;
		// 当大厅成员数据发生变更
		SteamMatchmaking.OnLobbyMemberDataChanged += HandleLobbyMemberDataChanged;
		// 该用户收到Steam好友的大厅邀请
		SteamMatchmaking.OnLobbyInvite += HandleLobbyInvite;
		// 该用户接受Steam好友的大厅邀请
		SteamFriends.OnGameLobbyJoinRequested += HandleGameLobbyJoinRequested;
		// 大厅创建后(无论是否成功)的消息
		SteamMatchmaking.OnLobbyCreated += HandleLobbyCreated;
		// 大厅数据改变 (包括但不限于主机变更)
		SteamMatchmaking.OnLobbyDataChanged += HandleLobbyDataChanged;

		// 初始化中继网络(必须调用)
		SteamNetworkingUtils.InitRelayNetworkAccess();

		//没几把用, 控制了棍母
		DebugTest();
	}

	void Update() {
		// Steamwork每帧调用,事件激活来源
		Steamworks.SteamClient.RunCallbacks();

		// 读取数据
		_socketManager?.Receive(32);

		// 读取数据
		foreach (var kvp in _outgoingConnections) {
			kvp.Value.Receive(32);
		}

		// 处理消息队列
		ProcessMessageQueue();

		// 刷新检查
		if (_autoRefreshTimer.TryTick()) {
			_ = RefreshLobbyListAsync();
		}

		// 在大厅 && 扫描启动协程为空 && 到达间隔
		if (IsInLobby && _scanCoroutines == null) {
			_scanCoroutines = StartCoroutine(ScanLoop());
		}
	}

	protected override void OnDestroy() {
		// 订阅大厅事件 大部分只做转发
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
		// 该用户收到Steam好友的大厅邀请
		SteamMatchmaking.OnLobbyInvite -= HandleLobbyInvite;
		// 该用户接受Steam好友的大厅邀请
		SteamFriends.OnGameLobbyJoinRequested -= HandleGameLobbyJoinRequested;
		// 大厅创建后(无论是否成功)的消息
		SteamMatchmaking.OnLobbyCreated -= HandleLobbyCreated;
		// 大厅数据改变
		SteamMatchmaking.OnLobbyDataChanged -= HandleLobbyDataChanged;

		DisconnectAll();

		base.OnDestroy();
	}

	#endregion

	#region[RAII函数]

	/// <summary>
	/// 断开所有连接(清理网络资源)
	/// </summary>
	public void DisconnectAll() {
		// 停止扫描协程
		if (_scanCoroutines != null) {
			StopCoroutine(_scanCoroutines);
			_scanCoroutines = null;
		}

		// 关闭连接协程
		foreach (var coroutine in _connectionCoroutines.Values) {
			if (coroutine != null) {
				StopCoroutine(coroutine);
			}
		}
		_connectionCoroutines.Clear();

		// 断开所有出站连接
		foreach (var connectionManager in _outgoingConnections.Values) {
			connectionManager?.Close();
		}
		_outgoingConnections.Clear();

		// 关闭监听Socket
		_socketManager?.Close();
		_socketManager = null;

		// 清理所有连接记录
		_allConnections.Clear();

		// 状态重置
		HasConnections = false;
		HostSteamId = 0;
		LobbyData = null;

		// 清空本地缓存
		_knownKeysCache.Clear();

		// 离开大厅(如果有)
		if (_currentLobby.Id.IsValid) {
			try {
				_currentLobby.Leave();
			} catch { }
			_currentLobby = default;
		}

		// 清空消息队列
		while (_messageQueue.TryDequeue(out _)) { }

		MPMain.LogInfo(Localization.Get("MPSteamworks.AllConnectionsDisconnected"));
	}

	#endregion

	#region[发送数据函数]

	/// <summary>
	/// 专门为 DataWriter 准备的重载,实现零拷贝转发
	/// </summary>
	public void SendToHost(DataWriter writer, SendType sendType = SendType.Reliable, ushort laneIndex = 0) {
		// 从 Writer 获取视图(ArraySegment 是结构体,包装它是轻量级的)
		var segment = writer.Data;
		SendToHost(segment.Array, segment.Offset, segment.Count, sendType, laneIndex);
	}

	/// <summary>
	/// 发送数据: 本机->总线->主机玩家
	/// </summary>
	public void SendToHost(byte[] data, int offset, int length,
						   SendType sendType = SendType.Reliable, ushort laneIndex = 0) {

		if (_currentLobby.Id.IsValid) {
			var hostSteamId = _currentLobby.Owner.Id;
			if (hostSteamId != SteamClient.SteamId && _allConnections.TryGetValue(hostSteamId, out var connection)) {
				connection.SendMessage(data, offset, length, sendType, laneIndex);
			}
		}
	}

	/// <summary>
	/// 专门为 DataWriter 准备的重载,实现零拷贝转发
	/// </summary>
	public void Broadcast(DataWriter writer, SendType sendType = SendType.Reliable, ushort laneIndex = 0) {
		// 从 Writer 获取视图(ArraySegment 是结构体,包装它是轻量级的)
		var segment = writer.Data;
		Broadcast(segment.Array, segment.Offset, segment.Count, sendType, laneIndex);
	}

	/// <summary>
	/// 发送数据: 本机->总线->所有连接玩家
	/// </summary>
	public void Broadcast(byte[] data, int offset, int length,
						  SendType sendType = SendType.Reliable, ushort laneIndex = 0) {
		foreach (var (steamId, connection) in _allConnections) {
			try {
				connection.SendMessage(data, offset, length, sendType, laneIndex);
			} catch (Exception ex) {
				MPMain.LogError(Localization.Get("MPSteamworks.BroadcastingException", ex.Message, Environment.StackTrace));
			}
		}
	}

	/// <summary>
	/// 专门为 DataWriter 准备的重载,实现零拷贝转发
	/// </summary>
	public void SendToPeer(SteamId steamId, DataWriter writer,
						   SendType sendType = SendType.Reliable, ushort laneIndex = 0) {
		// 从 Writer 获取视图(ArraySegment 是结构体,包装它是轻量级的)
		var segment = writer.Data;
		SendToPeer(steamId, segment.Array, segment.Offset, segment.Count, sendType, laneIndex);
	}

	/// <summary>
	/// 发送数据: 本机->特定玩家
	/// </summary>
	public void SendToPeer(SteamId steamId, byte[] data, int offset, int length,
						   SendType sendType = SendType.Reliable, ushort laneIndex = 0) {

		try {
			_allConnections[steamId].SendMessage(data, offset, length, sendType, laneIndex);
		} catch (Exception ex) {
			MPMain.LogError(Localization.Get(
				"MPSteamworks.UnicastException", ex.Message, steamId.ToString(),
				Environment.StackTrace));
		}
	}

	/// <summary>
	/// 发送数据: 本机->除个别玩家外所有连接玩家
	/// </summary>
	/// <param name="steamId">被排除的玩家</param>
	internal void BroadcastExcept(ulong steamId, byte[] data, int offset, int length,
								  SendType sendType = SendType.Reliable, ushort laneIndex = 0) {

		foreach (var (tempSteamId, connection) in _allConnections) {
			if (steamId == tempSteamId)
				continue;
			try {
				connection.SendMessage(data, offset, length, sendType, laneIndex);
			} catch (Exception ex) {
				MPMain.LogError(Localization.Get("MPSteamworks.BroadcastingException", ex.Message, Environment.StackTrace));
			}
		}
	}

	#endregion

	#region[消息处理函数]
	/// <summary>
	/// 接收数据: 任意玩家->消息队列
	/// </summary>
	private void HandleIncomingRawData(SteamId senderId, IntPtr data, int size) {
		// 从池里借出一块内存. 注意:buffer.Length 可能 >= size
		byte[] buffer = _messagePool.Rent(size);

		// 将非托管指针数据拷贝到借来的数组中
		System.Runtime.InteropServices.Marshal.Copy(data, buffer, 0, size);

		// 入队
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
				MPMain.LogError(Localization.Get("MPSteamworks.MessageQueueException", ex.Message));

			} finally {
				// 数据归还缓冲区
				_messagePool.Return(message.Data);
			}
		}
	}
	#endregion

	#region[连接/断连 回调函数]

	/// <summary>
	/// 接收数据: 任意玩家->本机 / 本机->任意玩家 连接成功 -> Player(In/Out)Connected总线
	/// </summary>
	public void OnPlayerConnected(SteamId steamId, Connection connection, bool isIncoming) {
		MPEventBusNet.NotifyPlayerConnected(steamId);
	}

	/// <summary>
	/// 接收数据: 玩家断开连接 -> PlayerDisconnected总线
	/// </summary>
	public void OnPlayerDisconnected(SteamId steamId) {
		if (_allConnections.ContainsKey(steamId)) {

			_allConnections.Remove(steamId);
			_outgoingConnections.Remove(steamId);
			StopCoroutine(_connectionCoroutines[steamId]);
			_connectionCoroutines.Remove(steamId);

			// 重连检测
			if (IsInLobby && IsMemberInLobby(steamId) && !_connectionCoroutines.ContainsKey(steamId))
				_connectionCoroutines[steamId] = StartCoroutine(ConnectionController(steamId, true));

			MPMain.LogInfo(Localization.Get("MPSteamworks.PlayerDisconnectedCleaned", steamId.ToString()));

			// 检查是否还有剩余连接
			HasConnections = _allConnections.Count > 0;

			// 没有重连成功 触发业务层销毁玩家
			if (!_allConnections.ContainsKey(steamId))
				MPEventBusNet.NotifyPlayerDisconnected(steamId);
		}
	}

	#endregion

	#region[连接器管理函数]

	/// <summary>
	/// 创建监听socket
	/// </summary>
	public void CreateListeningSocket() {
		try {
			_socketManager = SteamNetworkingSockets.CreateRelaySocket<SocketManager>(1);
			_socketManager.Interface = this;
		} catch (Exception socketEx) {
			MPMain.LogError(Localization.Get("MPSteamworks.SocketCreateException", socketEx.Message));
		}
	}

	/// <summary>
	/// 连接到指定玩家(纯网络连接,不处理业务逻辑)
	/// </summary>
	public void ConnectToPlayer(SteamId steamId) {
		try {
			if (_outgoingConnections.ContainsKey(steamId)) {
				MPMain.LogWarning(Localization.Get("MPSteamworks.AlreadyConnected", steamId.ToString()));
				return;
			}

			var connectionManager = SteamNetworkingSockets.ConnectRelay<SteamConnectionManager>(steamId, 1);
			connectionManager.Interface = connectionManager;
			connectionManager.Instance = this;
			MPMain.LogInfo(Localization.Get("MPSteamworks.ConnectingToPlayer", steamId.ToString()));
		} catch (Exception ex) {
			MPMain.LogError(Localization.Get("MPSteamworks.ConnectToPlayerException", ex.Message));
		}
	}

	/// <summary>
	/// 连接到指定玩家 - 异步版本
	/// </summary>
	public async Task<bool> ConnectToPlayerAsync(SteamId steamId) {
		SteamConnectionManager connectionManager = null;
		float timeout = 5f;
		float startTime = Time.unscaledTime;

		// 初始检查
		if (_outgoingConnections.ContainsKey(steamId) || _allConnections.ContainsKey(steamId)) {
			MPMain.LogWarning(Localization.Get("MPSteamworks.AlreadyConnected", steamId.ToString()));
			return true;
		}

		// 1. 同步建立连接
		try {
			MPMain.LogInfo(Localization.Get("MPSteamworks.ConnectingToPlayer", steamId.ToString()));

			// 建立连接
			connectionManager = SteamNetworkingSockets.ConnectRelay<SteamConnectionManager>(steamId, 1);
			connectionManager.Instance = this;
			_outgoingConnections[steamId] = connectionManager;
			_allConnections[steamId] = connectionManager.Connection;

		} catch (Exception ex) {
			MPMain.LogError(Localization.Get("MPSteamworks.ConnectToPlayerException", ex.Message));
			return false;
		}

		// 2. 异步等待连接建立
		if (connectionManager != null) {
			while (connectionManager.ConnectionInfo.State != ConnectionState.Connected) {
				if (Time.unscaledTime - startTime > timeout) {
					MPMain.LogError(Localization.Get("MPSteamworks.ConnectionTimeout", steamId.ToString()));
					_outgoingConnections.Remove(steamId);
					_allConnections.Remove(steamId);
					return false;
				}
				// 替换 yield return null
				await Task.Yield();
			}
		} else {
			return false;
		}

		MPMain.LogInfo(Localization.Get("MPSteamworks.ConnectSuccess", steamId.ToString()));
		return true;
	}

	#endregion

	#region[创建/加入大厅函数]

	/// <summary>
	/// 创建大厅(主机模式)- 异步版本
	/// </summary>
	public async Task<bool> CreateRoomAsync(int maxPlayers = 8, Dictionary<string, string> lobbyData = null) {
		// 清理全部连接
		DisconnectAll();

		try {
			if (!SteamClient.IsValid) {
				MPMain.LogError(Localization.Get("MPSteamworks.SteamClientInvalid"));
				return false;
			}

			MPMain.LogTest("MPSW CreateRoom A");

			// await SteamMatchmaking.CreateLobbyAsync
			Lobby? lobbyResult = await SteamMatchmaking.CreateLobbyAsync(maxPlayers);

			// 只检查结果并返回,移除所有同步大厅设置和 Socket 创建! 
			if (!lobbyResult.HasValue) {
				MPMain.LogError(Localization.Get("MPSteamworks.CreateLobbyFailed"));
				return false;
			}

			MPMain.LogTest("MPSW CreateRoom B");

			_currentLobby = lobbyResult.Value;

			MPMain.LogInfo(Localization.Get("MPSteamworks.LobbyCreatedSuccess", _currentLobby.Id.ToString()));

			// 设置大厅信息
			SetLobbyData(GetDefaultLobbyData());
			SetLobbyData(lobbyData);
			_currentLobby.SetPublic();
			_currentLobby.SetJoinable(true);
			_currentLobby.Owner = new Friend(SteamClient.SteamId);

			MPMain.LogTest("MPSW CreateRoom C");

			// 获取Socket
			CreateListeningSocket();

			MPMain.LogTest("MPSW CreateRoom D");

			// 刷新大厅数据
			RefreshLobbyData();

			MPMain.LogTest("MPSW CreateRoom E");

			return true; // 成功
		} catch (Exception ex) {
			MPMain.LogError(Localization.Get("MPSteamworks.CreateLobbyException", ex.Message));
			return false; // 失败
		}
	}

	/// <summary>
	/// 加入大厅(客户端模式)- 异步版本
	/// </summary>
	public async Task<bool> JoinRoomAsync(Lobby lobby) {
		// 清理全部连接
		DisconnectAll();

		try {
			MPMain.LogTest("MPSW JoinRoom A");

			// 核心改变:直接 await 任务
			RoomEnter result = await lobby.Join();

			MPMain.LogTest("MPSW JoinRoom B");

			// 检查 RoomEnter 结果
			if (result != RoomEnter.Success) {
				throw new Exception($"Failed to join Steam lobby: {result.ToString()}");
			}

			_currentLobby = lobby;
			string roomName = LobbyName
				?? Localization.Get("MPSteamworks.NullLobbyName");
			MPMain.LogInfo(Localization.Get("MPSteamworks.JoinLobbySuccess", roomName));

			MPMain.LogTest("MPSW JoinRoom C");

			// 获取Socket
			CreateListeningSocket();

			MPMain.LogTest("MPSW JoinRoom D");

			// 刷新大厅数据
			RefreshLobbyData();

			MPMain.LogTest("MPSW JoinRoom E");

			return true;

		} catch (Exception ex) {
			MPMain.LogError(Localization.Get("MPSteamworks.JoinLobbyException", ex.Message));
			return false;
		}
	}

	#endregion

	#region[Steam事件处理函数]
	/// <summary>
	/// 接收数据: 进入到大厅<br/>
	/// LobbyEntered总线订阅者: <see cref="MPCore.HandleLobbyEntered"/><br/>
	/// </summary>
	private void HandleLobbyEntered(Lobby lobby) {
		_currentLobby = lobby;
		HostSteamId = lobby.Owner.Id;
		MPMain.LogInfo(Localization.Get("MPSteamworks.EnteredLobby", lobby.Id.ToString()));

		// 发布事件到总线
		MPEventBusNet.NotifyLobbyEntered(lobby);
	}

	/// <summary>
	/// 接收数据: 大厅有成员加入->LobbyMemberJoined总线->连接新玩家
	/// </summary>
	private void HandleLobbyMemberJoined(Lobby lobby, Friend friend) {
		if (lobby.Id == _currentLobby.Id) {
			_currentLobby = lobby;
			MPMain.LogInfo(Localization.Get("MPSteamworks.PlayerJoinedRoom", friend.Name));

			// 发布事件到总线
			MPEventBusNet.NotifyLobbyMemberJoined(friend);
		}
	}

	/// <summary>
	/// 接收数据: 大厅有成员离开->LobbyMemberLeft总线
	/// </summary>
	private void HandleLobbyMemberLeave(Lobby lobby, Friend friend) {
		if (lobby.Id == _currentLobby.Id) {
			_currentLobby = lobby;
			MPMain.LogInfo(Localization.Get("MPSteamworks.PlayerLeftRoom", friend.Name));

			// 发布事件到总线
			MPEventBusNet.NotifyLobbyMemberLeave(friend);

			// 只在这里处理连接清理
			OnPlayerDisconnected(friend.Id);
		}
	}

	/// <summary>
	/// 接收数据: 大厅有成员断开连接-> 总线
	/// </summary>
	private void HandleLobbyMemberDisconnected(Lobby lobby, Friend friend) {
		if (lobby.Id == _currentLobby.Id) {
			MPMain.LogInfo(Localization.Get("MPSteamworks.PlayerDisconnectedFromLobby", friend.Name));

			// 重复分发
			// 发布断开事件到总线
			//MPEventBusNet.NotifyPlayerDisconnected(friend.Id);
		}
	}

	/// <summary>
	/// 接收数据: 玩家数据变更
	/// </summary>
	private void HandleLobbyMemberDataChanged(Lobby lobby, Friend friend) {
		var data = GetAllMemberData(friend);
		MPEventBusNet.NotifyMemberDataChanged(friend, data);
	}

	/// <summary>
	/// 接受数据: 收到Steam好友的大厅邀请 -> LobbyInvite总线
	/// </summary>
	private void HandleLobbyInvite(Friend friend, Lobby lobby) {
		MPEventBusNet.NotifyLobbyInvite(friend, lobby);
	}

	/// <summary>
	/// 接收数据: 玩家接受Steam好友的大厅邀请 -> GameLobbyJoinRequested总线
	/// </summary>
	private void HandleGameLobbyJoinRequested(Lobby lobby, SteamId steamId) {
		MPEventBusNet.NotifyGameLobbyJoinRequested(lobby, steamId);
	}

	/// <summary>
	/// 接收数据: 大厅创建回调
	/// </summary>
	public void HandleLobbyCreated(Result result, Lobby lobby) {
		if (result == Result.OK) return;
		// 针对特定错误给玩家提示
		// 针对常见的大厅创建错误进行分类处理
		switch (result) {
			case Result.LimitedUserAccount:
				// 账号受限 无法使用社交功能
				MPMain.LogWarning(Localization.Get("MPSteamworks.LimitedUserAccount"));
				break;
			case Result.NoConnection:
				// 没有连接到 Steam 网络
				MPMain.LogWarning(Localization.Get("MPSteamworks.NoConnection"));
				break;
			case Result.Timeout:
				// 请求超时
				MPMain.LogWarning(Localization.Get("MPSteamworks.Timeout"));
				break;
			case Result.InvalidParam:
				// 参数错误
				MPMain.LogWarning(Localization.Get("MPSteamworks.InvalidParam"));
				break;
			case Result.RateLimitExceeded:
				// 操作过于频繁
				MPMain.LogWarning(Localization.Get("MPSteamworks.RateLimitExceeded"));
				break;
			case Result.LimitExceeded:
				// 达到上限
				MPMain.LogWarning(Localization.Get("MPSteamworks.LimitExceeded"));
				break;
			case Result.AccessDenied:
				// 权限不足
				MPMain.LogWarning(Localization.Get("MPSteamworks.AccessDenied"));
				break;

			// 默认处理其他不常见错误
			default:
				// 默认错误 带参数
				MPMain.LogWarning(Localization.Get("MPSteamworks.CreateLobbyFailedDefault", result.ToString(), (int)result));
				break;
		}
	}

	/// <summary>
	/// 接收数据: 大厅数据更新<br/>
	/// LobbyDataChanged总线订阅者: <see cref="MPCore.HandleLobbyDataChanged"/>
	/// </summary>
	public void HandleLobbyDataChanged(Lobby lobby) {
		// 获取当前大厅真正的主机(Owner)
		SteamId currentOwnerId = lobby.Owner.Id;
		// 检查所有权是否发生了变更
		if (HostSteamId != 0 && HostSteamId != currentOwnerId) {
			MPMain.LogInfo(Localization.Get("MPSteamworks.HostChanged", HostSteamId.ToString(), currentOwnerId.ToString()));

			// 触发主机变更总线
			MPEventBusNet.NotifyLobbyHostChanged(new Friend(HostSteamId), HostSteamId == UserSteamId);

			HostSteamId = currentOwnerId;

			// 如果当前玩家是新主机 更改大厅所有者数据
			if (currentOwnerId == UserSteamId) {
				_currentLobby.SetData(MPKeys.OWNER_NAME, UserSteamId.ToString());
				var lobbyName = _currentLobby.GetData(MPKeys.LOBBY_NAME);
				if (string.IsNullOrWhiteSpace(lobbyName) || lobbyName.EndsWith("'s game")) {
					_currentLobby.SetData(MPKeys.LOBBY_NAME, $"{SteamClient.Name}'s game");
				}
			}
		}

		MPMain.LogInfo(Localization.Get("MPSteamworks.LobbyDataChanged"));
		var tempData = lobby.Data.ToDictionary(k => k.Key, v => v.Value);
		var delta = new Dictionary<string, string>();

		foreach (var kv in tempData) {
			// 如果旧数据里没有这个 Key, 或者 Value 变了
			if (!LobbyData.TryGetValue(kv.Key, out string oldVal) || oldVal != kv.Value) {
				delta[kv.Key] = kv.Value;
			}
		}
		LobbyData = tempData;

		if (delta.Count > 0) {
			MPEventBusNet.NotifyLobbyDataChanged(delta);
		}
	}

	#endregion

	#region[重连机制]

	public IEnumerator ScanLoop() {
		while (IsInLobby) {
			// 对齐到 Unix 时间整倍数网格
			double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
			yield return new WaitForSecondsRealtime((float)(SCAN_INTERVAL - (now % SCAN_INTERVAL) % SCAN_INTERVAL));

			// 清理已经成功建立连接的协程记录
			foreach (var (steamId,_) in _connectionCoroutines.ToList()) {
				if (_allConnections.ContainsKey(steamId))
					_connectionCoroutines.Remove(steamId);
			}

			// 在大厅但没有连接
			foreach (var member in Members) {
				if (_connectionCoroutines.Count >= 8) break;
				if (member.Id == UserSteamId) continue; // 跳过自己

				// 只有当没有连接, 且没有正在进行的连接协程时, 才发起连接
				if (!_allConnections.ContainsKey(member.Id) && !_connectionCoroutines.ContainsKey(member.Id)) {
					_connectionCoroutines[member.Id] = StartCoroutine(ConnectionController(member.Id, true));
				}
			}
		}
		_scanCoroutines = null;
	}

	/// <summary>
	/// 通用的连接控制器:支持初始连接和断线重连
	/// </summary>
	public IEnumerator ConnectionController(SteamId targetId, bool isReconnect) {
		// 如果是重连 等待1.5秒进行连接清理
		yield return new WaitForSecondsRealtime(isReconnect ? CONN_CLEANUP_RECONNECT : CONN_CLEANUP_FIRST);
		// 目标不在大厅或自己不在大厅 时退出连接流程
		if (!IsInLobby || !IsMemberInLobby(targetId)) {
			_connectionCoroutines.Remove(targetId);
			yield break;
		}

		// 核心重用逻辑:尝试并验证连接
		IEnumerator AttemptAndVerify(int maxAttempts, float retryInterval) {
			for (int i = 0; i < maxAttempts; i++) {
				// 检查现有连接(可能在循环开始前已连上)
				if (_allConnections.ContainsKey(targetId)) {
					// 等待 1秒确定连接正常
					yield return Wait1000ms;
					// 连接正常 退出协程
					if (_allConnections.ContainsKey(targetId)) {
						HasConnections = true;
						MPEventBusNet.NotifyPlayerConnected(targetId);
						yield break;
					}
				}
				// 清理连接并重连玩家
				ExecuteConnection(targetId);
				// 等待重试间隔,期间持续检查状态
				float endTime = Time.unscaledTime + retryInterval;
				// 重试间隔
				while (Time.unscaledTime < endTime) { 
					if (_allConnections.ContainsKey(targetId)) {
						// 等待 1秒确定连接正常
						yield return Wait1000ms;
						// 连接正常 退出协程
						if (_allConnections.ContainsKey(targetId)) {
							HasConnections = true;
							MPEventBusNet.NotifyPlayerConnected(targetId);
							yield break;
						}
					}
					yield return Wait200ms;
				}
				MPMain.LogWarning(Localization.Get("MPSteamworks.ConnectionAttemptFailed", i + 1, targetId));
			}
		}

		// ID小 则先尝试主动连接
		bool isInitiator = UserSteamId < targetId;

		if (isInitiator) {
			// 主动连接
			MPMain.LogInfo(Localization.Get("MPSteamworks.ConnectionInitiator", targetId));
			// 使用配置常量
			yield return AttemptAndVerify(MAX_ATTEMPTS, RETRY_INTERVAL);
		} else {
			MPMain.LogInfo(Localization.Get("MPSteamworks.ConnectionEeceiver", targetId));
			bool alreadyConnected = false;
			// 等待对方的主动连接
			float endTime = Time.unscaledTime + RECEIVER_WAIT_INITIATOR;
			while (Time.unscaledTime < endTime) {
				if (_allConnections.ContainsKey(targetId)) {
					// 等待 1秒确定连接正常
					yield return Wait1000ms;
					if (_allConnections.ContainsKey(targetId)) {
						HasConnections = true;
						MPEventBusNet.NotifyPlayerConnected(targetId);
						alreadyConnected = true;
						break;
					}
				}
				yield return Wait200ms;
			}

			// 未能被动监听连接 开始主动连接
			if (!alreadyConnected) {
				MPMain.LogWarning(Localization.Get("MPSteamworks.ReverseConnectionAttempt"));
				yield return AttemptAndVerify(MAX_ATTEMPTS, RETRY_INTERVAL);
			}
		}

		_connectionCoroutines.Remove(targetId);

		if (!_allConnections.ContainsKey(targetId) && IsInLobby && IsMemberInLobby(targetId))
			MPMain.LogError(Localization.Get("MPSteamworks.ConnectionProcessFailed"));
		yield break;
	}

	// 清理连接并重连玩家
	private void ExecuteConnection(SteamId targetId) {
		// 先物理清理, 防止 Already connected 错误
		if (_outgoingConnections.TryGetValue(targetId, out var oldMgr)) {
			oldMgr.Connection.Close();
			_outgoingConnections.Remove(targetId);
		}
		_allConnections.Remove(targetId);

		ConnectToPlayer(targetId);
	}

	#endregion

	#region[SocketManager接口实现]

	// 有玩家正在接入
	void ISocketManager.OnConnecting(Connection connection, ConnectionInfo info) {
		MPMain.LogInfo(Localization.Get("MPSteamworks.PlayerConnecting", info.Identity.SteamId.ToString()));
		connection.Accept();
	}

	// 有玩家已经接入
	void ISocketManager.OnConnected(Connection connection, ConnectionInfo info) {
		var steamId = info.Identity.SteamId;
		MPMain.LogInfo(Localization.Get("MPSteamworks.PlayerConnected", steamId.ToString(), connection.Id, info.State));
		_allConnections[steamId] = connection;
		StopCoroutine(_connectionCoroutines[steamId]);
		_connectionCoroutines.Remove(steamId);
		HasConnections = true;
		MPEventBusNet.NotifyPlayerConnected(steamId);
	}

	// 接收消息
	void ISocketManager.OnMessage(Connection connection, NetIdentity identity,
								   IntPtr data, int size, long messageNum,
								   long recvTime, int channel) {
		HandleIncomingRawData(identity.SteamId, data, size);
	}

	// 连接被本地或远程关闭
	void ISocketManager.OnDisconnected(Connection connection, ConnectionInfo info) {
		MPMain.LogError(Localization.Get("MPSteamworks.DisconnectedDetails", info.ToString()));
		OnPlayerDisconnected(info.Identity.SteamId);
	}

	#endregion

	#region[Steam 连接Connection管理器]
	internal class SteamConnectionManager : ConnectionManager, IConnectionManager {

		private MPSteamworks _instance;
		public MPSteamworks Instance { get => _instance; set => _instance = value; }

		// 正在去连接
		public override void OnConnecting(ConnectionInfo info) { }

		// 连接已建立
		public override void OnConnected(ConnectionInfo info) {
			SteamId steamId = info.Identity.SteamId;
			MPMain.LogInfo(Localization.Get("MPSteamworks.AlreadyActiveConnected", steamId.ToString(), info.State));
			_instance._outgoingConnections[steamId] = this;
			_instance._allConnections[steamId] = this.Connection;
			Instance.StopCoroutine(Instance._connectionCoroutines[steamId]);
			Instance._connectionCoroutines.Remove(steamId);
			Instance.HasConnections = true;
			MPEventBusNet.NotifyPlayerConnected(steamId);
		}

		// 接收消息
		public override void OnMessage(IntPtr data, int size, long messageNum, long recvTime, int channel) {
			Instance?.HandleIncomingRawData(this.ConnectionInfo.Identity.SteamId, data, size);
		}

		// 连接被本地或远程关闭
		public override void OnDisconnected(ConnectionInfo info) {
			Instance?.OnPlayerDisconnected(info.Identity.SteamId);
		}
	}
	#endregion

	#region[工具/API函数]

	/// <summary>
	/// 设置大厅默认数据,确保所有玩家都能识别这是同一款游戏的大厅,并且可以进行版本兼容性检查
	/// </summary>

	private Dictionary<string, string> GetDefaultLobbyData() {
		var dict = new Dictionary<string, string>();
		dict[MPKeys.GAME_KEY] = MPKeys.GAME_VALUE;
		dict[MPKeys.VERSION] = Application.version;
		dict[MPKeys.MOD_VERSION] = MPMain.PLUGIN_VERSION;
		dict[MPKeys.OWNER_NAME] = UserSteamId.ToString();
		dict[MPKeys.LOBBY_VISIBILITY] = "public";
		dict[MPKeys.ALLOW_CHEATS] = MPConfig.AllowCheats.ToString();
		// 伤害倍率
		dict[MPKeys.DAMAGE_CONFIG] = JsonConvert.SerializeObject(MPCore.damageRules.FloatFieldValues);
		// 队伍规则配置
		foreach (var kvp in RuleConfigLoader.LoadRulesAsLobbyData()) {
			dict[kvp.Key] = kvp.Value;  // 重复键会覆盖
			var teamNames = kvp.Key.Split('_');
			TeamRuleManager.activeTeams.Add(teamNames[1]);
			TeamRuleManager.activeTeams.Add(teamNames[2]);
		}
		// 活动队伍列表
		dict[MPKeys.ACTIVE_TEAMS] = string.Join(",", TeamRuleManager.activeTeams);
		return dict;
	}

	/// <summary>
	/// 设置大厅数据的公共接口,允许业务层在创建大厅时传入自定义数据
	/// </summary>
	public void SetLobbyData(string key, string value) {
		if (_currentLobby.Id.IsValid) {
			try {
				_currentLobby.SetData(key, value);
			} catch (Exception ex) {
				MPMain.LogError(Localization.Get("MPSteamworks.SetLobbyDataException", ex.Message));
			}
		}
	}

	/// <summary>
	/// 设置大厅数据的公共接口,允许业务层在创建大厅时传入自定义数据
	/// </summary>
	public void SetLobbyData(Dictionary<string, string> lobbyData) {
		if (_currentLobby.Id.IsValid) {
			try {
				foreach (var (key, value) in lobbyData) {
					_currentLobby.SetData(key, value);
				}
			} catch (Exception ex) {
				MPMain.LogError(Localization.Get("MPSteamworks.SetLobbyDataException", ex.Message));
			}
		}
	}

	/// <summary>
	/// 设置个人数据, 并同步更新索引 Key
	/// </summary>
	public void SetMemberData(string key, string value) {
		if (!_currentLobby.Id.IsValid) return;
		if (key == MPKeys.ALL_KEYS_INDEX) return; // 禁止手动修改索引 Key

		try {
			// 设置实际数据
			_currentLobby.SetMemberData(key, value);

			// 检查并更新索引
			if (_knownKeysCache.Add(key)) {
				string newKeys = string.Join(",", _knownKeysCache);
				_currentLobby.SetMemberData(MPKeys.ALL_KEYS_INDEX, newKeys);
			}
		} catch (Exception ex) {
			MPMain.LogError(Localization.Get("MPSteamworks.SetMemberDataFailed", ex.Message));
		}
	}

	/// <summary>
	/// 批量设置个人数据
	/// </summary>
	public void SetMemberData(Dictionary<string, string> memberData) {
		if (!_currentLobby.Id.IsValid || memberData == null) return;

		bool indexChanged = false;

		foreach (var kvp in memberData) {
			if (kvp.Key == MPKeys.ALL_KEYS_INDEX) continue;

			_currentLobby.SetMemberData(kvp.Key, kvp.Value);

			// 只要有新 Key 成功加入 Cache, 标记变更
			if (_knownKeysCache.Add(kvp.Key)) {
				indexChanged = true;
			}
		}

		if (indexChanged) {
			string newKeys = string.Join(",", _knownKeysCache);
			_currentLobby.SetMemberData(MPKeys.ALL_KEYS_INDEX, newKeys);
		}
	}

	/// <summary>
	/// 通过索引 Key 获取指定玩家的所有个人数据
	/// </summary>
	public Dictionary<string, string> GetAllMemberData(Friend friend) {
		// 直接返回本地数据
		//if (friend.Id == UserSteamId) return new Dictionary<string, string>(MemberData);

		var result = new Dictionary<string, string>();
		if (!_currentLobby.Id.IsValid) return result;

		try {
			// 先拿到索引字符串
			string allKeysRaw = _currentLobby.GetMemberData(friend, MPKeys.ALL_KEYS_INDEX);
			if (string.IsNullOrEmpty(allKeysRaw)) return result;

			// 拆分并逐一拉取数据
			string[] keys = allKeysRaw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
			foreach (var key in keys) {
				string val = _currentLobby.GetMemberData(friend, key);
				result[key] = val;
			}
		} catch (Exception ex) {
			MPMain.LogError(Localization.Get("MPSteamworks.GetAllMemberDataException", ex.Message));
		}

		return result;
	}

	/// <summary>
	/// 获取个人数据的公共接口,允许业务层在加入大厅时传入自定义数据
	/// </summary>
	public string GetMemberData(ulong friendId, string key) {
		if (_currentLobby.Id.IsValid) {
			try {
				return _currentLobby.GetMemberData(new Friend(friendId), key);
			} catch (Exception ex) {
				MPMain.LogError(Localization.Get("MPSteamworks.GetMemberDataFailed", ex.Message));
			}
		}
		return null;
	}

	//public void SendAllMemberData() {
	//	var writer = MPWriterPool.GetWriter(UserSteamId, MPProtocol.BroadcastId, PacketType.ResponseMemberData);
	//	writer.Put(MemberData);
	//	Broadcast(writer);
	//}

	/// <summary>
	/// 刷新大厅数据,在加入大厅或创建大厅后调用
	/// LobbyDataChanged总线订阅者: <see cref="MPCore.HandleLobbyDataChanged"/>
	/// </summary>
	public void RefreshLobbyData() {
		if (_currentLobby.Id.IsValid) {
			LobbyData = _currentLobby.Data.ToDictionary(x => x.Key, x => x.Value);
			MPEventBusNet.NotifyLobbyDataChanged(LobbyData);
		}
	}


	/// <summary>
	/// 移交大厅权限
	/// </summary>
	public void SetLobbyHost(IDType playerId) {
		if (!IsHost)
			return;
		var newHost = new Friend(playerId);
		_currentLobby.Owner = newHost;
	}

	#endregion

	#region[大厅检索]

	/// <summary>
	/// 刷新大厅列表
	/// 1. 5秒内重复调用直接返回缓存
	/// 2. 超过5秒则发起/等待异步任务
	/// 3. 成功触发刷新后, 重置30秒自动计时
	/// </summary>
	public Task<List<Lobby>> RefreshLobbyListAsync() {
		// 5秒缓存保护
		if (Time.unscaledTime - _lastRealFetchTime < CACHE_PROTECTION_TIME) {
			MPMain.LogInfo(Localization.Get("MPSteamworks.RateLimitCacheHit"));
			return Task.FromResult(LastFetchedLobbies);
		}

		lock (_taskLock) {
			// 重置30秒计时器(无论是手动还是自动触发, 都重新开始倒计时)
			_autoRefreshTimer.Reset();

			// 如果已经有一个任务在运行, 直接返回该任务
			if (_currentRefreshTask != null && !_currentRefreshTask.IsCompleted) {
				return _currentRefreshTask;
			}

			// 更新真实请求时间, 并开始新任务
			_lastRealFetchTime = Time.unscaledTime;
			_currentRefreshTask = RefreshLobbyList();
			return _currentRefreshTask;
		}
	}

	// 刷新大厅
	public async Task<List<Lobby>> RefreshLobbyList() {
		try {
			var lobbyIds = new HashSet<ulong>();
			var combinedLobbies = new List<Lobby>();
			// 扫描好友大厅
			foreach (var friend in SteamFriends.GetFriends()) {
				if (friend.IsPlayingThisGame && friend.GameInfo?.Lobby is Lobby lobby
					&& lobbyIds.Add(lobby.Id)) {
					// 添加不重复Lobby
					combinedLobbies.Add(lobby);
				}
			}

			// 搜索公开大厅
			var query = new Steamworks.Data.LobbyQuery()
				.FilterDistanceWorldwide()
				.WithKeyValue(MPKeys.GAME_KEY, "White Knuckle") // 确保是同一款游戏的
				.WithMaxResults(50);
			var publicLobbies = await query.RequestAsync();

			// 合并结果
			if (publicLobbies != null) {
				foreach (var lobby in publicLobbies) {
					if (lobbyIds.Add(lobby.Id)) combinedLobbies.Add(lobby);
				}
			}

			LastFetchedLobbies = combinedLobbies;
			return combinedLobbies;
		} catch (Exception ex) {
			MPMain.LogError(Localization.Get("MPSteamworks.LobbySearchException", ex.Message));
			return LastFetchedLobbies;
		}
	}

	#endregion

	#region[富状态控制]

	public void DebugTest() {
		SteamFriends.SetRichPresence("steam_display", "run form RMSS");
		SteamFriends.SetRichPresence("status", "Test text A");
		SteamFriends.SetRichPresence("steam_player_group", _currentLobby.Id.ToString());
		SteamFriends.SetRichPresence("steam_player_group_size", _currentLobby.MemberCount.ToString());
	}

	#endregion
}