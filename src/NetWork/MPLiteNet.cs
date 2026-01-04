//using LiteNetLib;
//using LiteNetLib.Utils;
//using System.Collections.Generic;
//using UnityEngine;
//using UnityEngine.Events;
//using UnityEngine.SceneManagement;
//using WKMultiMod.Component;
//using WKMultiMod.Core;
//using static ENT_Player;
//using static WKMultiMod.Data.PlayerData;
//using Quaternion = UnityEngine.Quaternion;
//using Vector3 = UnityEngine.Vector3;

//namespace WKMultiMod.Core;

//public class MPLiteNet : MonoBehaviour {
//	public bool IsMultiplayerActive;

//	// 服务器和客户端监听器 - 处理网络事件
//	private EventBasedNetListener _serverListener;
//	private EventBasedNetListener _clientListener;
//	// 服务器和客户端管理器 - 管理网络连接
//	private NetManager _client;
//	private NetManager _server;
//	// 连接到服务器的对等端引用    
//	private NetPeer _serverPeer;

//	// 最大玩家数量
//	private int _maxPlayerCount;
//	// 玩家字典 - 存储所有玩家对象, 键为玩家ID, 值为GameObject
//	private Dictionary<int, GameObject> _remotePlayers = new Dictionary<int, GameObject>();
//	// 手部字典 - 存储所有玩家手部对象, 键为玩家ID, 值为GameObject
//	private Dictionary<int, GameObject> _remoteLeftHands = new Dictionary<int, GameObject>();
//	// 手部字典 - 存储所有玩家手部对象, 键为玩家ID, 值为GameObject
//	private Dictionary<int, GameObject> _remoteRightHands = new Dictionary<int, GameObject>();
//	// 下一个玩家ID - 用于分配唯一的玩家标识符
//	private int _nextPlayerId = 0;
//	// 世界种子 - 用于同步游戏世界生成
//	public int WorldSeed { get; private set; }

//	// 数据包类型枚举 - 定义不同类型的网络消息
//	enum PacketType {
//		PlayerTransformUpdate = 0,  // 位置和旋转更新
//		HandTransformUpdate = 1,    // 手部位置和旋转更新
//		ConnectedToServer = 2,  // 连接成功通知
//		SeedUpdate = 3,         // 世界种子更新
//		CreatePlayer = 4,       // 创建新玩家
//		RemovePlayer = 5,       // 移除玩家
//	}

//	// 注意：日志通过 MPMain.Logger 访问

//	void Awake() {
//		MPMain.Logger.LogInfo("[loading] MultiplayerCore Awake");

//		// 初始化网络监听器和管理器
//		_serverListener = new EventBasedNetListener();
//		_server = new NetManager(_serverListener);
//		_clientListener = new EventBasedNetListener();
//		_client = new NetManager(_clientListener);

//		// 订阅场景加载事件, 用于执行依赖于场景的操作(如命令注册)
//		SceneManager.sceneLoaded += OnSceneLoaded;
//	}

//	private void Start() {
//		MPMain.Logger.LogInfo("[loading] MultiplayerCore Start");
//	}

//	private void Update() {
//		// 恢复网络事件轮询
//		if (_client != null) _client.PollEvents();
//		if (_server != null && _server.IsRunning) _server.PollEvents();

//		// 如果已连接到服务器, 持续更新位置. 
//		if (_serverPeer != null && ENT_Player.GetPlayer() != null) {
//			UpdatePlayerTransform();
//			UpdateHandTransform();
//		}
//	}

//	// 客户端: 更新本地玩家位置并发送到服务器
//	private void UpdatePlayerTransform() {
//		NetDataWriter writer = new NetDataWriter();
//		writer.Put((int)PacketType.PlayerTransformUpdate);

//		// 获取本地玩家位置和旋转
//		Vector3 playerPosition = ENT_Player.GetPlayer().transform.position;
//		Vector3 playerRotation = ENT_Player.GetPlayer().transform.eulerAngles;

