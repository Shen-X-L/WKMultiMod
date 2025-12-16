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
	// 当前大厅（只读，用于查询）
	private Lobby _currentLobby;

	// 网络管理器（只做连接，不做业务逻辑）
	private SocketManager _socketManager;
	private Dictionary<SteamId, ConnectionManager> _outgoingConnections = new Dictionary<SteamId, ConnectionManager>();
	private Dictionary<SteamId, Connection> _allConnections = new Dictionary<SteamId, Connection>();

	// 消息队列
	private ConcurrentQueue<NetworkMessage> _messageQueue = new ConcurrentQueue<NetworkMessage>();

	// 辅助属性
	// 是否是主机
	public bool IsHost => _socketManager != null;
	// 获取当前大厅ID
	public ulong CurrentLobbyId {
		get { return _currentLobby.Id.Value; }
	}
	// 检查是否在房间中
	public bool IsInLobby {
		get { return _currentLobby.Id.IsValid; }
	}

	void Awake() {
		if(!SteamClient.IsValid)
			SteamClient.Init(3195790, true);

		Initialize();

		// 订阅发送事件
		SteamNetworkEvents.OnSendToHost += HandleSendToHost;
		SteamNetworkEvents.OnHostBroadcast += HandleHostBroadcast;
		SteamNetworkEvents.OnHostSendToPeer += HandleHostSendToPeer;

		// 订阅主机专用事件
		SteamNetworkEvents.OnHostRequestExistingPlayers += HandleHostRequestExistingPlayers;
	}

	void Update() {
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
		SteamNetworkEvents.OnHostBroadcast -= HandleHostBroadcast;
		SteamNetworkEvents.OnHostSendToPeer -= HandleHostSendToPeer;
		SteamNetworkEvents.OnHostRequestExistingPlayers -= HandleHostRequestExistingPlayers;

		DisconnectAll();
	}

	/// <summary>
	/// 初始化（只做网络层初始化）
	/// </summary>
	private void Initialize() {
		try {
			if (!SteamClient.IsValid) {
				MPMain.Logger.LogError("[MP Mod MPSteamworks] Steamworks初始化失败！");
				return;
			}

			MPMain.Logger.LogInfo($"[MP Mod MPSteamworks] Steamworks初始化成功！玩家: {SteamClient.Name} ID: {SteamClient.SteamId.Value.ToString()}");

			// 订阅大厅事件（只做事件转发）
			SteamMatchmaking.OnLobbyEntered += OnLobbyEntered;
			SteamMatchmaking.OnLobbyMemberJoined += OnLobbyMemberJoined;
			SteamMatchmaking.OnLobbyMemberLeave += OnLobbyMemberLeft;
			SteamMatchmaking.OnLobbyMemberDisconnected += OnLobbyMemberDisconnected;

			// 初始化中继网络（必须调用）
			SteamNetworkingUtils.InitRelayNetworkAccess();

		} catch (Exception ex) {
			MPMain.Logger.LogError($"[MP Mod MPSteamworks] Steamworks初始化异常: {ex.Message}");
		}
	}

	/// <summary>
	/// 断开所有连接（清理网络资源）
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

		// 离开大厅（如果有）
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
	/// 客机: 处理发送给主机的数据
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
	/// 主机: 处理广播给所有玩家的数据
	/// </summary>
	private void HandleHostBroadcast(byte[] data, SendType sendType, ushort laneIndex) {
		foreach (var connection in _allConnections.Values) {
			try {
				connection.SendMessage(data, sendType, laneIndex);
			} catch (Exception ex) {
				MPMain.Logger.LogError($"[MP Mod MPSteamworks] 广播数据异常: {ex.Message}");
			}
		}
	}

	/// <summary>
	/// 主机: 向特定玩家发送数据
	/// </summary>
	/// <param name="data"></param>
	/// <param name="steamId"></param>
	/// <param name="sendType"></param>
	/// <param name="laneIndex"></param>
	private void HandleHostSendToPeer(byte[] data, SteamId steamId,
		SendType sendType, ushort laneIndex) {
		try {
			_allConnections[steamId].SendMessage(data, sendType, laneIndex);
		} catch (Exception ex) {
			MPMain.Logger.LogError($"[MP Mod MPSteamworks] 单播数据异常: {ex.Message} 目标: {steamId.Value}");
		}
	}

	/// <summary>
	/// 接收网络消息并转发到总线
	/// </summary>
	public void ReceiveNetworkMessage(SteamId senderId, byte[] data) {
		_messageQueue.Enqueue(new NetworkMessage {
			SenderId = senderId,
			Data = data,
			ReceiveTime = DateTime.UtcNow
		});
	}

	/// <summary>
	/// 处理消息队列
	/// </summary>
	private void ProcessMessageQueue() {
		int processedCount = 0;
		const int maxMessagesPerFrame = 50;

		while (processedCount < maxMessagesPerFrame && _messageQueue.TryDequeue(out var message)) {
			try {
				// 直接转发到总线，不处理业务逻辑
				SteamNetworkEvents.TriggerReceiveSteamData((int)message.SenderId.Value, message.Data);
				processedCount++;
			} catch (Exception ex) {
				MPMain.Logger.LogError($"[MP Mod MPSteamworks] 转发消息异常: {ex.Message}");
			}
		}
	}

	/// <summary>
	/// 玩家连接成功 - 发布到总线
	/// </summary>
	public void OnPlayerConnected(SteamId steamId, Connection connection, bool isIncoming) {
		MPMain.Logger.LogInfo($"[MP Mod MPSteamworks] 玩家连接成功: {steamId.Value}");

		if (isIncoming) {
			// 入站连接（其他玩家连接到我的监听Socket）
			// 如果我是主机，需要通知新玩家所有现有玩家信息
			if (_socketManager != null) {
				// 发送连接成功消息给新玩家
				SendConnectionSuccessToNewPlayer(steamId);
			}
		}

		// 记录连接
		_allConnections[steamId] = connection;

		// 发布连接事件到总线
		SteamNetworkEvents.TriggerPlayerConnected(steamId);
	}

	/// <summary>
	/// 玩家断开连接 - 发布到总线
	/// </summary>
	public void OnPlayerDisconnected(SteamId playerId) {
		MPMain.Logger.LogInfo($"[MP Mod MPSteamworks] 玩家断开连接: {playerId.Value}");

		// 清理连接
		_outgoingConnections.Remove(playerId);
		_allConnections.Remove(playerId);

		// 发布断开事件到总线
		SteamNetworkEvents.TriggerPlayerDisconnected(playerId.Value);
	}

	/// <summary>
	/// 处理主机请求现有玩家列表
	/// </summary>
	private void HandleHostRequestExistingPlayers(SteamId newPlayerSteamId) {
		if (!IsHost) return;

		// 获取所有连接中的SteamId（除了新玩家）
		var existingSteamIds = new List<SteamId>();
		foreach (var steamId in _allConnections.Keys) {
			if (steamId != newPlayerSteamId && steamId != SteamClient.SteamId) {
				existingSteamIds.Add(steamId);
			}
		}

		// 需要Core将SteamId转换为PlayerId
		// 这里只是记录，实际转换在Core中完成
		MPMain.Logger.LogInfo($"[MP Mod MPSteamworks] 主机获取到 {existingSteamIds.Count} 个现有玩家连接");
	}

	/// <summary>
	/// 连接到指定玩家（纯网络连接，不处理业务逻辑）
	/// </summary>
	private void ConnectToPlayer(SteamId playerId) {
		try {
			if (_outgoingConnections.ContainsKey(playerId) || _allConnections.ContainsKey(playerId)) {
				MPMain.Logger.LogWarning($"[MP Mod MPSteamworks] 已经连接到玩家: {playerId.Value}");
				return;
			}

			var connectionManager = SteamNetworkingSockets.ConnectRelay<SteamConnectionManager>(playerId, 0);
			_outgoingConnections[playerId] = connectionManager;
			_allConnections[playerId] = connectionManager.Connection;

			MPMain.Logger.LogInfo($"[MP Mod MPSteamworks] 正在连接玩家: {playerId.Value}");
		} catch (Exception ex) {
			MPMain.Logger.LogError($"[MP Mod MPSteamworks] 连接玩家异常: {ex.Message}");
		}
	}

	/// <summary>
	/// 创建房间（主机模式）- 协程版本（内部使用异步）
	/// </summary>
	public IEnumerator CreateRoomCoroutine(string roomName, int maxPlayers, Action<bool> callback) {
		// 启动异步任务并在协程中等待
		var task = CreateRoomInternalAsync(roomName, maxPlayers, callback);

		// 等待异步任务完成
		while (!task.IsCompleted) {
			yield return null;
		}

		// 如果任务有异常，在这里处理
		if (task.IsFaulted) {
			MPMain.Logger.LogError($"[MP Mod MPSteamworks] 创建房间任务异常: {task.Exception?.Message}");
		}
	}

	/// <summary>
	/// 内部异步创建房间方法
	/// </summary>
	private async Task CreateRoomInternalAsync(string roomName, int maxPlayers, Action<bool> callback) {
		bool success = false;
		Exception exception = null;

		try {
			// 先关闭现有连接
			DisconnectAll();
			await Task.Yield(); // 确保异步后执行，让断开操作生效

			MPMain.Logger.LogInfo($"[MP Mod MPSteamworks] 正在创建房间: {roomName}");

			// 创建大厅 - 使用异步等待
			var lobbyTask = await SteamMatchmaking.CreateLobbyAsync(maxPlayers);

			if (!lobbyTask.HasValue) {
				MPMain.Logger.LogError("[MP Mod MPSteamworks] 创建房间失败");
				await InvokeOnMainThread(() => callback?.Invoke(false));
				return;
			}

			_currentLobby = lobbyTask.Value;

			// 设置大厅数据（基本配置）
			_currentLobby.SetData("name", roomName);
			_currentLobby.SetData("game_version", Application.version);
			_currentLobby.SetData("owner", SteamClient.SteamId.ToString());

			_currentLobby.SetPublic();
			_currentLobby.SetJoinable(true);
			_currentLobby.Owner = new Friend(SteamClient.SteamId);

			// 创建监听Socket（作为主机）
			try {
				_socketManager = SteamNetworkingSockets.CreateRelaySocket<SteamSocketManager>();
				MPMain.Logger.LogWarning("[MP Mod MPSteamworks] TestC");
			} catch (Exception socketEx) {
				MPMain.Logger.LogError($"[MP Mod MPSteamworks] 创建Socket失败: {socketEx.Message}");
				// 即使Socket创建失败，我们仍算房间创建成功，但网络功能可能受限
			}

			MPMain.Logger.LogInfo($"[MP Mod MPSteamworks] 房间创建成功，ID: {_currentLobby.Id.Value.ToString()}");

			// 触发大厅进入事件
			await InvokeOnMainThread(() => {
				SteamNetworkEvents.TriggerLobbyEntered(_currentLobby);
			});

			success = true;

		} catch (Exception ex) {
			exception = ex;
			MPMain.Logger.LogError($"[MP Mod MPSteamworks] 创建房间异常: {ex.Message}");
		}

		// 确保回调在主线程执行
		await InvokeOnMainThread(() => {
			callback?.Invoke(success);
		});

		if (exception != null) {
			throw new Exception("[MP Mod MPSteamworks] 创建房间失败", exception);
		}
	}

	/// <summary>
	/// 加入房间（客户端模式）- 协程版本（内部使用异步）
	/// </summary>
	public IEnumerator JoinRoomCoroutine(ulong lobbyId, Action<bool> callback) {
		// 启动异步任务并在协程中等待
		var task = JoinRoomInternalAsync(lobbyId, callback);

		// 等待异步任务完成
		while (!task.IsCompleted) {
			yield return null;
		}

		// 如果任务有异常，在这里处理
		if (task.IsFaulted) {
			MPMain.Logger.LogError($"[MP Mod MPSteamworks] 加入房间任务异常: {task.Exception?.Message}");
		}
	}

	/// <summary>
	/// 内部异步加入房间方法
	/// </summary>
	private async Task JoinRoomInternalAsync(ulong lobbyId, Action<bool> callback) {
		bool success = false;
		Exception exception = null;
		Lobby? lobbyResult = null;

		MPMain.Logger.LogInfo($"[MP Mod MPSteamworks] 正在加入房间: {lobbyId.ToString()}");

		try {
			// 1. 断开连接并等待
			DisconnectAll();
			await Task.Yield();

			// 2. 加入大厅
			var lobbyTask = await SteamMatchmaking.JoinLobbyAsync(new SteamId { Value = lobbyId });

			// 3. 检查异步结果
			if (!lobbyTask.HasValue) {
				MPMain.Logger.LogError("[MP Mod MPSteamworks] 加入房间失败: 结果为空");
				await InvokeOnMainThread(() => callback?.Invoke(false));
				return;
			}

			lobbyResult = lobbyTask;
			_currentLobby = lobbyResult.Value;

			string roomName = _currentLobby.GetData("name") ?? "未知房间";
			MPMain.Logger.LogInfo($"[MP Mod MPSteamworks] 加入房间成功: {roomName}");
			success = true;

			// 4. 连接到房主
			var hostSteamId = _currentLobby.Owner.Id;
			if (hostSteamId != SteamClient.SteamId) {
				bool connectSuccess = await ConnectToPlayerInternalAsync(hostSteamId);
				if (!connectSuccess) {
					MPMain.Logger.LogWarning($"[MP Mod MPSteamworks] 连接到房主失败，但已加入大厅");
					success = false;
				}
			}

			// 5. 触发大厅进入事件
			if (success) {
				await Task.Yield(); // 等待一帧，分离连接/回调与事件触发
				MPMain.Logger.LogInfo("[MP Mod] LOG Trigger: 准备触发大厅进入事件 (Join)");
				await InvokeOnMainThread(() => {
					SteamNetworkEvents.TriggerLobbyEntered(_currentLobby);
				});
				MPMain.Logger.LogInfo("[MP Mod] LOG Trigger: 事件触发完毕 (Join)");
			}

		} catch (Exception ex) {
			exception = ex;
			MPMain.Logger.LogError($"[MP Mod MPSteamworks] 加入房间异常: {ex.Message}");
			success = false;
		}

		// 6. 执行回调（在主线程）
		await Task.Yield();
		MPMain.Logger.LogInfo("[MP Mod] LOG Callback: 准备调用回调 (Join)");
		await InvokeOnMainThread(() => {
			callback?.Invoke(success);
		});
		MPMain.Logger.LogInfo("[MP Mod] LOG Callback: 回调调用完毕 (Join)");

		if (exception != null) {
			throw new Exception("[MP Mod MPSteamworks] 加入房间失败", exception);
		}
	}

	/// <summary>
	/// 内部异步连接玩家方法
	/// </summary>
	private async Task<bool> ConnectToPlayerInternalAsync(SteamId playerId) {
		bool success = false;
		Exception exception = null;
		ConnectionManager connectionManager = null;

		try {
			// 1. 预检查
			if (_outgoingConnections.ContainsKey(playerId) || _allConnections.ContainsKey(playerId)) {
				MPMain.Logger.LogWarning($"[MP Mod MPSteamworks] 已经连接到玩家: {playerId.Value.ToString()}");
				return true;
			}

			MPMain.Logger.LogInfo($"[MP Mod MPSteamworks] 正在连接玩家: {playerId.Value.ToString()}");

			// 2. 建立连接
			connectionManager = SteamNetworkingSockets.ConnectRelay<SteamConnectionManager>(playerId, 0);
			_outgoingConnections[playerId] = connectionManager;
			_allConnections[playerId] = connectionManager.Connection;

			// 3. 等待连接建立（带超时）
			float timeout = 5f;
			float startTime = Time.time;

			while (connectionManager.ConnectionInfo.State != ConnectionState.Connected) {
				if (Time.time - startTime > timeout) {
					MPMain.Logger.LogError($"[MP Mod MPSteamworks] 连接玩家超时: {playerId.Value.ToString()}");
					_outgoingConnections.Remove(playerId);
					_allConnections.Remove(playerId);
					return false;
				}
				await Task.Delay(10); // 每10毫秒检查一次
			}

			MPMain.Logger.LogInfo($"[MP Mod MPSteamworks] 连接玩家成功: {playerId.Value.ToString()}");
			success = true;

		} catch (Exception ex) {
			exception = ex;
			MPMain.Logger.LogError($"[MP Mod MPSteamworks] 连接玩家异常: {ex.Message}");
			success = false;
		}

		await Task.Yield(); // 等待一帧，确保安全

		if (exception != null) {
			throw new Exception($"[MP Mod MPSteamworks] 连接玩家失败: {playerId.Value.ToString()}", exception);
		}

		return success;
	}

	/// <summary>
	/// 连接到指定玩家 - 协程版本（内部使用异步）
	/// </summary>
	private IEnumerator ConnectToPlayerCoroutine(SteamId playerId, Action<bool> callback) {
		// 启动异步任务并在协程中等待
		var task = ConnectToPlayerInternalAsync(playerId);

		// 等待异步任务完成
		while (!task.IsCompleted) {
			yield return null;
		}

		// 获取结果
		bool success = task.IsCompletedSuccessfully ? task.Result : false;

		// 直接调用回调（假设已经在主线程）
		callback?.Invoke(success);

		// 如果任务有异常，在这里处理
		if (task.IsFaulted) {
			MPMain.Logger.LogError($"[MP Mod MPSteamworks] 连接玩家任务异常: {task.Exception?.Message}");
		}
	}

	/// <summary>
	/// 在主线程执行委托（Unity 需要）
	/// </summary>
	private async Task InvokeOnMainThread(Action action) {
		// 这里使用 Unity 的主线程调度器
		// 如果你有 UnityMainThreadDispatcher，可以这样使用：
		// UnityMainThreadDispatcher.Instance().Enqueue(() => action?.Invoke());

		// 如果没有，可以使用简单的 Task.Run
		await Task.Run(() => {
			// 注意：如果涉及 Unity API，必须确保在主线程执行
			// 这里假设 SteamNetworkEvents.TriggerLobbyEntered 是线程安全的
			action?.Invoke();
		});
	}
	/// <summary>
	/// 创建房间的简便方法
	/// </summary>
	public void CreateRoom(string roomName, int maxPlayers, Action<bool> callback) {
		StartCoroutine(CreateRoomCoroutine(roomName, maxPlayers, callback));
	}

	/// <summary>
	/// 加入房间的简便方法
	/// </summary>
	public void JoinRoom(ulong lobbyId, Action<bool> callback) {
		StartCoroutine(JoinRoomCoroutine(lobbyId, callback));
	}


	/// <summary>
	/// 大厅进入 - 发布到总线
	/// </summary>
	private void OnLobbyEntered(Lobby lobby) {
		_currentLobby = lobby;
		MPMain.Logger.LogInfo($"[MP Mod MPSteamworks] 进入大厅: {lobby.Id.Value}");

		// 发布事件到总线
		SteamNetworkEvents.TriggerLobbyEntered(lobby);
	}

	/// <summary>
	/// 大厅成员加入 - 发布到总线
	/// </summary>
	private void OnLobbyMemberJoined(Lobby lobby, Friend friend) {
		if (lobby.Id == _currentLobby.Id) {
			MPMain.Logger.LogInfo($"[MP Mod MPSteamworks] 玩家加入房间: {friend.Name}");

			// 发布事件到总线
			SteamNetworkEvents.TriggerLobbyMemberJoined(friend.Id);

			// 如果是主机，连接到新玩家
			if (friend.Id != SteamClient.SteamId && _socketManager != null) {
				ConnectToPlayer(friend.Id);
			}
		}
	}

	/// <summary>
	/// 大厅成员离开 - 发布到总线 TriggerLobbyMemberLeft
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
	/// 大厅成员断开连接 - 发布到总线 TriggerPlayerDisconnected
	/// </summary>
	private void OnLobbyMemberDisconnected(Lobby lobby, Friend friend) {
		if (lobby.Id == _currentLobby.Id) {
			MPMain.Logger.LogInfo($"[MP Mod MPSteamworks] 玩家断开连接: {friend.Name}");

			// 发布断开事件到总线
			SteamNetworkEvents.TriggerPlayerDisconnected(friend.Id);
		}
	}

	/// <summary>
	/// 向新玩家发送连接成功消息（仅主机需要）
	/// </summary>
	private void SendConnectionSuccessToNewPlayer(SteamId newPlayerId) {
		// 获取所有现有玩家的SteamId（除了新玩家）
		var existingPlayerIds = new List<int>();
		foreach (var steamId in _allConnections.Keys) {
			if (steamId != newPlayerId && steamId != SteamClient.SteamId) {
				existingPlayerIds.Add((int)steamId.Value);
			}
		}

		// 通过总线发送创建玩家消息给所有现有玩家
		foreach (var steamId in _allConnections.Keys) {
			if (steamId != newPlayerId) {
				var writer = new NetDataWriter();
				writer.Put((int)PacketType.CreatePlayer);
				writer.Put((int)newPlayerId.Value);
				_allConnections[steamId].SendMessage(MPDataSerializer.WriterToBytes(writer));
			}
		}

		// 发送连接成功消息给新玩家
		var successWriter = new NetDataWriter();
		successWriter.Put((int)PacketType.ConnectedToServer);
		successWriter.Put(existingPlayerIds.Count);
		foreach (var id in existingPlayerIds) {
			successWriter.Put(id);
		}
		_allConnections[newPlayerId].SendMessage(MPDataSerializer.WriterToBytes(successWriter));
	}

	/// <summary>
	/// Steam Socket管理器 - 完全无状态
	/// </summary>
	private class SteamSocketManager : SocketManager {
		// 使用静态方法获取当前实例
		private static MPSteamworks GetInstance() {
			return GameObject.FindObjectOfType<MPSteamworks>();
		}

		public override void OnConnecting(Connection connection, ConnectionInfo info) {
			MPMain.Logger.LogInfo($"[MP Mod MPSteamworks] 玩家正在连接: {info.Identity.SteamId.Value}");
			connection.Accept();
		}

		public override void OnConnected(Connection connection, ConnectionInfo info) {
			var instance = GetInstance();
			if (instance != null) {
				instance.OnPlayerConnected(info.Identity.SteamId, connection, true);
			}
		}

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

		public override void OnConnecting(ConnectionInfo info) { }

		public override void OnConnected(ConnectionInfo info) {
			var instance = GetInstance();
			if (instance != null) {
				instance.OnPlayerConnected(info.Identity.SteamId, this.Connection, false);
			}
		}

		public override void OnMessage(IntPtr data, int size, long messageNum, long recvTime, int channel) {
			var instance = GetInstance();
			if (instance != null) {
				byte[] bytes = new byte[size];
				System.Runtime.InteropServices.Marshal.Copy(data, bytes, 0, size);
				instance.ReceiveNetworkMessage(this.ConnectionInfo.Identity.SteamId, bytes);
			}
		}

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