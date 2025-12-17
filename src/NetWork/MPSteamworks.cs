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

namespace WKMultiMod.src.NetWork;

public class MPSteamworks : MonoBehaviour {
	private Lobby _currentLobby;

	// 网络管理器(只做连接，不做业务逻辑)
	private SocketManager _socketManager;
	private Dictionary<SteamId, ConnectionManager> _outgoingConnections = new Dictionary<SteamId, ConnectionManager>();
	private Dictionary<SteamId, Connection> _allConnections = new Dictionary<SteamId, Connection>();

	// 消息队列
	private ConcurrentQueue<NetworkMessage> _messageQueue = new ConcurrentQueue<NetworkMessage>();

	// 获取当前大厅ID
	public ulong CurrentLobbyId {
		get { return _currentLobby.Id.Value; }
	}
	// 检查是否在房间中
	public bool IsInLobby {
		get { return _currentLobby.Id.IsValid; }
	}

	void Awake() {

		Initialize();

		// 订阅发送事件
		SteamNetworkEvents.OnBroadcast += HandleBroadcast;
		SteamNetworkEvents.OnSendToPeer += HandleSendToPeer;
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
		SteamNetworkEvents.OnBroadcast -= HandleBroadcast;
		SteamNetworkEvents.OnSendToPeer -= HandleSendToPeer;

		DisconnectAll();
	}

	/// <summary>
	/// 初始化(只做网络层初始化)
	/// </summary>
	private void Initialize() {
		try {
			if (!SteamClient.IsValid) {
				MPMain.Logger.LogError("[MP Mod MPSteamworks] Steamworks初始化失败！");
				return;
			}

			MPMain.Logger.LogInfo($"[MP Mod MPSteamworks] Steamworks初始化成功！玩家: {SteamClient.Name} ID: {SteamClient.SteamId.Value.ToString()}");

			// 订阅大厅事件(只做事件转发)
			// 本机加入大厅
			SteamMatchmaking.OnLobbyEntered += OnLobbyEntered;
			// 该用户已经加入或正在加入大厅
			SteamMatchmaking.OnLobbyMemberJoined += OnLobbyMemberJoined;
			// 该用户已离开或即将离开大厅
			SteamMatchmaking.OnLobbyMemberLeave += OnLobbyMemberLeft;
			// 该用户在未离开大厅的情况下断线
			SteamMatchmaking.OnLobbyMemberDisconnected += OnLobbyMemberDisconnected;

			// 初始化中继网络(必须调用)
			SteamNetworkingUtils.InitRelayNetworkAccess();

		} catch (Exception ex) {
			MPMain.Logger.LogError($"[MP Mod MPSteamworks] Steamworks初始化异常: {ex.Message}");
		}
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

		MPMain.Logger.LogInfo("[MP Mod MPSteamworks] 所有网络连接已断开");
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
		foreach (var connection in _allConnections.Values) {
			try {
				connection.SendMessage(data, sendType, laneIndex);
			} catch (Exception ex) {
				MPMain.Logger.LogError($"[MP Mod MPSteamworks] 广播数据异常: {ex.Message}");
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
			MPMain.Logger.LogError($"[MP Mod MPSteamworks] 单播数据异常: {ex.Message} 目标: {steamId.Value.ToString()}");
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
				// 直接转发到总线，不处理业务逻辑
				SteamNetworkEvents.TriggerReceiveSteamData(message.SenderId.Value, message.Data);
				processedCount++;
			} catch (Exception ex) {
				MPMain.Logger.LogError($"[MP Mod MPSteamworks] 转发消息异常: {ex.Message}");
			}
		}
	}

	/// <summary>
	/// 接收数据: 任意玩家->本机 / 本机->任意玩家 连接成功 -> Player(In/Out)Connected总线
	/// </summary>
	public void OnPlayerConnected(SteamId steamId, Connection connection, bool isIncoming) {
		MPMain.Logger.LogInfo($"[MP Mod MPSteamworks] 玩家连接成功: {steamId.Value.ToString()}");

		// 记录连接
		_allConnections[steamId] = connection;

		// 触发 玩家连接
		SteamNetworkEvents.TriggerPlayerConnected(steamId);
	}

	/// <summary>
	/// 接收数据: 玩家断开连接 -> PlayerDisconnected总线
	/// </summary>
	public void OnPlayerDisconnected(SteamId steamId) {
		MPMain.Logger.LogInfo($"[MP Mod MPSteamworks] 玩家断开连接: {steamId.Value.ToString()}");

		SteamNetworkEvents.TriggerPlayerDisconnected(steamId);
		// 清理连接
		_outgoingConnections.Remove(steamId);
		_allConnections.Remove(steamId);
	}

