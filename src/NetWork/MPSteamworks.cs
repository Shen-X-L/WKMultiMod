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

		Initialize();

		// 订阅发送事件
		SteamNetworkEvents.OnSendToHost += HandleSendToHost;
		SteamNetworkEvents.OnHostBroadcast += HandleHostBroadcast;
		SteamNetworkEvents.OnHostSendToPeer += HandleHostSendToPeer;

		// 订阅主机专用事件
		SteamNetworkEvents.OnHostRequestExistingPlayers += HandleHostRequestExistingPlayers;
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
		} catch {
			MPMain.Logger.LogError("[MP Mod TEST] 同步创建抛出未知异常!");
		}
	}

	/// <summary>
	/// 创建房间（主机模式）- 纯协程轮询版本
	/// </summary>
	public IEnumerator CreateRoomCoroutine(string roomName, int maxPlayers, Action<bool> callback) {
		bool success = false;

		// 1. 断开连接并等待一帧
		DisconnectAll();
		yield return null;

		MPMain.Logger.LogInfo($"[MP Mod MPSteamworks] 正在创建房间: {roomName}");

		// 2. 启动异步操作 (不使用 await)
		var lobbyTask = SteamMatchmaking.CreateLobbyAsync(maxPlayers);
		MPMain.Logger.LogWarning("[MP Mod MPSteamworks] TestA_Start");

		// 3. 手动协程轮询 (安全等待)
		while (!lobbyTask.IsCompleted) {
			yield return null; // 每帧检查一次
			MPMain.Logger.LogWarning("[MP Mod MPSteamworks] TestB_while");
		}
		MPMain.Logger.LogWarning("[MP Mod MPSteamworks] TestC_TaskCompleted");

		// 4. 检查结果
		if (!lobbyTask.Result.HasValue) {
			MPMain.Logger.LogError("[MP Mod MPSteamworks] 创建房间失败: 结果为空");
			callback?.Invoke(false);
			yield break;
		}
		_currentLobby = lobbyTask.Result.Value;

		// 5. 强制等待一帧，隔离 Task 完成上下文
		yield return null;
		MPMain.Logger.LogWarning("[MP Mod MPSteamworks] TestD1_PostCompletionIsolation");

		// 6. 敏感操作：设置大厅数据和创建 Socket

			// 同步设置大厅数据
			_currentLobby.SetData("name", roomName);
			_currentLobby.SetData("game_version", Application.version);
			_currentLobby.SetData("owner", SteamClient.SteamId.ToString());
			_currentLobby.SetPublic();
			_currentLobby.SetJoinable(true);
			_currentLobby.Owner = new Friend(SteamClient.SteamId);

			// 强制等待一帧，隔离 Socket 初始化
			yield return null;
		try {
			MPMain.Logger.LogWarning("[MP Mod MPSteamworks] TestD2_PreSocket");

			_socketManager = SteamNetworkingSockets.CreateRelaySocket<SteamSocketManager>();
			MPMain.Logger.LogWarning("[MP Mod MPSteamworks] TestE_SocketSuccess");

			MPMain.Logger.LogInfo($"[MP Mod MPSteamworks] 房间创建成功，ID: {_currentLobby.Id.Value.ToString()}");
			success = true;

		} catch (Exception ex) {
			MPMain.Logger.LogError($"[MP Mod MPSteamworks] 房间配置失败: {ex.Message}");
			success = false;
		}

		// 7. 事件触发 (如果需要，放在最安全的位置)
		if (success) {
			yield return null; // 隔离事件触发
			MPMain.Logger.LogWarning("[MP Mod MPSteamworks] TestF_PreTrigger");
			// SteamNetworkEvents.TriggerLobbyEntered(_currentLobby); 
			// 暂时不触发，如果成功再考虑加回
		}

		// 8. 最终回调 (隔离回调)
		yield return null;
		MPMain.Logger.LogWarning("[MP Mod MPSteamworks] TestG_PreCallback");
		callback?.Invoke(success);
		MPMain.Logger.LogWarning("[MP Mod MPSteamworks] TestH_Finished");
	}

	public async Task<bool> CreateRoomAsync(string roomName, int maxPlayers) {
		// 启动异步操作
		DisconnectAll();
		await Task.Yield();

		try {
			if (!SteamClient.IsValid) {
				MPMain.Logger.LogError("[MP Mod MPSteamworks] SteamClient 无效");
				return false;
			}

			MPMain.Logger.LogInfo($"[MP Mod MPSteamworks] 正在创建房间: {roomName}");

			// 核心：直接 await 任务
			Lobby? lobbyResult = await SteamMatchmaking.CreateLobbyAsync(maxPlayers);

			// 只检查结果并返回，移除所有同步大厅设置和 Socket 创建！
			if (!lobbyResult.HasValue) {
				MPMain.Logger.LogError("[MP Mod MPSteamworks] 创建房间失败: 结果为空");
				return false;
			}

			_currentLobby = lobbyResult.Value;

			MPMain.Logger.LogInfo($"[MP Mod MPSteamworks] 房间创建成功，ID: {_currentLobby.Id.Value.ToString()}");
			MPMain.Logger.LogWarning("[MP Mod MPSteamworks] FINAL_A_AsyncFinished");

			return true; // 成功

		} catch (Exception ex) {
			MPMain.Logger.LogError($"[MP Mod MPSteamworks] 创建房间异常: {ex.Message}");
			return false; // 失败
		}
	}

	// 重新定义 CreateRoom 公共方法
	public void CreateRoom(string roomName, int maxPlayers, Action<bool> callback) {
		// 启动新的纯协程
		//StartCoroutine(CreateRoomCoroutine(roomName, maxPlayers, callback));
		// 启动异步
		StartCoroutine(RunAsync(CreateRoomAsync(roomName, maxPlayers), callback));
	}

	/// <summary>
	/// 加入房间（客户端模式）- 异步版本
	/// </summary>
	public async Task<bool> JoinRoomAsync(Lobby lobby) {
		// 注意：我们将 ConnectToPlayerCoroutine 转换为 async Task<bool>

		try {
			// 核心改变：直接 await 任务
			RoomEnter result = await lobby.Join();

			// 检查 RoomEnter 结果
			if (result != RoomEnter.Success) {
				throw new Exception($"Steam Lobby join failed: {result}");
			}

			_currentLobby = lobby;
			string roomName = _currentLobby.GetData("name") ?? "未知房间";
			MPMain.Logger.LogInfo($"[MP Mod MPSteamworks] 加入房间成功: {roomName}");

			// 异步等待连接房主
			var hostSteamId = _currentLobby.Owner.Id;
			if (hostSteamId != SteamClient.SteamId) {
				// 替换 StartCoroutine 为 await 异步方法
				bool connectSuccess = await ConnectToPlayerAsync(hostSteamId);
				if (!connectSuccess) {
					MPMain.Logger.LogWarning($"[MP Mod MPSteamworks] 连接到房主失败，但已加入大厅");
				}
			}

			SteamNetworkEvents.TriggerLobbyEntered(_currentLobby);
			return true;

		} catch (Exception ex) {
			MPMain.Logger.LogError($"[MP Mod MPSteamworks] 加入房间异常: {ex.Message}");
			// 这里可以调用回调，但因为我们将它包装在 Task 中，由调用者处理回调更清晰
			return false;
		}
	}

	// 对应的启动方法简化
	public void JoinRoom(ulong lobbyId, Action<bool> callback) {
		Lobby lobby = new Lobby(lobbyId);
		// 使用 Unity 的扩展方法来启动 async Task
		this.StartCoroutine(RunAsync(JoinRoomAsync(lobby), callback));
	}

	/// <summary>
	/// 连接到指定玩家 - 异步版本
	/// </summary>
	private async Task<bool> ConnectToPlayerAsync(SteamId playerId) {
		ConnectionManager connectionManager = null;
		float timeout = 5f;
		float startTime = Time.time;

		// 初始检查
		if (_outgoingConnections.ContainsKey(playerId) || _allConnections.ContainsKey(playerId)) {
			MPMain.Logger.LogWarning($"[MP Mod MPSteamworks] 已经连接到玩家: {playerId.Value}");
			return true;
		}

		// 1. 同步建立连接
		try {
			MPMain.Logger.LogInfo($"[MP Mod MPSteamworks] 正在连接玩家: {playerId.Value}");

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
					MPMain.Logger.LogError($"[MP Mod MPSteamworks] 连接玩家超时: {playerId.Value}");
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

		MPMain.Logger.LogInfo($"[MP Mod MPSteamworks] 连接玩家成功: {playerId.Value}");
		return true;
	}

	// 这是一个通用的辅助方法，用于将 async Task<bool> 包装到 Unity 的 StartCoroutine 中，
	// 并将结果传递给 Action<bool> 回调。
	private IEnumerator RunAsync(Task<bool> task, Action<bool> callback) {
		// 等待 Task 完成
		yield return new WaitWhile(() => !task.IsCompleted);

		// 强制等待一帧，确保 Task 内部的上下文完全释放
		yield return null;

		MPMain.Logger.LogWarning("[MP Mod MPSteamworks] TestF2");
		if (task.IsFaulted) {
			MPMain.Logger.LogWarning("[MP Mod MPSteamworks] TestG1");
			MPMain.Logger.LogError($"[MP Mod MPSteamworks] 异步任务执行失败: {task.Exception.InnerException.Message}");
			callback?.Invoke(false);
		} else {
			MPMain.Logger.LogWarning("[MP Mod MPSteamworks] TestG2");
			// Task.Result 即为异步方法的返回值 (bool)
			callback?.Invoke(task.Result);
		}
	}

	/// <summary>
	/// 大厅进入 - 发布到总线
	/// </summary>
	private void OnLobbyEntered(Lobby lobby) {
		_currentLobby = lobby;
		MPMain.Logger.LogInfo($"[MP Mod MPSteamworks] 进入大厅: {lobby.Id.Value.ToString()}");

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
			MPMain.Logger.LogInfo("[MP Mod MPSteamworks] TestAA");
			MPMain.Logger.LogInfo($"[MP Mod MPSteamworks] 玩家正在连接: {info.Identity.SteamId.Value.ToString()}");
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