//		// 写入位置和旋转数据
//		writer.Put(playerPosition.x);
//		writer.Put(playerPosition.y);
//		writer.Put(playerPosition.z);
//		writer.Put(playerRotation.x);
//		writer.Put(playerRotation.y);
//		writer.Put(playerRotation.z);

//		// 发送到服务器
//		_serverPeer.Send(writer, DeliveryMethod.ReliableOrdered);
//	}

//	private void UpdateHandTransform() {
//		NetDataWriter writer = new NetDataWriter();
//		writer.Put((int)PacketType.HandTransformUpdate);
//		ENT_Player.Hand leftHand = ENT_Player.GetPlayer().hands[0];
//		ENT_Player.Hand rightHand = ENT_Player.GetPlayer().hands[1];
//		writer.Put(leftHand.IsFree());
//		writer.Put(rightHand.IsFree());
//		if (!leftHand.IsFree()) {
//			// 获取本地玩家手部位置
//			Vector3 LeftPosition = leftHand.GetHoldWorldPosition();
//			writer.Put(LeftPosition.x);
//			writer.Put(LeftPosition.y);
//			writer.Put(LeftPosition.z);
//		}
//		if (!rightHand.IsFree()) {
//			// 获取本地玩家手部位置
//			Vector3 RightPosition = rightHand.GetHoldWorldPosition();
//			writer.Put(RightPosition.x);
//			writer.Put(RightPosition.y);
//			writer.Put(RightPosition.z);
//		}

//		// 发送到服务器
//		_serverPeer.Send(writer, DeliveryMethod.ReliableOrdered);
//	}

//	// 场景加载完成时调用
//	private void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
//		MPMain.Logger.LogInfo("[] 核心场景加载完成: " + scene.name);

//		if (scene.name == "Game-Main") {
//			// 注册命令和初始化世界数据
//			if (CommandConsole.instance != null) {
//				InitializeData();
//				RegisterCommands();
//			} else {
//				MPMain.Logger.LogError("[] 场景加载后 CommandConsole 实例仍为 null, 无法注册命令.");
//			}
//		}
//		if (scene.name == "Main-Menu") {
//			// 返回主菜单时关闭连接
//			CloseAllConnections();
//		}
//	}

//	// 当核心对象被销毁时调用
//	void OnDestroy() {
//		// 核心对象被销毁时的清理工作
//		MPMain.Logger.LogError("[loading] MultiplayerCore 被销毁");
//		SceneManager.sceneLoaded -= OnSceneLoaded;

//		// 关闭网络连接
//		CloseAllConnections();
//	}

//	// 命令注册
//	private void RegisterCommands() {
//		// 将命令注册到 CommandConsole
//		CommandConsole.AddCommand("host", Host);
//		CommandConsole.AddCommand("join", Join);
//		CommandConsole.AddCommand("leave", Leave);
//		MPMain.Logger.LogInfo("[loading] 命令集 注册成功");
//	}

//	// 初始化数据
//	private void InitializeData() {
//		_maxPlayerCount = 4;
//		//players.Clear();
//	}

//	// 关闭所有连接
//	private void CloseAllConnections() {
//		// 如果服务器正在运行, 断开所有连接
//		if (_server != null) {
//			// 取消订阅服务器事件
//			_serverListener.ConnectionRequestEvent -= HandleConnectionRequest;
//			_serverListener.PeerConnectedEvent -= HandlePeerConnected;
//			_serverListener.NetworkReceiveEvent -= HandleNetworkReceive;
//			_serverListener.PeerDisconnectedEvent -= HandlePeerDisconnected;

//			// 断开所有客户端连接
//			_server.DisconnectAll();

//			// 停止服务器
//			if (_server.IsRunning) {
//				_server.Stop();
//			}

//			MPMain.Logger.LogInfo("[Close] 服务器连接已停止.");
//		}

