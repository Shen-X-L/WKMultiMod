using LiteNetLib.Utils;
using Steamworks;
using Steamworks.Data;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using WKMultiMod.src.Core;
using WKMultiMod.src.Data;
using WKMultiMod.src.Util;

namespace WKMultiMod.src.NetWork;

// 只做连接,不做业务逻辑
public class MPSteamworks : MonoBehaviour {

	// Debug日志输出间隔
	private TickTimer _debugTick = new TickTimer(5f);

	private Lobby _currentLobby;

	// 监听socket
	internal SocketManager _socketManager;
	// 出站连接池
	internal Dictionary<SteamId, ConnectionManager> _outgoingConnections = new Dictionary<SteamId, ConnectionManager>();
	// 已经建立成功的连接池
	internal Dictionary<SteamId, Connection> _allConnections = new Dictionary<SteamId, Connection>();

	// 是否有链接
	public bool HasConnections { get; private set; }

	// 消息队列
	private ConcurrentQueue<NetworkMessage> _messageQueue = new ConcurrentQueue<NetworkMessage>();

	// 本机SteamId
	private SteamId _mySteamId;
	public SteamId MySteamId { get => _mySteamId; private set => _mySteamId = value; }

	// 之前的主机SteamId
	private SteamId _lastKnownHostId;

	// 获取当前大厅ID
	public ulong CurrentLobbyId {
		get { return _currentLobby.Id.Value; }
	}
	// 检查是否在房间中
	public bool IsInLobby {
		get { return _currentLobby.Id.IsValid; }
	}

	// 检查是否是大厅所有者
	public bool IsHost {
		get {
			if (_currentLobby.Id == 0) return false;
			return _currentLobby.Owner.Id == SteamClient.SteamId;
		}
	}

	void Awake() {

		//SteamClient.Init(3195790u);

		try {
			if (!SteamClient.IsValid) {
				MPMain.LogError(
					"[MPSW] Steamworks初始化失败,请检查Steam在线情况",
					"[MPSW] Failed to initialize Steamworks. Please check your Steam login status.");
				return;
			}

			MPMain.LogInfo(
				$"[MPSW] Steamworks初始化成功 玩家: " +
				$"玩家: {SteamClient.Name} ID: {SteamClient.SteamId.ToString()}",
				$"[MPSW] Steamworks initialization succeeded. " +
				$"Player: {SteamClient.Name} ID: {SteamClient.SteamId.ToString()}");
			MySteamId = SteamClient.SteamId;

			// 订阅MPCore会发送的事件
			SteamNetworkEvents.OnSendToHost += HandleSendToHost;
			SteamNetworkEvents.OnBroadcast += HandleBroadcast;
			SteamNetworkEvents.OnSendToPeer += HandleSendToPeer;

			SteamNetworkEvents.OnConnectToHost += ConnectToHost;

			// 订阅大厅事件 大部分只做转发
			// 本机加入大厅
			SteamMatchmaking.OnLobbyEntered += OnLobbyEntered;
			// 该用户已经加入或正在加入大厅
			SteamMatchmaking.OnLobbyMemberJoined += OnLobbyMemberJoined;
			// 该用户已离开或即将离开大厅
			SteamMatchmaking.OnLobbyMemberLeave += OnLobbyMemberLeft;
			// 该用户在未离开大厅的情况下断线
			SteamMatchmaking.OnLobbyMemberDisconnected += OnLobbyMemberDisconnected;
			// 当大厅成员数据或大厅所有权发生变更
			SteamMatchmaking.OnLobbyMemberDataChanged += OnLobbyMemberDataChanged;

			// 初始化中继网络(必须调用)
			SteamNetworkingUtils.InitRelayNetworkAccess();

		} catch (Exception ex) {
			MPMain.LogError(
				$"[MPSW] Steamworks初始化异常: {ex.Message}",
				$"[MPSW] Steamworks initialization exception: {ex.Message}");
		}

	}