	/// <summary>
	/// 处理主机请求现有玩家列表
	/// </summary>
	private void HandleHostRequestExistingPlayers(SteamId newPlayerSteamId) {
		if (!MultiPlayerCore.Instance.IsHost) return;

		// 获取所有连接中的SteamId(除了新玩家)
		var existingSteamIds = new List<SteamId>();
		foreach (var steamId in _allConnections.Keys) {
			if (steamId != newPlayerSteamId && steamId != SteamClient.SteamId) {
				existingSteamIds.Add(steamId);
			}
		}
		// 这里只是记录，实际转换在Core中完成
		MPMain.Logger.LogInfo($"[MP Mod MPSteamworks] 主机获取到 {existingSteamIds.Count} 个现有玩家连接");
	}

	/// <summary>
	/// 连接到指定玩家(纯网络连接，不处理业务逻辑)
	/// </summary>
	public void ConnectToPlayer(SteamId steamId) {
		try {
			if (_outgoingConnections.ContainsKey(steamId) || _allConnections.ContainsKey(steamId)) {
				MPMain.Logger.LogWarning($"[MP Mod MPSteamworks] 已经连接到玩家: {steamId.Value.ToString()}");
				return;
			}

			var connectionManager = SteamNetworkingSockets.ConnectRelay<SteamConnectionManager>(steamId, 0);
			_outgoingConnections[steamId] = connectionManager;
			_allConnections[steamId] = connectionManager.Connection;

			MPMain.Logger.LogInfo($"[MP Mod MPSteamworks] 正在连接玩家: {steamId.Value.ToString()}");
		} catch (Exception ex) {
			MPMain.Logger.LogError($"[MP Mod MPSteamworks] 连接玩家异常: {ex.Message}");
		}
	}

	/// <summary>
	/// 纯测试 会卡死主进程
	/// </summary>
	/// <param name="maxPlayers"></param>
	public void TestLobbyCreationSync(int maxPlayers) {
		if (!SteamClient.IsValid) return;
		try {
			Lobby? lobbyResult = SteamMatchmaking.CreateLobbyAsync(maxPlayers).Result;
			if (lobbyResult.HasValue) {
				MPMain.Logger.LogInfo("[MP Mod TEST] 同步创建成功!");
			} else {
				MPMain.Logger.LogError("[MP Mod TEST] 同步创建失败: 结果为空");
			}
		} catch (Exception ex) {
			MPMain.Logger.LogError($"[MP Mod TEST] 同步创建抛出 C# 异常: {ex.Message}");
			// 如果这里捕获到异常，请记录并报告！
		}
	}
	/// <summary>
	/// 创建房间(主机模式)- 异步版本
	/// </summary>
	public async Task<bool> CreateRoomAsync(string roomName, int maxPlayers) {
		// 清理全部连接
		DisconnectAll();
		//await Task.Yield();

		try {
			if (!SteamClient.IsValid) {
				MPMain.Logger.LogError("[MP Mod MPSteamworks] SteamClient 无效");
				return false;
			}

			// 核心：直接 await 任务
			Lobby? lobbyResult = await SteamMatchmaking.CreateLobbyAsync(maxPlayers);

			// 只检查结果并返回，移除所有同步大厅设置和 Socket 创建！
			if (!lobbyResult.HasValue) {
				MPMain.Logger.LogError("[MP Mod MPSteamworks] 创建房间失败: 结果为空");
				return false;
			}

			_currentLobby = lobbyResult.Value;

			MPMain.Logger.LogInfo($"[MP Mod MPSteamworks] 房间创建成功，ID: {_currentLobby.Id.Value.ToString()}");

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
				MPMain.Logger.LogError($"[MP Mod MPSteamworks] 创建Socket失败: {socketEx.Message}");
			}

			return true; // 成功
		} catch (Exception ex) {
			MPMain.Logger.LogError($"[MP Mod MPSteamworks] 创建房间异常: {ex.Message}");
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
				throw new Exception($"Steam Lobby join failed: {result.ToString()}");
			}

			_currentLobby = lobby;
			string roomName = _currentLobby.GetData("name") ?? "未知房间";
			MPMain.Logger.LogInfo($"[MP Mod MPSteamworks] 加入房间成功: {roomName}");

			// 获取Socket
			try {
				_socketManager = SteamNetworkingSockets.CreateRelaySocket<SteamSocketManager>();
			} catch (Exception socketEx) {
				MPMain.Logger.LogError($"[MP Mod MPSteamworks] 创建Socket失败: {socketEx.Message}");
			}