//		// 断开客户端连接
//		if (_client != null) {
//			// 取消订阅客户端事件
//			_clientListener.NetworkReceiveEvent -= HandleClientNetworkReceive;

//			// 断开与服务器的连接
//			_client.DisconnectAll();

//			// 停止客户端
//			if (_client.IsRunning) {
//				_client.Stop();
//			}

//			MPMain.Logger.LogInfo("[Close] 客户端连接已停止.");
//		}

//		_serverPeer = null; // 重置对等端引用

//		// 销毁所有玩家对象
//		foreach (KeyValuePair<int, GameObject> player in _remotePlayers) {
//			if (player.Value != null) {
//				Destroy(player.Value);
//			}
//		}

//		_remotePlayers.Clear();
//		_remoteLeftHands.Clear();
//		_remoteRightHands.Clear();

//		// 重置多人游戏活动标志
//		IsMultiplayerActive = false;
//	}

//	// 服务器端：处理连接请求
//	private void HandleConnectionRequest(ConnectionRequest request) {
//		if (_server.ConnectedPeersCount < _maxPlayerCount) {
//			request.Accept();
//		} else {
//			request.Reject();
//		}
//	}

//	// 服务器端：处理新客户端连接
//	private void HandlePeerConnected(NetPeer peer) {
//		peer.Tag = _nextPlayerId;
//		_nextPlayerId++;

//		MPMain.Logger.LogInfo("[server] 新客户端已连接: ID= " + peer.Tag.ToString());
//		//CommandConsole.Log("We got connection: " + peer.Tag);
//		CommandConsole.Log("We got new connection");

//		// 发送连接成功消息
//		SendConnectionSuccessMessage(peer);

//		// 发送世界种子信息
//		SendWorldSeedToPeer(peer);

//		// 通知所有客户端创建新玩家
//		NotifyAllClientsToCreatePlayer(peer);
//	}

//	// 服务器端：发送连接成功消息给新客户端
//	private void SendConnectionSuccessMessage(NetPeer peer) {
//		NetDataWriter writer = new NetDataWriter();
//		writer.Put((int)PacketType.ConnectedToServer);
//		writer.Put(_server.ConnectedPeersCount - 1);

//		foreach (NetPeer connectedPeer in _server.ConnectedPeerList) {
//			if ((int)connectedPeer.Tag == (int)peer.Tag) continue;
//			writer.Put((int)connectedPeer.Tag);
//		}

//		peer.Send(writer, DeliveryMethod.ReliableOrdered);
//	}

//	// 服务器端：发送世界种子给指定客户端
//	private void SendWorldSeedToPeer(NetPeer peer) {
//		NetDataWriter writer = new NetDataWriter();
//		writer.Put((int)PacketType.SeedUpdate);
//		writer.Put(WorldLoader.instance.seed);
//		peer.Send(writer, DeliveryMethod.ReliableOrdered);
//	}

//	// 服务器端：通知所有客户端创建新玩家
//	private void NotifyAllClientsToCreatePlayer(NetPeer peer) {
//		NetDataWriter writer = new NetDataWriter();
//		writer.Put((int)PacketType.CreatePlayer);
//		writer.Put((int)peer.Tag);
//		_server.SendToAll(writer, DeliveryMethod.ReliableOrdered, peer);
//	}

//	// 服务器端：处理网络数据接收
//	private void HandleNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod) {
//		// 基本验证：确保数据足够读取一个整数(数据包类型)
//		if (reader.AvailableBytes < 4) {
//			reader.Recycle();
//			return;
//		}

//		int packetType = reader.GetInt();

//		switch (packetType) {
//			case (int)PacketType.PlayerTransformUpdate:
//				ForwardPlayerTransformUpdate(peer, reader);
//				break;

//			case (int)PacketType.HandTransformUpdate:
//				ForwardHandTransformUpdate(peer, reader);
//				break;
//		}

