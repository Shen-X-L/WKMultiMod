using System;
using System.Collections.Generic;
using System.Text;

namespace WKMultiMod.src.Data;

// 数据包类型枚举 - 定义不同类型的网络消息
public enum PacketType {
	ConnectedToServer = 0,  // 连接成功通知
	SeedUpdate = 1,         // 世界种子更新
	CreatePlayer = 2,       // 创建新玩家
	RemovePlayer = 3,       // 移除玩家
	PlayerDataUpdate = 4,	// 玩家数据更新
	RequestInitData = 5,	// 请求初始化世界数据
}