	void Update() {
		// 关键：在 Update 中持续调用 RunCallbacks
		Steamworks.SteamClient.RunCallbacks();

		ProcessMessageQueue();

		if (_socketManager != null) {
			_socketManager.Receive(32);
		}

		foreach (var connectionManager in _outgoingConnections.Values) {
			connectionManager.Receive(32);
		}
	}

	void OnDestroy() {
		// 取消订阅
		SteamNetworkEvents.OnSendToHost -= HandleSendToHost;
		SteamNetworkEvents.OnBroadcast -= HandleBroadcast;
		SteamNetworkEvents.OnSendToPeer -= HandleSendToPeer;

		SteamNetworkEvents.OnConnectToHost -= ConnectToHost;

		// 取消订阅大厅事件 大部分只做转发
		// 本机加入大厅
		SteamMatchmaking.OnLobbyEntered -= OnLobbyEntered;
		// 该用户已经加入或正在加入大厅
		SteamMatchmaking.OnLobbyMemberJoined -= OnLobbyMemberJoined;
		// 该用户已离开或即将离开大厅
		SteamMatchmaking.OnLobbyMemberLeave -= OnLobbyMemberLeft;
		// 该用户在未离开大厅的情况下断线
		SteamMatchmaking.OnLobbyMemberDisconnected -= OnLobbyMemberDisconnected;
		// 当大厅成员数据或大厅所有权发生变更
		SteamMatchmaking.OnLobbyMemberDataChanged -= OnLobbyMemberDataChanged;

		DisconnectAll();
	}

	/// <summary>
	/// 断开所有连接(清理网络资源)
	/// </summary>
	public void DisconnectAll() {
		// 断开所有出站连接
		foreach (var connectionManager in _outgoingConnections.Values) {
			try {
				connectionManager.Close();
			} catch { }
		}
		_outgoingConnections.Clear();

		// 关闭监听Socket
		if (_socketManager != null) {
			try {
				_socketManager.Close();
			} catch { }
			_socketManager = null;
		}

		// 清理所有连接记录
		_allConnections.Clear();

		// 离开大厅(如果有)
		if (_currentLobby.Id.IsValid) {
			try {
				_currentLobby.Leave();
			} catch { }
			_currentLobby = default;
		}

		// 清空消息队列
		while (_messageQueue.TryDequeue(out _)) { }

		HasConnections = false;

		MPMain.LogInfo(
			"[MPSW] 所有网络连接已断开",
			"[MPSW] All network connections have been disconnected.");
	}

	/// <summary>
	/// 获取大厅ID
	/// </summary>
	public ulong GetLobbyId() {
		return _currentLobby.Id.Value;
	}

	/// <summary>
	/// 发送数据: 本机->总线->主机玩家
	/// </summary>
	private void HandleSendToHost(byte[] data, SendType sendType, ushort laneIndex) {
		if (_currentLobby.Id.IsValid) {
			var hostSteamId = _currentLobby.Owner.Id;
			if (hostSteamId != SteamClient.SteamId && _allConnections.TryGetValue(hostSteamId, out var connection)) {
				connection.SendMessage(data, sendType, laneIndex);
			}
		}
	}

	/// <summary>
	/// 发送数据: 本机->总线->所有连接玩家
	/// </summary>
	private void HandleBroadcast(byte[] data, SendType sendType, ushort laneIndex) {

		// Debug
		bool canLog = _debugTick.IsTick();
		if (canLog) {
			MPMain.LogInfo(
				$"[MPSW] 开始广播数据,当前连接数: {_allConnections.Count.ToString()}",
				$"[MPSW] Started broadcasting data, current connections: {_allConnections.Count.ToString()}");
		}

		foreach (var (steamId, connection) in _allConnections) {
			try {
				if (canLog) {
					MPMain.LogInfo(
						$"[MPSW] 广播数据,当前连接: " +
						$"SteamId: {steamId.ToString()} 连接Id: {connection.Id.ToString()}",
						$"[MPSW] Sending data to connections. " +
						$"SteamId: {steamId.ToString()} ConnectionId: {connection.Id.ToString()}");
				}

				connection.SendMessage(data, sendType, laneIndex);
			} catch (Exception ex) {
				MPMain.LogError(
					$"[MPSW] 广播数据异常: {ex.Message}",
					$"[MPSW] Broadcasting data exception: {ex.Message}");
			}
		}

	}