//		reader.Recycle();
//	}

//	// 服务器端：转发位置更新给所有其他客户端
//	private void ForwardPlayerTransformUpdate(NetPeer peer, NetPacketReader reader) {
//		NetDataWriter writer = new NetDataWriter();
//		writer.Put((int)PacketType.PlayerTransformUpdate);
//		writer.Put((int)peer.Tag);
//		writer.Put(reader.GetRemainingBytes());
//		_server.SendToAll(writer, DeliveryMethod.ReliableOrdered, peer);
//	}

//	// 服务器端: 转发手部位置更新给所有其他客户端
//	private void ForwardHandTransformUpdate(NetPeer peer, NetPacketReader reader) {
//		NetDataWriter writer = new NetDataWriter();
//		writer.Put((int)PacketType.HandTransformUpdate);
//		writer.Put((int)peer.Tag);
//		writer.Put(reader.GetRemainingBytes());
//		_server.SendToAll(writer, DeliveryMethod.ReliableOrdered, peer);
//	}

//	// 服务器端：处理客户端断开连接
//	private void HandlePeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo) {
//		int disconnectedPlayerId = (int)peer.Tag;

//		// 主机端：销毁本地的远程玩家代理对象
//		if (_remotePlayers.ContainsKey(disconnectedPlayerId)) {
//			Destroy(_remotePlayers[disconnectedPlayerId]);
//			_remotePlayers.Remove(disconnectedPlayerId);
//			MPMain.Logger.LogInfo("[server] 主机已移除远程玩家 ID: " + disconnectedPlayerId);
//		}

//		// 通知所有剩余的客户端移除该玩家
//		NotifyAllClientsToRemovePlayer(disconnectedPlayerId);
//	}

//	// 服务器端：通知所有客户端移除玩家
//	private void NotifyAllClientsToRemovePlayer(int playerId) {
//		NetDataWriter writer = new NetDataWriter();
//		writer.Put((int)PacketType.RemovePlayer);
//		writer.Put(playerId);
//		_server.SendToAll(writer, DeliveryMethod.ReliableOrdered);
//	}

//	// 客户端：处理接收到的网络数据
//	private void HandleClientNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod) {
//		// 基本验证：确保数据足够读取一个整数(数据包类型)
//		if (reader.AvailableBytes < 4) {
//			reader.Recycle();
//			return;
//		}

//		int packetType = reader.GetInt();

//		switch (packetType) {
//			case (int)PacketType.PlayerTransformUpdate:
//				HandlePlayerTransformUpdate(reader);
//				break;

//			case (int)PacketType.HandTransformUpdate:
//				HandleHandTransformUpdate(reader);
//				break;

//			case (int)PacketType.ConnectedToServer:
//				HandleConnectionSuccess(reader);
//				break;

//			case (int)PacketType.SeedUpdate:
//				HandleSeedUpdate(reader);
//				break;

//			case (int)PacketType.CreatePlayer:
//				HandleCreatePlayer(reader);
//				break;

//			case (int)PacketType.RemovePlayer:
//				HandleRemovePlayer(reader);
//				break;
//		}

//		reader.Recycle();
//	}

//	// 客户端：处理其他玩家的位置更新
//	private void HandlePlayerTransformUpdate(NetPacketReader reader) {
//		int playerId = reader.GetInt();
//		Vector3 newPosition = new Vector3(
//			reader.GetFloat(),
//			reader.GetFloat(),
//			reader.GetFloat()
//		);
//		Vector3 newRotation = new Vector3(
//			reader.GetFloat(),
//			reader.GetFloat(),
//			reader.GetFloat()
//		);

//		// 没有该玩家则忽略
//		if (!_remotePlayers.ContainsKey(playerId)) return;

//		// 更新玩家位置和旋转
//		RemotePlayerComponent player = _remotePlayers[playerId].GetComponent<RemotePlayerComponent>();
//		player.UpdatePosition(newPosition);
//		player.UpdateRotation(newRotation);
//	}

