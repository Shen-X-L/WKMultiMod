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
public class MPSteamworks : MonoBehaviour, ISocketManager, IConnectionManager {

	// Debug日志输出间隔
	private TickTimer _debugTick = new TickTimer(5.0f);
	private TickTimer _debugTick1 = new TickTimer(3.0f);
	private TickTimer _debugTick2 = new TickTimer(10.0f);

	/// <summary>
	/// 客户端连接信息类
	/// 封装SteamID和连接对象
	/// </summary>
	public struct Client {
		public SteamId steamId;      // Steam用户ID
		public Connection connection; // Steamworks连接对象
	}

	// 大厅缓存
	private Lobby _currentLobby;

	// 服务器套接字管理器
	internal SocketManager _socketManager;
	// 客户端连接管理器
	internal ConnectionManager _connectionManager;
	// 已连接客户端字典
	internal Dictionary<ulong, Client> _connectedClients;

	// 消息队列
	private ConcurrentQueue<NetworkMessage> _messageQueue = new ConcurrentQueue<NetworkMessage>();

	// 本机Id
	private ulong _userSteamId;
	public ulong UserSteamId { get => _userSteamId; private set => _userSteamId = value; }
	// 之前的主机Id
	private ulong _lastKnownHostSteamId;
	public ulong HostSteamId { get => _lastKnownHostSteamId; private set => _lastKnownHostSteamId = value; }
	// 广播Id
	private const ulong _broadcastId = 0;
	public ulong BroadcastId { get => _broadcastId; }

	// 获取当前大厅Id
	public ulong CurrentLobbyId {
		get { return _currentLobby.Id.Value; }
	}
	// 检查是否在大厅中
	public bool IsInLobby {
		get { return _currentLobby.Id.IsValid; }
	}

