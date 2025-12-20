using System;
using System.Collections.Generic;
using System.Text;

namespace WKMultiMod.src.Data;

// 数据包类型枚举 - 定义不同类型的网络消息
public enum PacketType {
	ConnectedToServer = 0,  // 客机->主机: 请求初始化世界数据
	InitializeWorld = 1,    // 主机->客机: 接收初始化世界数据,创建玩家,重加载地图
	CreatePlayer = 2,       // 主机->客机: 创建新玩家
	RemovePlayer = 3,       // 主机->客机: 移除玩家
	PlayerDataUpdate = 4,   // 客机->主机->客机: 玩家数据更新
}