//	// 客户端：处理其他玩家手部位置更新
//	private void HandleHandTransformUpdate(NetPacketReader reader) {
//		int playerId = reader.GetInt();
//		bool isLeftFree = reader.GetBool();
//		bool isRightFree = reader.GetBool();
//		Vector3 leftLocalPosition = new Vector3(-0.4f, 0.5f, 0.4f);
//		Vector3 rightLocalPosition = new Vector3(0.4f, 0.5f, 0.4f);
//		if (!isLeftFree) {
//			// 左手世界坐标
//			Vector3 leftWorldPosition = new Vector3(
//				reader.GetFloat(),
//				reader.GetFloat(),
//				reader.GetFloat()
//			);
//			// 转为局部坐标
//			leftLocalPosition = _remotePlayers[playerId].transform.InverseTransformPoint(leftWorldPosition);
//		}
//		if (!isRightFree) {
//			// 右手世界坐标
//			Vector3 rightWorldPosition = new Vector3(
//				reader.GetFloat(),
//				reader.GetFloat(),
//				reader.GetFloat()
//			);
//			// 转为局部坐标
//			rightLocalPosition = _remotePlayers[playerId].transform.InverseTransformPoint(rightWorldPosition);
//		}
//		// 没有该玩家则忽略
//		if (!_remoteLeftHands.ContainsKey(playerId) || !_remoteRightHands.ContainsKey(playerId)) return;
//		// 更新手部位置
//		RemoteHandComponent leftHand = _remoteLeftHands[playerId].GetComponent<RemoteHandComponent>();
//		leftHand.SetDefaultLocalPosition(leftLocalPosition);
//		RemoteHandComponent rightHand = _remoteRightHands[playerId].GetComponent<RemoteHandComponent>();
//		rightHand.SetDefaultLocalPosition(rightLocalPosition);
//	}

//	// 客户端：处理连接成功消息
//	private void HandleConnectionSuccess(NetPacketReader reader) {
//		int peerCount = reader.GetInt();
//		MPMain.Logger.LogInfo("[client] 已连接, 正在加载 " + peerCount.ToString() + " 玩家");
//		CommandConsole.Log(
//			"Connected!\nCreating "
//			+ peerCount
//			+ " player instance(s).");

//		for (int i = 0; i < peerCount; i++) {
//			CreateRemotePlayer(reader.GetInt());
//		}
//	}

//	// 客户端：处理加载世界种子
//	private void HandleSeedUpdate(NetPacketReader reader) {
//		WorldSeed = reader.GetInt();
//		MPMain.Logger.LogInfo("[client] 加载世界, 种子号: " + WorldSeed.ToString());
//		WorldLoader.ReloadWithSeed(new string[] { WorldSeed.ToString() });
//	}

//	// 客户端：处理创建玩家消息
//	private void HandleCreatePlayer(NetPacketReader reader) {
//		int playerId = reader.GetInt();
//		CreateRemotePlayer(playerId);
//	}

//	// 客户端：处理移除玩家消息
//	private void HandleRemovePlayer(NetPacketReader reader) {
//		int playerIdToRemove = reader.GetInt();

//		if (_remotePlayers.ContainsKey(playerIdToRemove)) {
//			Destroy(_remotePlayers[playerIdToRemove]);
//			_remotePlayers.Remove(playerIdToRemove);
//			_remoteLeftHands.Remove(playerIdToRemove);
//			_remoteRightHands.Remove(playerIdToRemove);
//			MPMain.Logger.LogInfo("[client] 客户端已移除远程玩家: ID=" + playerIdToRemove);
//		}
//	}


//	// 命令实现
//	public void Host(string[] args) {
//		// 先关闭现有连接
//		CloseAllConnections();

//		if (_server == null) {
//			MPMain.Logger.LogError("[server] 服务器管理器不存在");
//			return;
//		}