			return true;

		} catch (Exception ex) {
			MPMain.Logger.LogError($"[MP Mod MPSteamworks] 加入房间异常: {ex.Message}");
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
	public async Task<bool> ConnectToPlayerAsync(SteamId playerId) {
		ConnectionManager connectionManager = null;
		float timeout = 5f;
		float startTime = Time.time;

		// 初始检查
		if (_outgoingConnections.ContainsKey(playerId) || _allConnections.ContainsKey(playerId)) {
			MPMain.Logger.LogWarning($"[MP Mod MPSteamworks] 已经连接到玩家: {playerId.Value.ToString()}");
			return true;
		}

		// 1. 同步建立连接
		try {
			MPMain.Logger.LogInfo($"[MP Mod MPSteamworks] 正在连接玩家: {playerId.Value.ToString()}");

			// 建立连接
			connectionManager = SteamNetworkingSockets.ConnectRelay<SteamConnectionManager>(playerId, 0);
			_outgoingConnections[playerId] = connectionManager;
			_allConnections[playerId] = connectionManager.Connection;

		} catch (Exception ex) {
			MPMain.Logger.LogError($"[MP Mod MPSteamworks] 建立连接异常: {ex.Message}");
			return false;
		}

		// 2. 异步等待连接建立
		if (connectionManager != null) {
			while (connectionManager.ConnectionInfo.State != ConnectionState.Connected) {
				if (Time.time - startTime > timeout) {
					MPMain.Logger.LogError($"[MP Mod MPSteamworks] 连接玩家超时: {playerId.Value.ToString()}");
					_outgoingConnections.Remove(playerId);
					_allConnections.Remove(playerId);
					return false;
				}
				// 替换 yield return null
				await Task.Yield();
			}
		} else {
			return false;
		}

		MPMain.Logger.LogInfo($"[MP Mod MPSteamworks] 连接玩家成功: {playerId.Value.ToString()}");
		return true;
	}

	/// <summary>
	/// 这是一个通用的辅助方法，用于将 async Task<bool> 包装到 Unity 的 StartCoroutine 中，
	/// 并将结果传递给 Action<bool> 回调.
	/// </summary>
	private IEnumerator RunAsync(Task<bool> task, Action<bool> callback) {
		// 等待 Task 完成
		yield return new WaitWhile(() => !task.IsCompleted);

		// 强制等待一帧，确保 Task 内部的上下文完全释放
		yield return null;

		if (task.IsFaulted) {
			MPMain.Logger.LogError($"[MP Mod MPSteamworks] 异步任务执行失败: {task.Exception.InnerException.Message}");
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
		MPMain.Logger.LogInfo($"[MP Mod MPSteamworks] 进入大厅: {lobby.Id.Value.ToString()}");

		// 发布事件到总线
		SteamNetworkEvents.TriggerLobbyEntered(lobby);
	}

	/// <summary>
	/// 接收数据: 大厅有成员加入->LobbyMemberJoined总线->连接新玩家
	/// </summary>
	private void OnLobbyMemberJoined(Lobby lobby, Friend friend) {
		if (lobby.Id == _currentLobby.Id) {
			MPMain.Logger.LogInfo($"[MP Mod MPSteamworks] 玩家加入房间: {friend.Name}");

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
			MPMain.Logger.LogInfo($"[MP Mod MPSteamworks] 玩家离开房间: {friend.Name}");

			// 发布事件到总线
			SteamNetworkEvents.TriggerLobbyMemberLeft(friend.Id);

			// 只在这里处理连接清理
			OnPlayerDisconnected(friend.Id);
		}
	}

	/// <summary>
	/// 接收数据: 大厅有成员断开连接->PlayerDisconnected总线
	/// </summary>
	private void OnLobbyMemberDisconnected(Lobby lobby, Friend friend) {
		if (lobby.Id == _currentLobby.Id) {
			MPMain.Logger.LogInfo($"[MP Mod MPSteamworks] 玩家断开连接: {friend.Name}");

			// 发布断开事件到总线
			SteamNetworkEvents.TriggerPlayerDisconnected(friend.Id);
		}
	}

	/// <summary>
	/// Steam Socket管理器 - 完全无状态
	/// </summary>
	private class SteamSocketManager : SocketManager {
		// 使用静态方法获取当前实例
		private static MPSteamworks GetInstance() {
			return GameObject.FindObjectOfType<MPSteamworks>();
		}

		// 有玩家正在接入
		public override void OnConnecting(Connection connection, ConnectionInfo info) {
			MPMain.Logger.LogInfo($"[MP Mod MPSteamworks] 玩家正在连接: {info.Identity.SteamId.Value.ToString()}");
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
			return GameObject.FindObjectOfType<MPSteamworks>();
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