lobbyrestart指令

岩钉被拔掉,和锤入时坐标偏移无法同步

玩家手持物品销毁同步

压缩玩家ID ulong->short 创建玩家ID字典类,数据储存在steamLobbyData中
分配规则??? 时间戳+(steamId hash)去除大部分冲突+LobbyData进行二次校验来进行偏移
LP组件使用压缩ID,RP中使用压缩ID+自定义玩家名字

使用WKLib构建部分UI

玩家ID TMP组件变为UI显示

换个UI

UI_LobbyListPane.RefreshLobbyList换成对象池

重构NameTag为RPContainer进行挂载

他人想法:
游乐场模式击杀统计排行榜

Lua
API:生成投射物