	// 是否有链接
	public bool HasConnections { get; private set; }

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
				$"玩家: {SteamClient.Name} Id: {SteamClient.SteamId.ToString()}",
				$"[MPSW] Steamworks initialization succeeded. " +
				$"Player: {SteamClient.Name} Id: {SteamClient.SteamId.ToString()}");

			// 初始化Steam中继网络访问
			SteamNetworkingUtils.InitRelayNetworkAccess();
			// 获取并显示用户Steam ID
			UserSteamId = SteamClient.SteamId;

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

		// 接收并处理网络数据
		_connectionManager?.Receive(32); // 客户端接收
		_socketManager?.Receive(32);     // 服务器接收

		// 处理数据队列
		ProcessMessageQueue();
	}

	void OnDestroy() {
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
		// 关闭客户端连接
		_connectionManager?.Close();
		_connectionManager = null; // 必须置空，防止 Update 继续 Receive
								   // 关闭服务器套接字
		_socketManager?.Close();
		_socketManager = null;

		// 清理所有连接记录
		// 字典初始化/清理
		if (_connectedClients == null) _connectedClients = new Dictionary<ulong, Client>();
		else _connectedClients.Clear();

		// 状态重置
		HasConnections = false;
		_lastKnownHostSteamId = 0;

		// 离开大厅(如果有)
		if (_currentLobby.Id.IsValid) {
			try {
				_currentLobby.Leave();
			} catch { }
			_currentLobby = default;
		}

		// 清空消息队列
		while (_messageQueue.TryDequeue(out _)) { }

		MPMain.LogInfo(
			"[MPSW] 所有网络连接已断开",
			"[MPSW] All network connections have been disconnected.");
	}

	/// <summary>
	/// 获取大厅Id
	/// </summary>
	public ulong GetLobbyId() {
		return _currentLobby.Id.Value;
	}

	/// <summary>
	/// 仅客户端 发送数据: 本机->主机玩家
	/// </summary>
	public void HandleSendToHost(byte[] data, SendType sendType = SendType.Reliable,
		ushort laneIndex = 0) {

		if (IsHost || _connectionManager == null) {
			return;
		}
		var result = _connectionManager.Connection.SendMessage(data, sendType, laneIndex);
		if (result != Result.OK) {
			if (_debugTick1.TryTick())
				MPMain.LogInfo(
					$"[MPSW] 消息发送失败! 结果: {result.ToString()}, 数据大小: {data.Length.ToString()}",
					$"[MPSW] Message sending failed! Result: {result.ToString()}, Data size: {data.Length.ToString()}");
		} else {
			//if (_debugTick1.TryTick())
			//	MPMain.LogError(
			//		$"[MPSW] 消息发送成功! 结果: {result.ToString()}, 数据大小: {data.Length.ToString()}",
			//		$"[MPSW] message successfully sent! Result: {result}, Data Size: {data.Length}");
		}
	}

	/// <summary>
	/// 仅客户端 发送数据: 本机->主机玩家
	/// </summary>
	public void HandleSendToHost(byte[] data, int offset, int length,
		SendType sendType = SendType.Reliable, ushort laneIndex = 0) {

		if (IsHost || _connectionManager == null) {
			return;
		}
		var result = _connectionManager.Connection.SendMessage(data, offset, length, sendType, laneIndex);
		if (result != Result.OK) {
			if (_debugTick1.TryTick())
				MPMain.LogInfo(
					$"[MPSW] 消息发送失败! 结果: {result.ToString()}, 数据大小: {data.Length.ToString()}",
					$"[MPSW] Message sending failed! Result: {result.ToString()}, Data size: {data.Length.ToString()}");
		} else {
			//if (_debugTick1.TryTick())
			//	MPMain.LogError(
			//		$"[MPSW] 消息发送成功! 结果: {result.ToString()}, 数据大小: {data.Length.ToString()}",
			//		$"[MPSW] message successfully sent! Result: {result}, Data Size: {data.Length}");
		}
	}

	/// <summary>
	/// 仅主机 发送数据: 本机->所有连接玩家
	/// </summary>
	public void HandleBroadcast(byte[] data, SendType sendType = SendType.Reliable,
		ushort laneIndex = 0) {

		// Debug
		bool canLog = _debugTick.TryTick();
		if (canLog) {
			MPMain.LogInfo(
				$"[MPSW] 开始广播数据,当前连接数: {_connectedClients.Count.ToString()}",
				$"[MPSW] Started broadcasting data, current connections: {_connectedClients.Count.ToString()}");
		}

		foreach (var (steamId, connection) in _connectedClients) {
			try {
				if (canLog) {
					MPMain.LogInfo(
						$"[MPSW] 广播数据,当前连接: " +
						$"SteamId: {steamId.ToString()} 连接Id: {connection.connection.Id.ToString()}",
						$"[MPSW] Sending data to connections. " +
						$"SteamId: {steamId.ToString()} ConnectionId: {connection.connection.Id.ToString()}");
				}

				connection.connection.SendMessage(data, sendType, laneIndex);
			} catch (Exception ex) {
				MPMain.LogError(
					$"[MPSW] 广播数据异常: {ex.Message}",
					$"[MPSW] Broadcasting data exception: {ex.Message}");
			}
		}
	}

	/// <summary>
	/// 仅主机 发送数据: 本机->所有连接玩家
	/// </summary>
	public void HandleBroadcast(byte[] data, int offset, int length,
		SendType sendType = SendType.Reliable, ushort laneIndex = 0) {

		// Debug
		bool canLog = _debugTick.TryTick();
		if (canLog) {
			MPMain.LogInfo(
				$"[MPSW] 开始广播数据,当前连接数: {_connectedClients.Count.ToString()}",
				$"[MPSW] Started broadcasting data, current connections: {_connectedClients.Count.ToString()}");
		}

		foreach (var (steamId, connection) in _connectedClients) {
			try {
				if (canLog) {
					MPMain.LogInfo(
						$"[MPSW] 广播数据,当前连接: " +
						$"SteamId: {steamId.ToString()} 连接Id: {connection.connection.Id.ToString()}",
						$"[MPSW] Sending data to connections. " +
						$"SteamId: {steamId.ToString()} ConnectionId: {connection.connection.Id.ToString()}");
				}

				connection.connection.SendMessage(data, offset, length, sendType, laneIndex);
			} catch (Exception ex) {
				MPMain.LogError(
					$"[MPSW] 广播数据异常: {ex.Message}",
					$"[MPSW] Broadcasting data exception: {ex.Message}");
			}
		}
	}

	/// <summary>
	/// 仅主机 发送数据: 本机->除个别玩家外所有连接玩家
	/// </summary>
	/// <param name="steamId">被排除的玩家</param>
	public void HandleBroadcastExcept(ulong steamId, byte[] data,
		SendType sendType = SendType.Reliable, ushort laneIndex = 0) {

		// Debug
		bool canLog = _debugTick.TryTick();
		if (canLog) {
			MPMain.LogInfo(
				$"[MPSW] 开始广播数据,当前连接数: {_connectedClients.Count.ToString()}",
				$"[MPSW] Started broadcasting data, current connections: {_connectedClients.Count.ToString()}");
		}

		foreach (var (tempSteamId, connection) in _connectedClients) {
			if (steamId == tempSteamId)
				continue;
			try {
				if (canLog) {
					MPMain.LogInfo(
						$"[MPSW] 广播数据,当前连接: " +
						$"SteamId: {tempSteamId.ToString()} 连接Id: {connection.connection.Id.ToString()}",
						$"[MPSW] Sending data to connections. " +
						$"SteamId: {tempSteamId.ToString()} ConnectionId: {connection.connection.Id.ToString()}");
				}
				connection.connection.SendMessage(data, sendType, laneIndex);
			} catch (Exception ex) {
				MPMain.LogError(
					$"[MPSW] 广播数据异常: {ex.Message}",
					$"[MPSW] Broadcasting data exception: {ex.Message}");
			}
		}
	}

	/// <summary>
	/// 仅主机 发送数据: 本机->除个别玩家外所有连接玩家
	/// </summary>
	/// <param name="steamId">被排除的玩家</param>
	public void HandleBroadcastExcept(ulong steamId, byte[] data, int offset, int length,
		SendType sendType = SendType.Reliable, ushort laneIndex = 0) {

		// Debug
		bool canLog = _debugTick.TryTick();
		if (canLog) {
			MPMain.LogInfo(
				$"[MPSW] 开始广播数据,当前连接数: {_connectedClients.Count.ToString()}",
				$"[MPSW] Started broadcasting data, current connections: {_connectedClients.Count.ToString()}");
		}

		foreach (var (tempSteamId, connection) in _connectedClients) {
			if (steamId == tempSteamId)
				continue;
			try {
				if (canLog) {
					MPMain.LogInfo(
						$"[MPSW] 广播数据,当前连接: " +
						$"SteamId: {tempSteamId.ToString()} 连接Id: {connection.connection.Id.ToString()}",
						$"[MPSW] Sending data to connections. " +
						$"SteamId: {tempSteamId.ToString()} ConnectionId: {connection.connection.Id.ToString()}");
				}
				connection.connection.SendMessage(data, offset, length, sendType, laneIndex);
			} catch (Exception ex) {
				MPMain.LogError(
					$"[MPSW] 广播数据异常: {ex.Message}",
					$"[MPSW] Broadcasting data exception: {ex.Message}");
			}
		}
	}

	/// <summary>
	/// 仅主机 发送数据: 本机->特定玩家
	/// </summary>
	public void HandleSendToPeer(ulong steamId, byte[] data,
		SendType sendType = SendType.Reliable, ushort laneIndex = 0) {

		try {
			_connectedClients[steamId].connection.SendMessage(data, sendType, laneIndex);
		} catch (Exception ex) {
			MPMain.LogError(
				$"[MPSW] 单播数据异常: {ex.Message} SteamId: {steamId.ToString()}",
				$"[MPSW] Unicast data exception: {ex.Message} SteamId: {steamId.ToString()}");
		}
	}

	/// <summary>
	/// 仅主机 发送数据: 本机->特定玩家
	/// </summary>
	public void HandleSendToPeer(ulong steamId, byte[] data, int offset, int length,
		SendType sendType = SendType.Reliable, ushort laneIndex = 0) {

		try {
			_connectedClients[steamId].connection.SendMessage(data, offset, length, sendType, laneIndex);
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
	/// 仅主机 接收数据: 玩家断开连接 -> PlayerDisconnected总线
	/// </summary>
	private void OnPlayerDisconnected(ulong steamId) {
		if (_connectedClients.ContainsKey(steamId)) {
			_connectedClients.Remove(steamId);

			MPMain.LogInfo(
				$"[MPSW] 玩家断开,已清理连接. SteamId: {steamId.ToString()}",
				$"[MPSW] Player disconnected, connection cleaned up. SteamId: {steamId.ToString()}");

			// 检查是否还有剩余连接
			HasConnections = _connectedClients.Count > 0;

			// 触发业务层销毁玩家
			SteamNetworkEvents.TriggerPlayerDisconnected(steamId);
		}
	}

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
			MPMain.LogError(
				$"[MPSW] 创建Socket失败: {socketEx.Message}",
				$"[MPSW] Create Socket exception: {socketEx.Message}");
		}
	}

	/// <summary>
	/// 创建大厅(主机模式)- 异步版本
	/// </summary>
	public async Task<bool> CreateRoomAsync(string roomName, int maxPlayers) {
		// 清理全部连接
		DisconnectAll();
		await Task.Yield();

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
				$"[MPSW] 大厅创建成功,Id: {_currentLobby.Id.ToString()}",
				$"[MPSW] Lobby created successfully.Lobby Id: {_currentLobby.Id.ToString()}");

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
				?? (MPMain.DebugLogLanguage == 0 ? "未知大厅" : "Unknown lobby");
			MPMain.LogInfo(
				$"[MPSW] 加入大厅成功: {roomName}",
				$"[MPSW] Successfully joined lobby: {roomName}");

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
		_lastKnownHostSteamId = lobby.Owner.Id;
		MPMain.LogInfo(
			$"[MPSW] 进入大厅. 大厅Id: {lobby.Id.ToString()}",
			$"[MPSW] Entered lobby. LobbyId: {lobby.Id.ToString()}");
		// 连接主机
		if (!IsHost) {
			ConnectToHost();
		}
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
		}
	}

	/// <summary>
	/// 接收数据: 大厅数据变更->
	/// 主机变更->LobbyHostChanged总线
	/// </summary>
	private void OnLobbyMemberDataChanged(Lobby lobby, Friend friend) {
		// 大厅变更
		if (lobby.Id != _currentLobby.Id) {
			// 更新部分大厅数据
			_currentLobby = lobby;
			MPMain.LogInfo(
				$"[MPCore] 大厅变更: {_currentLobby.Id.ToString()} -> {lobby.Id.ToString()}",
				$"[MPCore] Lobby change: {_currentLobby.Id.ToString()} -> {lobby.Id.ToString()}");
			return;
		}
		// 原大厅 更新部分大厅数据
		_currentLobby = lobby;
		// 获取当前大厅真正的主机(Owner)
		SteamId currentOwnerId = lobby.Owner.Id;
		// 检查所有权是否发生了变更
		if (_lastKnownHostSteamId != 0 && _lastKnownHostSteamId != currentOwnerId) {
			MPMain.LogInfo(
				$"[MPCore] 主机变更: {_lastKnownHostSteamId.ToString()} -> {currentOwnerId.ToString()}",
				$"[MPCore] Host change: {_lastKnownHostSteamId.ToString()} -> {currentOwnerId.ToString()}");
			
			if (!IsHost) {
				// 连接主机
				ConnectToHost();
			} else {
				// 重新创建监听Socket
				CreateListeningSocket();
			}
			// 触发主机变更总线
			SteamNetworkEvents.TriggerLobbyHostChanged(lobby, _lastKnownHostSteamId);
			// 更新主机Id
			_lastKnownHostSteamId = currentOwnerId;
		}
	}


	// 仅主机: 有玩家正在接入
	void ISocketManager.OnConnecting(Connection connection, ConnectionInfo info) {
		MPMain.LogInfo(
			$"[MPSW] 玩家正在连接. SteamId{info.Identity.SteamId.ToString()} " +
			$"连接Id: {connection.Id} 连接状态: {info.State}",
			$"[MPSW] Player is connecting. SteamId{info.Identity.SteamId.ToString()}" +
			$"Connection Id: {connection.Id} Connection state: {info.State}");
		connection.Accept();
	}

	// 仅主机: 有玩家已经接入
	void ISocketManager.OnConnected(Connection connection, ConnectionInfo info) {
		var steamId = info.Identity.SteamId;
		MPMain.LogInfo(
				$"[MPSW] 玩家已经接入. SteamId{steamId.ToString()} " +
				$"连接Id: {connection.Id} 连接状态: {info.State}",
				$"[MPSW] The player has already connected. SteamId{steamId.ToString()}" +
				$"Connection Id: {connection.Id} Connection state: {info.State}");

		if (!_connectedClients.ContainsKey(steamId)) {
			_connectedClients.Add(steamId, new Client {
				steamId = steamId,
				connection = connection,
			});
			SteamNetworkEvents.TriggerPlayerConnected(steamId);
			HasConnections = true;
		}
		return;
	}

	// 仅主机: 连接被本地或远程关闭
	void ISocketManager.OnDisconnected(Connection connection, ConnectionInfo info) {
		if (_connectedClients.Remove(info.Identity.SteamId)) {
			connection.Close();
			OnPlayerDisconnected(info.Identity.SteamId);
		}
	}

	// 仅主机: 接收消息
	void ISocketManager.OnMessage(Connection connection, NetIdentity identity,
								  IntPtr data, int size, long messageNum,
								  long recvTime, int channel) {
		byte[] bytes = new byte[size];
		System.Runtime.InteropServices.Marshal.Copy(data, bytes, 0, size);
		ReceiveNetworkMessage(identity.SteamId, bytes);
	}

	// 仅客户端: 正在去连接
	void IConnectionManager.OnConnecting(ConnectionInfo info) { }

	// 仅客户端: 连接已建立
	void IConnectionManager.OnConnected(ConnectionInfo info) {
		MPMain.LogInfo(
			$"[MPSW] 已经主动连接玩家. SteamId{info.Identity.SteamId.ToString()} 连接状态: {info.State}",
			$"[MPSW] Already actively connected to the player. SteamId{info.Identity.SteamId.ToString()}" +
			$"Connection state: {info.State}");
		HasConnections = true;
	}

	// 仅客户端: 接收消息
	void IConnectionManager.OnMessage(IntPtr data, int size, long messageNum, long recvTime, int channel) {
		byte[] bytes = new byte[size];
		System.Runtime.InteropServices.Marshal.Copy(data, bytes, 0, size);
		ReceiveNetworkMessage(_lastKnownHostSteamId, bytes);
	}

	// 仅客户端: 连接被本地或远程关闭
	void IConnectionManager.OnDisconnected(ConnectionInfo info) {
		OnPlayerDisconnected(info.Identity.SteamId);
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