	/// <summary>
	/// 发送数据: 本机->总线->特定玩家
	/// </summary>
	/// <param name="data"></param>
	/// <param name="steamId"></param>
	/// <param name="sendType"></param>
	/// <param name="laneIndex"></param>
	private void HandleSendToPeer(byte[] data, SteamId steamId, SendType sendType, ushort laneIndex) {
		try {
			_allConnections[steamId].SendMessage(data, sendType, laneIndex);
		} catch (Exception ex) {
			MPMain.LogError(
				$"[MPSW] 单播数据异常: {ex.Message} SteamId: {steamId.ToString()}",
				$"[MPSW] Unicast data exception: {ex.Message} SteamId: {steamId.ToString()}");
		}
	}

	/// <summary>
	/// 接收数据: 任意玩家->消息队列
	/// </summary>
	public void ReceiveNetworkMessage(SteamId senderId, byte[] data) {
		_messageQueue.Enqueue(new NetworkMessage {
			SenderId = senderId,
			Data = data,
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
				// 直接转发到总线,不处理业务逻辑
				SteamNetworkEvents.TriggerReceiveSteamData(message.SenderId.Value, message.Data);
				processedCount++;
			} catch (Exception ex) {
				MPMain.LogError(
					$"[MPSW] 消息队列转发消息异常: {ex.Message}",
					$"[MPSW] MessageQueue forwarding message to MPCore exception: {ex.Message}");
			}
		}
	}

	/// <summary>
	/// 接收数据: 任意玩家->本机 / 本机->任意玩家 连接成功 -> Player(In/Out)Connected总线
	/// </summary>
	public void OnPlayerConnected(SteamId steamId, Connection connection, bool isIncoming) {

		// 出站连接不覆盖入站连接
		if (_allConnections.ContainsKey(steamId) && !isIncoming) {
			return; 
		}
		// 入站连接覆盖出站连接
		if (_allConnections.ContainsKey(steamId) && isIncoming) {
			_allConnections[steamId] = connection;
			MPMain.LogInfo(
				$"[MPSW] 玩家入站连接覆盖: " +
				$"SteamId: {steamId.ToString()} 连接Id: {connection.Id.ToString()}",
				$"[MPSW] Player incoming connection overridden. " +
				$"SteamId: {steamId.ToString()} ConnectionId: {connection.Id.ToString()}");
			return;
		}

		_allConnections[steamId] = connection;

		// 正常记录
		MPMain.LogInfo(
			$"[MPSW] 玩家连接建立完成 ({(isIncoming ? "入站" : "出站")}) " +
			$"SteamId: {steamId.ToString()} 连接Id: {connection.Id.ToString()}",
			$"[MPSW] Player connection established ({(isIncoming ? "incoming" : "outgoing")}) " +
			$"SteamId: {steamId.ToString()} ConnectionId: {connection.Id.ToString()}");

		HasConnections = true;

		// 触发总线 RemotePlayerManager
		SteamNetworkEvents.TriggerPlayerConnected(steamId);
	}

	/// <summary>
	/// 接收数据: 玩家断开连接 -> PlayerDisconnected总线
	/// </summary>
	private void OnPlayerDisconnected(SteamId steamId) {
		if (_allConnections.ContainsKey(steamId)) {
			_allConnections.Remove(steamId);

			// 如果你维护了主动连接字典,也要清理
			if (_outgoingConnections.ContainsKey(steamId)) {
				_outgoingConnections.Remove(steamId);
			}

			MPMain.LogInfo(
				$"[MPSW] 玩家断开,已清理连接. SteamId: {steamId.ToString()}",
				$"[MPSW] Player disconnected, connection cleaned up. SteamId: {steamId.ToString()}");

			// 检查是否还有剩余连接
			HasConnections = _allConnections.Count > 0;

			// 触发业务层销毁玩家
			SteamNetworkEvents.TriggerPlayerDisconnected(steamId);
		}
	}