//		// 修复：检查参数长度, 防止 IndexOutOfRangeException
//		if (args.Length < 1) {
//			CommandConsole.LogError(
//				"Usage: host <port> [max_players]\nExample: host 22222");
//			return;
//		}

//		ushort port = ushort.Parse(args[0]);

//		if (args.Length >= 2) {
//			_maxPlayerCount = int.Parse(args[1]);
//		} else {
//			_maxPlayerCount = 4; // 默认值
//		}

//		_server.Start(port); // 在指定端口启动服务器

//		// 订阅服务器事件
//		_serverListener.ConnectionRequestEvent += HandleConnectionRequest;
//		_serverListener.PeerConnectedEvent += HandlePeerConnected;
//		_serverListener.NetworkReceiveEvent += HandleNetworkReceive;
//		_serverListener.PeerDisconnectedEvent += HandlePeerDisconnected;

//		// 主机作为客户端连接到自己的服务器
//		Join(["127.0.0.1", port.ToString()]);

//		MPMain.Logger.LogInfo("[server] 已创建服务端");

//		CommandConsole.Log("Hosting lobby...");
//		CommandConsole.LogError(
//			"You are a hosting a peer-to-peer lobby\n"
//			+ "By sharing your IP you are also sharing your address\n"
//			+ "Be careful... :)");
//	}

//	public void Join(string[] args) {
//		// 先取消可能存在的客户端订阅
//		_clientListener.NetworkReceiveEvent -= HandleClientNetworkReceive;

//		if (_client == null) {
//			MPMain.Logger.LogError("[client] 客户端管理器不存在");
//			return;
//		}

//		// 参数验证
//		if (args.Length < 2) {
//			CommandConsole.LogError(
//				"Usage: join <IP> <port>\n"
//				+ "Example: join 127.0.0.1 22222 or join [::1] 22222");
//			return;
//		}

//		// 解析IP地址和端口
//		string ip = args[0];
//		int port = int.Parse(args[1]);

//		// 如果客户端已经在运行, 先停止
//		if (_client.IsRunning) {
//			_client.DisconnectAll();
//			_client.Stop();
//		}

//		// 启动客户端并连接到服务器
//		_client.Start();
//		_serverPeer = _client.Connect(ip, port, "");

//		// 设置多人游戏活动标志
//		IsMultiplayerActive = true;

//		// 处理客户端接收到的网络数据
//		_clientListener.NetworkReceiveEvent += HandleClientNetworkReceive;

//		MPMain.Logger.LogInfo("[server] 尝试连接: " + ip);
//		CommandConsole.Log("Trying to join ip: " + ip);
//	}

//	public void Leave(string[] args) {
//		CloseAllConnections();
//		MPMain.Logger.LogInfo("[] 所有连接已断开, 远程玩家已清理.");
//	}

//	// 赋予可攀爬组件
//	private void CreateHandholdObject(GameObject gameObject) {
//		// 添加 ObjectTagger 组件
//		ObjectTagger tagger = gameObject.AddComponent<ObjectTagger>();
//		if (tagger != null) {
//			tagger.tags.Add("Handhold");
//		}

//		// 添加 CL_Handhold 组件 (攀爬逻辑)
//		CL_Handhold handholdComponent = gameObject.AddComponent<CL_Handhold>();
//		if (handholdComponent != null) {
//			// 添加停止和激活事件
//			handholdComponent.stopEvent = new UnityEvent();
//			handholdComponent.activeEvent = new UnityEvent();
//		}

//		// 确保 渲染器 被赋值, 否则 材质 设置会崩溃
//		Renderer objectRenderer = gameObject.GetComponent<Renderer>();
//		if (objectRenderer != null) {
//			gameObject.GetComponent<CL_Handhold>().handholdRenderer = objectRenderer;
//		}
//	}

