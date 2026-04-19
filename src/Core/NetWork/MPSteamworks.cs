using Steamworks;
using Steamworks.Data;
using System;
using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Principal;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Windows;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.Util;
using static WKMPMod.UI.UI_Manager;

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

	// Debug日志输出间隔
	private TickTimer _debugTick = new TickTimer(5f);

	// 大厅Id
	public Lobby _currentLobby;

	// 获取大厅相关信息
	public ulong LobbyId { get => _currentLobby.Id.Value; }
	public string LobbyName { get { return _currentLobby.GetData("name"); } }
	public int LobbySize { get { return _currentLobby.MaxMembers; } }

	// 获取全部在线玩家
	public IEnumerable<Friend> Members { get => _currentLobby.Members; }

	// 检查是否在大厅中
	public bool IsInLobby {
		get { return _currentLobby.Id.IsValid; }
	}

	public ulong UserSteamId { get => SteamClient.SteamId; }    // 本机Id													
	public ulong HostSteamId { get; private set; }  // 之前的主机Id

	// 检查是否是大厅所有者
	public bool IsHost {
		get {
			if (_currentLobby.Id == 0) return false;
			return _currentLobby.Owner.Id == UserSteamId;
		}
	}

	internal SocketManager _socketManager;  // 监听socket
											// 出站连接池
	internal Dictionary<SteamId, SteamConnectionManager> _outgoingConnections = new Dictionary<SteamId, SteamConnectionManager>();
	// 已经建立成功的连接池
	internal Dictionary<SteamId, Connection> _allConnections = new Dictionary<SteamId, Connection>();

	public bool HasConnections { get; private set; }    // 是否有链接

	// 消息队列
	private ConcurrentQueue<NetworkMessage> _messageQueue = new ConcurrentQueue<NetworkMessage>();
	// 数据池
	private static readonly ArrayPool<byte> _messagePool = ArrayPool<byte>.Shared;

	// 判断玩家是否在大厅
	public bool IsMemberInLobby(SteamId targetId) {
		foreach (var member in _currentLobby.Members) {
			if (member.Id == targetId) return true;
		}
		return false;
	}

	// 全部大厅
	public List<Lobby> LastFetchedLobbies { get; private set; } = new List<Lobby>();	// 上次查询大厅列表
	private TickTimer _autoRefreshTimer = new TickTimer(30f);// 计时器
	private float _lastRealFetchTime = -999f;           // 上次真正发起网络请求的时间	
	private const float CACHE_PROTECTION_TIME = 5f;		// 5秒刷新冷却
	private Task<List<Lobby>> _currentRefreshTask;       // 当前正在执行的刷新任务
	private readonly object _taskLock = new object();    // 锁

	#region[Unity组件生命周期函数]
	protected override void Awake() {
		base.Awake();
		//SteamClient.Init(3195790u);
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
		// 当大厅成员数据或大厅所有权发生变更
		SteamMatchmaking.OnLobbyMemberDataChanged += HandleLobbyMemberDataChanged;
		// 该用户收到Steam好友的大厅邀请
		SteamMatchmaking.OnLobbyInvite += HandleLobbyInvite;
		// 该用户接受Steam好友的大厅邀请
		SteamFriends.OnGameLobbyJoinRequested += HandleGameLobbyJoinRequested;

		// 初始化中继网络(必须调用)
		SteamNetworkingUtils.InitRelayNetworkAccess();
	}


	void Update() {
		// Steamwork每帧调用,事件激活来源
		Steamworks.SteamClient.RunCallbacks();

		// 读取数据
		_socketManager?.Receive(32);

		// 读取数据
		foreach (var connectionManager in _outgoingConnections.Values) {
			connectionManager.Receive(32);
		}

		// 处理消息队列
		ProcessMessageQueue();

		// 刷新检查
		if (_autoRefreshTimer.TryTick()) {
			_ = RefreshLobbyListAsync();
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

		DisconnectAll();

		base.OnDestroy();
	}

	#endregion

	#region[RAII函数]

	/// <summary>
	/// 断开所有连接(清理网络资源)
	/// </summary>
	public void DisconnectAll() {
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

		// Debug
		//bool canLog = _debugTick.TryTick();
		//if (canLog) {
		//	MPMain.LogInfo(Localization.GetByPath("MPSteamworks.StartedBroadcasting", _allConnections.Count.ToString()));
		//}

		foreach (var (steamId, connection) in _allConnections) {
			try {

				//if (canLog) {
				//	MPMain.LogInfo(Localization.GetByPath("MPSteamworks.SendingToConnection", steamId.ToString(), connection.Id.ToString()));
				//}

				connection.SendMessage(data, offset, length, sendType, laneIndex);
			} catch (Exception ex) {
				MPMain.LogError(Localization.Get("MPSteamworks.BroadcastingException", ex.Message));
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
			MPMain.LogError(Localization.Get("MPSteamworks.UnicastException", ex.Message, steamId.ToString()));
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
				MPMain.LogError(Localization.Get("MPSteamworks.BroadcastingException", ex.Message));
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

			// 重连检测
			if (IsInLobby || IsMemberInLobby(steamId))
				StartCoroutine(ConnectionController(steamId, true));

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
			_outgoingConnections[steamId] = connectionManager;
			_allConnections[steamId] = connectionManager.Connection;
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
		float startTime = Time.time;

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
				if (Time.time - startTime > timeout) {
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
		//await Task.Yield();

		try {
			if (!SteamClient.IsValid) {
				MPMain.LogError(Localization.Get("MPSteamworks.SteamClientInvalid"));
				return false;
			}

			// await SteamMatchmaking.CreateLobbyAsync
			Lobby? lobbyResult = await SteamMatchmaking.CreateLobbyAsync(maxPlayers);

			// 只检查结果并返回,移除所有同步大厅设置和 Socket 创建！
			if (!lobbyResult.HasValue) {
				MPMain.LogError(Localization.Get("MPSteamworks.CreateLobbyFailed"));
				return false;
			}

			_currentLobby = lobbyResult.Value;

			MPMain.LogInfo(Localization.Get("MPSteamworks.LobbyCreatedSuccess", _currentLobby.Id.ToString()));

			// 设置大厅信息
			SetLobbyData(GetDefaultLobbyData());
			SetLobbyData(lobbyData);
			_currentLobby.SetPublic();
			_currentLobby.SetJoinable(true);
			_currentLobby.Owner = new Friend(SteamClient.SteamId);

			// 获取Socket
			CreateListeningSocket();

			return true; // 成功
		} catch (Exception ex) {
			MPMain.LogError(Localization.Get("MPSteamworks.CreateLobbyException", ex.Message));
			return false; // 失败
		}
	}

	/// <summary>
	/// CreateRoom 异步启动包装器
	/// </summary>
	public void CreateRoom(int maxPlayers, Dictionary<string, string> lobbyData, Action<bool> callback) {
		// 启动异步
		StartCoroutine(RunAsync(CreateRoomAsync(maxPlayers, lobbyData), callback));
	}

	/// <summary>
	/// 加入大厅(客户端模式)- 异步版本
	/// </summary>
	public async Task<bool> JoinRoomAsync(Lobby lobby) {
		// 清理全部连接
		DisconnectAll();

		try {
			// 核心改变:直接 await 任务
			RoomEnter result = await lobby.Join();

			// 检查 RoomEnter 结果
			if (result != RoomEnter.Success) {
				throw new Exception($"Failed to join Steam lobby: {result.ToString()}");
			}

			_currentLobby = lobby;
			string roomName = LobbyName
				?? Localization.Get("MPSteamworks.NullLobbyName");
			MPMain.LogInfo(Localization.Get("MPSteamworks.JoinLobbySuccess", roomName));

			// 获取Socket
			CreateListeningSocket();
			return true;

		} catch (Exception ex) {
			MPMain.LogError(Localization.Get("MPSteamworks.JoinLobbyException", ex.Message));
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

	#region[Steam事件处理函数]
	/// <summary>
	/// 接收数据: 进入到大厅->LobbyEntered总线
	/// </summary>
	private void HandleLobbyEntered(Lobby lobby) {
		_currentLobby = lobby;
		HostSteamId = lobby.Owner.Id;
		MPMain.LogInfo(Localization.Get("MPSteamworks.EnteredLobby", lobby.Id.ToString()));

		// 在这里连接所有玩家
		// 遍历大厅里已经在的所有成员
		foreach (var member in lobby.Members) {
			if (member.Id == UserSteamId) continue; // 跳过自己
			MPMain.LogInfo(Localization.Get("MPSteamworks.ConnectedToLobbyPlayer", member.Name, member.Id.ToString()));
			StartCoroutine(ConnectionController(member.Id, false));
		}

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

			// 连接到新玩家
			if (friend.Id != SteamClient.SteamId) {
				StartCoroutine(ConnectionController(friend.Id, false));
			}
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
			_currentLobby = lobby;
			MPMain.LogInfo(Localization.Get("MPSteamworks.PlayerDisconnectedFromLobby", friend.Name));

			// 重复分发
			// 发布断开事件到总线
			//SteamNetworkEvents.TriggerPlayerDisconnected(friend.Id);
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
				"MPSteamworks.LobbyIdChanged", _currentLobby.Id.ToString(), lobby.Id.ToString()));
			_currentLobby = lobby;
			// 以后会在这里触发总线
			return;
		}

		// 原大厅 更新部分大厅数据
		_currentLobby = lobby;
		// 获取当前大厅真正的主机(Owner)
		SteamId currentOwnerId = lobby.Owner.Id;
		// 检查所有权是否发生了变更
		if (HostSteamId != 0 && HostSteamId != currentOwnerId) {
			MPMain.LogInfo(Localization.Get("MPSteamworks.HostChanged", HostSteamId.ToString(), currentOwnerId.ToString()));

			// 触发主机变更总线
			MPEventBusNet.NotifyLobbyHostChanged(lobby, new Friend(HostSteamId));

			HostSteamId = currentOwnerId;

			// 如果当前玩家是新主机 更改大厅所有者数据
			if (currentOwnerId == UserSteamId)
				_currentLobby.SetData("owner", UserSteamId.ToString());
		}
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

	#endregion

	#region[重连机制]

	/// <summary>
	/// 通用的连接控制器:支持初始连接和断线重连
	/// </summary>
	public IEnumerator ConnectionController(SteamId targetId, bool isReconnect) {
		// 如果是重连 等待1.5秒进行连接清理
		yield return new WaitForSeconds(isReconnect ? 1.5f : 0.2f);
		// 目标不在大厅或自己不在大厅 时退出连接流程
		if (!IsInLobby || !IsMemberInLobby(targetId)) yield break;

		// 核心重用逻辑:尝试并验证连接
		IEnumerator AttemptAndVerify(int maxAttempts) {
			for (int i = 0; i < maxAttempts; i++) {
				// 检查现有连接(可能在循环开始前已连上)
				if (_allConnections.ContainsKey(targetId)) {
					yield return new WaitForSeconds(1.0f);
					if (_allConnections.ContainsKey(targetId)) {
						HasConnections = true;
						MPEventBusNet.NotifyPlayerConnected(targetId);
						yield break; // 成功退出
					}
				}

				ExecuteConnection(targetId);

				// 等待重试间隔,期间持续检查状态
				float timer = 0;
				while (timer < 3.0f) { // 3秒重试间隔
					if (_allConnections.ContainsKey(targetId)) {
						yield return new WaitForSeconds(1.0f);
						if (_allConnections.ContainsKey(targetId)) {
							HasConnections = true;
							MPEventBusNet.NotifyPlayerConnected(targetId);
							yield break; // 成功退出
						}
					}
					timer += Time.deltaTime;
					yield return null;
				}
				MPMain.LogWarning(Localization.Get("MPSteamworks.ConnectionAttemptFailed", i + 1, targetId));
			}
		}

		bool isInitiator = UserSteamId < targetId;

		if (isInitiator) {

			MPMain.LogInfo(Localization.Get("MPSteamworks.ConnectionInitiator", targetId));
			yield return AttemptAndVerify(3);
		} else {

			MPMain.LogInfo(Localization.Get("MPSteamworks.ConnectionEeceiver", targetId));
			float waitTimer = 0;
			bool alreadyConnected = false;

			while (waitTimer < 10f) {
				if (_allConnections.ContainsKey(targetId)) {
					yield return new WaitForSeconds(1.0f);
					if (_allConnections.ContainsKey(targetId)) {
						HasConnections = true;
						MPEventBusNet.NotifyPlayerConnected(targetId);
						alreadyConnected = true;
						break;
					}
				}
				waitTimer += Time.deltaTime;
				yield return null;
			}

			if (!alreadyConnected) {
				MPMain.LogWarning(Localization.Get("MPSteamworks.ReverseConnectionAttempt"));
				yield return AttemptAndVerify(3);
			}
		}

		if (!_allConnections.ContainsKey(targetId) && IsInLobby && IsMemberInLobby(targetId)) {
			MPMain.LogError(Localization.Get("MPSteamworks.ConnectionProcessFailed"));
			yield break;
		}
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
			//MPEventBusNet.NotifyPlayerConnected(steamId);
			//Instance?.HasConnections = true;
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
			MPMain.LogError(Localization.Get("MPSteamworks.AsyncTaskFailed", task.Exception.InnerException.Message));
			callback?.Invoke(false);
		} else {
			// Task.Result 即为异步方法的返回值 (bool)
			callback?.Invoke(task.Result);
		}
	}

	/// <summary>
	/// 设置大厅默认数据,确保所有玩家都能识别这是同一款游戏的大厅,并且可以进行版本兼容性检查
	/// </summary>
	private Dictionary<string, string> GetDefaultLobbyData() {
		var dict = new Dictionary<string, string>();
		dict["game"] = "White Knuckle";
		dict["version"] = Application.version;
		dict["owner"] = UserSteamId.ToString();
		dict["visibility"] = "public";
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
		if (Time.time - _lastRealFetchTime < CACHE_PROTECTION_TIME) {
			MPMain.LogInfo("[MP Debug] 处于5秒冷却期内, 直接返回缓存结果");
			return Task.FromResult(LastFetchedLobbies);
		}

		lock (_taskLock) {
			// 重置30秒计时器（无论是手动还是自动触发, 都重新开始倒计时）
			_autoRefreshTimer.Reset();

			// 如果已经有一个任务在运行, 直接返回该任务
			if (_currentRefreshTask != null && !_currentRefreshTask.IsCompleted) {
				return _currentRefreshTask;
			}

			// 更新真实请求时间, 并开始新任务
			_lastRealFetchTime = Time.time;
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
				if (friend.IsPlayingThisGame
					&& friend.GameInfo?.Lobby is Lobby lobby) {

					if (lobbyIds.Add(lobby.Id)) combinedLobbies.Add(lobby);// 添加不重复Lobby
				}
			}

			// 搜索公开大厅
			var query = new Steamworks.Data.LobbyQuery()
				.FilterDistanceWorldwide()
				.WithKeyValue("game", "White Knuckle") // 确保是同一款游戏的
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
}