	/// <summary>
	/// 处理主机请求现有玩家列表
	/// </summary>
	private void HandleHostRequestExistingPlayers(SteamId newPlayerSteamId) {
		if (!IsHost) return;

		// 获取所有连接中的SteamId(除了新玩家)
		var existingSteamIds = new List<SteamId>();
		foreach (var steamId in _allConnections.Keys) {
			if (steamId != newPlayerSteamId && steamId != SteamClient.SteamId) {
				existingSteamIds.Add(steamId);
			}
		}
		// 这里只是记录,实际转换在Core中完成
		MPMain.LogInfo(
			$"[MPSW] 主机获取到 {existingSteamIds.Count} 个现有玩家连接",
			$"[MPSW] Host obtained {existingSteamIds.Count} existing player connections.");
	}

	/// <summary>
	/// 主动连接到指定玩家(纯网络连接,不处理业务逻辑)
	/// </summary>
	private void ConnectToPlayer(SteamId steamId) {
		try {
			if (_outgoingConnections.ContainsKey(steamId)) {
				MPMain.LogWarning(
					$"[MPSW] 已经连接过玩家. SteamId: {steamId.ToString()}",
					$"[MPSW] Already connected to player. SteamId: {steamId.ToString()}");
				return;
			}

			var connectionManager = SteamNetworkingSockets.ConnectRelay<SteamConnectionManager>(steamId, 0);
			_outgoingConnections[steamId] = connectionManager;

			MPMain.LogInfo(
				$"[MPSW] 正在出站连接玩家. SteamId: {steamId.ToString()}",
				$"[MPSW] Connecting to player. SteamId: {steamId.ToString()}");
		} catch (Exception ex) {
			MPMain.LogError(
				$"[MPSW] 连接玩家异常: {ex.Message}",
				$"[MPSW] Exception while connecting to player: {ex.Message}");
		}
	}

	/// <summary>
	/// 主动连接到主机
	/// </summary>
	private void ConnectToHost() {
		SteamId hostId = _currentLobby.Owner.Id;
		if (IsHost || _outgoingConnections.ContainsKey(hostId)) {
			return;
		}
		var connectionManager = SteamNetworkingSockets.ConnectRelay<SteamConnectionManager>(hostId, 0);
		_outgoingConnections[hostId] = connectionManager;
	}

	/// <summary>
	/// 纯测试 会卡死主进程
	/// </summary>
	/// <param name="maxPlayers"></param>
	//public void TestLobbyCreationSync(int maxPlayers) {
	//	if (!SteamClient.IsValid) return;
	//	try {
	//		Lobby? lobbyResult = SteamMatchmaking.CreateLobbyAsync(maxPlayers).Result;
	//		if (lobbyResult.HasValue) {
	//			MPMain.Logger.LogInfo("[TEST] 同步创建成功!");
	//		} else {
	//			MPMain.Logger.LogError("[TEST] 同步创建失败: 结果为空");
	//		}
	//	} catch (Exception ex) {
	//		MPMain.Logger.LogError($"[TEST] 同步创建抛出 C# 异常: {ex.Message}");
	//		// 如果这里捕获到异常,请记录并报告！
	//	}
	//}