//	// 创建远程玩家对象
//	private GameObject CreateMultiPlayerObject(int tag) {
//		// 输出创建信息
//		MPMain.Logger.LogInfo("[create] 创建玩家中" + tag);

//		// 创建玩家游戏对象
//		GameObject player = GameObject.CreatePrimitive(PrimitiveType.Capsule);
//		player.name = "RemotePlayer_" + tag;

//		// 设置碰撞器为触发器 (Collider/Trigger)
//		CapsuleCollider playerCollider = player.GetComponent<CapsuleCollider>();
//		if (playerCollider != null) {
//			playerCollider.isTrigger = true;
//			// 调整尺寸 (胶囊体高2.0, 宽0.5, 中心在0.0)
//			playerCollider.radius = 0.5f;
//			playerCollider.height = 2.0f;
//		}

//		// 设置碰撞体可以被攀爬
//		CreateHandholdObject(player);

//		// 添加第二个碰撞器 - 用于物理碰撞
//		CapsuleCollider physicsCollider = player.AddComponent<CapsuleCollider>();
//		physicsCollider.isTrigger = false;  // 不是触发器,用于物理阻挡
//		physicsCollider.radius = 0.4f;     // 稍微小一点,避免重叠问题
//		physicsCollider.height = 1.8f;
//		physicsCollider.center = new Vector3(0, 0.1f, 0); // 轻微偏移避免完全重叠

//		// 添加 远程玩家 组件以处理位置和旋转更新
//		RemotePlayerComponent playerComponent = player.AddComponent<RemotePlayerComponent>();

//		// 设置材质
//		Material bodyMaterial = new Material(Shader.Find("Unlit/Color"));
//		bodyMaterial.color = Color.gray;
//		player.GetComponent<Renderer>().material = bodyMaterial;

//		// 创建眼睛子对象
//		GameObject leftEye = GameObject.CreatePrimitive(PrimitiveType.Sphere);
//		leftEye.name = "RemotePlayer_LeftEye_" + tag;
//		leftEye.transform.SetParent(player.transform);
//		leftEye.transform.localScale = new Vector3(0.2f, 0.2f, 0.2f);
//		leftEye.transform.localPosition = new Vector3(-0.15f, 0.5f, 0.45f);
//		GameObject rightEye = GameObject.CreatePrimitive(PrimitiveType.Sphere);
//		rightEye.name = "RemotePlayer_RightEye_" + tag;
//		rightEye.transform.SetParent(player.transform);
//		rightEye.transform.localScale = new Vector3(0.2f, 0.2f, 0.2f);
//		rightEye.transform.localPosition = new Vector3(0.15f, 0.5f, 0.45f);

//		// 将玩家添加到字典中
//		_remotePlayers.Add(tag, player);

//		// 设置为不销毁
//		DontDestroyOnLoad(player);

//		// 输出创建成功信息
//		MPMain.Logger.LogInfo("[create] 创建玩家成功 ID:" + tag);

//		// 返回创建的玩家对象
//		return player;
//	}

//	// 创建远程玩家手部对象
//	private (GameObject leftHand, GameObject rightHand) CreateMultiHandObject(int tag) {
//		// 输出创建信息
//		MPMain.Logger.LogInfo("[create] 创建手部中" + tag);

//		// 创建左手
//		GameObject leftHand = GameObject.CreatePrimitive(PrimitiveType.Sphere);
//		leftHand.name = $"RemotePlayer_LeftHand_" + tag;
//		leftHand.transform.localScale = new Vector3(0.2f, 0.2f, 0.2f);
//		// 创建右手
//		GameObject rightHand = GameObject.CreatePrimitive(PrimitiveType.Sphere);
//		rightHand.name = $"RemotePlayer_RightHand_" + tag;
//		rightHand.transform.localScale = new Vector3(0.2f, 0.2f, 0.2f);