	/// <summary>
	/// 创建房间(主机模式)- 异步版本
	/// </summary>
	public async Task<bool> CreateRoomAsync(string roomName, int maxPlayers) {
		// 清理全部连接
		DisconnectAll();
		//await Task.Yield();

		try {
			if (!SteamClient.IsValid) {
				MPMain.LogError(
					"[MPSW] SteamClient 无效,请检查Steam在线情况",
					"[MPSW] SteamClient is invalid. Please check your Steam login status.");
				return false;
			}

			// 核心：直接 await 任务
			Lobby? lobbyResult = await SteamMatchmaking.CreateLobbyAsync(maxPlayers);

			// 只检查结果并返回,移除所有同步大厅设置和 Socket 创建！
			if (!lobbyResult.HasValue) {
				MPMain.LogError(
					"[MPSW] 创建大厅失败",
					"[MPSW] Failed to create lobby");
				return false;
			}

			_currentLobby = lobbyResult.Value;

			MPMain.LogInfo(
				$"[MPSW] 大厅创建成功,ID: {_currentLobby.Id.ToString()}",
				$"[MPSW] Lobby created successfully.Lobby ID: {_currentLobby.Id.ToString()}");

			// 设置大厅信息
			_currentLobby.SetData("name", roomName);
			_currentLobby.SetData("game_version", Application.version);
			_currentLobby.SetData("owner", SteamClient.SteamId.ToString());
			_currentLobby.SetPublic();
			_currentLobby.SetJoinable(true);
			_currentLobby.Owner = new Friend(SteamClient.SteamId);

			// 获取Socket
			try {
				_socketManager = SteamNetworkingSockets.CreateRelaySocket<SteamSocketManager>();
			} catch (Exception socketEx) {
				MPMain.LogError(
					$"[MPSW] 创建Socket失败: {socketEx.Message}",
					$"[MPSW] Create Socket exception: {socketEx.Message}");
			}

			return true; // 成功
		} catch (Exception ex) {
			MPMain.LogError(
				$"[MPSW] 创建大厅异常: {ex.Message}",
				$"[MPSW] Create Lobby exception: {ex.Message}");
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
	/// 加入房间(客户端模式)- 异步版本
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
				?? (MPMain.DebugLogLanguage == 0 ? "未知大厅" : "Unknown lobby");
			MPMain.LogInfo(
				$"[MPSW] 加入大厅成功: {roomName}",
				$"[MPSW] Successfully joined lobby: {roomName}");

			// 获取Socket
			// 在主机离线,该玩家被提升为主机时使用
			try {
				_socketManager = SteamNetworkingSockets.CreateRelaySocket<SteamSocketManager>();
			} catch (Exception socketEx) {
				MPMain.LogError(
					$"[MPSW] 创建Socket失败: {socketEx.Message}",
					$"[MPSW] Create Socket exception: {socketEx.Message}");
			}

			return true;

		} catch (Exception ex) {
			MPMain.LogError(
				$"[MPSW] 加入大厅异常: {ex.Message}",
				$"[MPSW] Join lobby exception: {ex.Message}");
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

	/// <summary>
	/// 连接到指定玩家 - 异步版本
	/// </summary>
	public async Task<bool> ConnectToPlayerAsync(SteamId steamId) {
		ConnectionManager connectionManager = null;
		float timeout = 5f;
		float startTime = Time.time;

		// 初始检查
		if (_outgoingConnections.ContainsKey(steamId) || _allConnections.ContainsKey(steamId)) {
			MPMain.LogWarning(
				$"[MPSW] 已经连接到玩家. SteamId: {steamId.ToString()}",
				$"[MPSW] Already connected to player. SteamId: {steamId.ToString()}");
			return true;
		}

		// 1. 同步建立连接
		try {
			MPMain.LogInfo(
				$"[MPSW] 正在连接玩家. SteamId: {steamId.ToString()}",
				$"[MPSW] Connecting to player. SteamId: {steamId.ToString()}");

			// 建立连接
			connectionManager = SteamNetworkingSockets.ConnectRelay<SteamConnectionManager>(steamId, 0);
			_outgoingConnections[steamId] = connectionManager;
			_allConnections[steamId] = connectionManager.Connection;

		} catch (Exception ex) {
			MPMain.LogError(
				$"[MPSW] 建立连接异常: {ex.Message}",
				$"[MPSW] Exception while connecting to player: {ex.Message}");
			return false;
		}

		// 2. 异步等待连接建立
		if (connectionManager != null) {
			while (connectionManager.ConnectionInfo.State != ConnectionState.Connected) {
				if (Time.time - startTime > timeout) {
					MPMain.LogError(
						$"[MPSW] 连接玩家超时. SteamId: {steamId.ToString()}",
						$"[MPSW] Connection to player timed out. SteamId: {steamId.ToString()}");
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

		MPMain.LogInfo(
			$"[MPSW] 连接玩家成功. SteamId: {steamId.ToString()}",
			$"[MPSW] Successfully connected to player. SteamId: {steamId.ToString()}");
		return true;
	}

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
			MPMain.LogError(
				$"[MPSW] 异步任务执行失败: {task.Exception.InnerException.Message}",
				$"[MPSW] Async task execution failed: {task.Exception.InnerException.Message}");
			callback?.Invoke(false);
		} else {
			// Task.Result 即为异步方法的返回值 (bool)
			callback?.Invoke(task.Result);
		}
	}

	/// <summary>
	/// 接收数据: 进入到大厅->LobbyEntered总线
	/// </summary>
	private void OnLobbyEntered(Lobby lobby) {
		_currentLobby = lobby;
		_lastKnownHostId = lobby.Owner.Id;
		MPMain.LogInfo(
			$"[MPSW] 进入大厅. 大厅Id: {lobby.Id.ToString()}",
			$"[MPSW] Entered lobby. LobbyId: {lobby.Id.ToString()}");

		// 发布事件到总线
		SteamNetworkEvents.TriggerLobbyEntered(lobby);
	}

	/// <summary>
	/// 接收数据: 大厅有成员加入->LobbyMemberJoined总线->连接新玩家
	/// </summary>
	private void OnLobbyMemberJoined(Lobby lobby, Friend friend) {
		if (lobby.Id == _currentLobby.Id) {
			MPMain.LogInfo(
				$"[MPSW] 玩家加入大厅. SteamId: {friend.Name}",
				$"[MPSW] Player joined room. SteamId: {friend.Name}");

			// 发布事件到总线
			SteamNetworkEvents.TriggerLobbyMemberJoined(friend.Id);

			// 连接到新玩家
			if (friend.Id != SteamClient.SteamId) {
				ConnectToPlayer(friend.Id);
			}
		}
	}

	/// <summary>
	/// 接收数据: 大厅有成员离开->LobbyMemberLeft总线
	/// </summary>
	private void OnLobbyMemberLeft(Lobby lobby, Friend friend) {
		if (lobby.Id == _currentLobby.Id) {
			MPMain.LogInfo(
				$"[MPSW] 玩家离开大厅. SteamId: {friend.Name}",
				$"[MPSW] Player left the room. SteamId: {friend.Name}");

			// 发布事件到总线
			SteamNetworkEvents.TriggerLobbyMemberLeft(friend.Id);

			// 只在这里处理连接清理
			OnPlayerDisconnected(friend.Id);
		}
	}

	/// <summary>
	/// 接收数据: 大厅有成员断开连接-> 总线
	/// </summary>
	private void OnLobbyMemberDisconnected(Lobby lobby, Friend friend) {
		if (lobby.Id == _currentLobby.Id) {
			MPMain.LogInfo(
				$"[MPSW] 玩家断开大厅连接. SteamId: {friend.Name}",
				$"[MPSW] Player disconnected from the lobby. SteamId: {friend.Name}");

			// 重复分发
			// 发布断开事件到总线
			//SteamNetworkEvents.TriggerPlayerDisconnected(friend.Id);
		}
	}

	/// <summary>
	/// 接收数据: 大厅数据变更->
	/// 主机变更->LobbyHostChanged总线
	/// </summary>
	/// <param name="lobby"></param>
	/// <param name="friend"></param>
	private void OnLobbyMemberDataChanged(Lobby lobby, Friend friend) {
		// 还是原房间
		if (lobby.Id == _currentLobby.Id) {
			// 更新部分房间数据
			_currentLobby = lobby;
			// 获取当前大厅真正的主机（Owner）
			SteamId currentOwnerId = lobby.Owner.Id;
			// 检查所有权是否发生了变更
			if (_lastKnownHostId != 0 && _lastKnownHostId != currentOwnerId) {
				MPMain.LogInfo(
					$"[MPCore] 主机变更: {_lastKnownHostId.ToString()} -> {currentOwnerId.ToString()}",
					$"[MPCore] Host change: {_lastKnownHostId.ToString()} -> {currentOwnerId.ToString()}");

				// 触发主机变更总线
				SteamNetworkEvents.TriggerLobbyHostChanged(lobby, _lastKnownHostId);
			}
					
		}
	}

	/// <summary>
	/// Steam Socket管理器 - 完全无状态
	/// </summary>
	private class SteamSocketManager : SocketManager {
		// 使用静态方法获取当前实例
		private static MPSteamworks GetInstance() {
			return MPCore.Instance.Steamworks;
		}

		// 有玩家正在接入
		public override void OnConnecting(Connection connection, ConnectionInfo info) {
			MPMain.LogInfo(
				$"[MPSW] 玩家正在连接. SteamId{info.Identity.SteamId.ToString()}",
				$"[MPSW] Player is connecting. SteamId{info.Identity.SteamId.ToString()}");
			connection.Accept();
		}
		
		// 有玩家已经接入
		public override void OnConnected(Connection connection, ConnectionInfo info) {
			var instance = GetInstance();
			if (instance != null) {
				instance.OnPlayerConnected(info.Identity.SteamId, connection, true);
			}
		}

        public override void OnConnectionChanged(Connection connection, ConnectionInfo info) {
            base.OnConnectionChanged(connection, info);
        }

		// 接收消息
		public override void OnMessage(Connection connection, NetIdentity identity,
									  IntPtr data, int size, long messageNum,
									  long recvTime, int channel) {
			var instance = GetInstance();
			if (instance != null) {
				byte[] bytes = new byte[size];
				System.Runtime.InteropServices.Marshal.Copy(data, bytes, 0, size);
				instance.ReceiveNetworkMessage(identity.SteamId, bytes);
			}
		}

		// 连接被本地或远程关闭
		public override void OnDisconnected(Connection connection, ConnectionInfo info) {
			var instance = GetInstance();
			if (instance != null) {
				instance.OnPlayerDisconnected(info.Identity.SteamId);
			}
		}
	}

	/// <summary>
	/// Steam 连接管理器 - 完全无状态
	/// </summary>
	private class SteamConnectionManager : ConnectionManager {
		private static MPSteamworks GetInstance() {
			return MPCore.Instance.Steamworks;
		}

		// 正在去连接
		public override void OnConnecting(ConnectionInfo info) { }

		// 连接已建立
		public override void OnConnected(ConnectionInfo info) {
			var instance = GetInstance();
			if (instance != null) {
				instance.OnPlayerConnected(info.Identity.SteamId, this.Connection, false);
			}
		}

		// 接收消息
		public override void OnMessage(IntPtr data, int size, long messageNum, long recvTime, int channel) {
			var instance = GetInstance();
			if (instance != null) {
				byte[] bytes = new byte[size];
				System.Runtime.InteropServices.Marshal.Copy(data, bytes, 0, size);
				instance.ReceiveNetworkMessage(this.ConnectionInfo.Identity.SteamId, bytes);
			}
		}

		// 连接被本地或远程关闭
		public override void OnDisconnected(ConnectionInfo info) {
			var instance = GetInstance();
			if (instance != null) {
				instance.OnPlayerDisconnected(info.Identity.SteamId);
			}
		}
	}

	/// <summary>
	/// 网络消息结构
	/// </summary>
	private struct NetworkMessage {
		public SteamId SenderId;
		public byte[] Data;
		public DateTime ReceiveTime;
	}
}