//		// 设置碰撞体为触发器 (Collider/Trigger)
//		SphereCollider leftCollider = leftHand.GetComponent<SphereCollider>();
//		if (leftCollider != null) {
//			leftCollider.isTrigger = true;
//			// 调整尺寸 
//			leftCollider.radius = 0.2f;
//		}
//		SphereCollider rightCollider = rightHand.GetComponent<SphereCollider>();
//		if (rightCollider != null) {
//			rightCollider.isTrigger = true;
//			// 调整尺寸 
//			rightCollider.radius = 0.2f;
//		}

//		// 设置碰撞体可以被攀爬
//		CreateHandholdObject(leftHand);
//		CreateHandholdObject(rightHand);

//		// 添加 远程玩家手部 组件以处理位置更新
//		RemoteHandComponent leftComponent = leftHand.AddComponent<RemoteHandComponent>();
//		leftComponent.hand = HandType.Left;
//		RemoteHandComponent rightComponent = rightHand.AddComponent<RemoteHandComponent>();
//		rightComponent.hand = HandType.Right;

//		// 设置材质
//		Material leftHandMaterial = new Material(Shader.Find("Unlit/Color"));
//		leftHandMaterial.color = Color.white; // 设置为白色
//		leftHand.GetComponent<Renderer>().material = leftHandMaterial;
//		Material rightHandMaterial = new Material(Shader.Find("Unlit/Color"));
//		rightHandMaterial.color = Color.white; // 设置为白色
//		rightHand.GetComponent<Renderer>().material = rightHandMaterial;

//		// 将手部添加到字典中
//		_remoteLeftHands.Add(tag, leftHand);
//		_remoteRightHands.Add(tag, rightHand);

//		// 输出创建成功信息
//		MPMain.Logger.LogInfo("[create] 创建手部成功 ID:" + tag);

//		// 返回创建的手部对象
//		return (leftHand, rightHand);
//	}

//	private GameObject CreateMultiNameObject(string tag) {
//		// 创建文本子对象 (用来承载 TextMeshPro)
//		GameObject textObject = new GameObject("PlayerID_Text_" + tag);

//		// 设置文本框位置：略高于胶囊体
//		textObject.transform.localPosition = new Vector3(0f, 1.5f, 0f);
//		textObject.transform.localRotation = Quaternion.identity;

//		// 添加 TextMesh 组件
//		TextMesh textMesh = textObject.AddComponent<TextMesh>();

//		// 配置文本内容和外观
//		if (textMesh != null) {
//			textMesh.text = "Player ID: " + tag.ToString(); // 显示玩家 ID
//			textMesh.fontSize = 20;                         // TextMesh的字体大小单位不同
//			textMesh.characterSize = 0.1f;                  // 字符大小缩放
//			textMesh.anchor = TextAnchor.MiddleCenter;      // 对齐方式

//			// 设置颜色 - TextMesh自带的材质有透明通道
//			textMesh.color = new Color(1f, 1f, 1f, 0.85f);  // 白色,85%透明度

//			// 但你可以设置一个深色背景来提高可读性：
//			textMesh.fontStyle = FontStyle.Bold;            // 加粗

//			// 设置字体
//			textMesh.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
//		}

//		// 添加 Billboard 组件 实现永远面向摄像机
//		textObject.AddComponent<LootAtComponent>();

//		return textObject;
//	}

//	// 创建玩家视觉表现
//	private void CreateRemotePlayer(int tag) {
//		// 创建手部对象
//		GameObject player = CreateMultiPlayerObject(tag);

//		// 创建手部对象
//		var (leftHand, rightHand) = CreateMultiHandObject(tag);
//		leftHand.transform.SetParent(player.transform);
//		rightHand.transform.SetParent(player.transform);

//		// 创建名称文本对象
//		GameObject textObject = CreateMultiNameObject(tag.ToString());
//		textObject.transform.SetParent(player.transform);

//		// 输出日志
//		CommandConsole.Log("Creating Player with Tag: " + player.GetInstanceID());
//	}
//}