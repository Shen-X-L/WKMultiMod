生物同步 HUNTER在场景切换后ID不一致(记录hunter生命周期触发) TEETH无法同步 生物移动需要平滑 NEST中部分生物的ID无法一致(僵尸)

盗版创建房间失败 ?

lobbyrestart指令

支持Lua自定义命令

投射物同步

修改死亡物品掉落 尝试同步

玩家离开时 所属物品销毁同步

玩家手持物品销毁同步

压缩玩家ID ulong->short 创建玩家ID字典类,数据储存在steamLobbyData中
分配规则??? 时间戳+(steamId hash)去除大部分冲突+LobbyData进行二次校验来进行偏移
LP组件使用压缩ID,RP中使用压缩ID+自定义玩家名字

修复捷径碰撞网格丢失

使用WKLib构建部分UI

玩家ID TMP组件变为UI显示

换个UI

UI_LobbyListPane.RefreshLobbyList换成对象池

重构NameTag为RPContainer进行挂载

他人想法:
游乐场模式击杀统计排行榜

Lua
修改mod标准格式为
Main.lua
PerkModule/
ItemModule/
Script/

PerkModule ItemModule变成不可读

可以修改AI队伍
API:生成投射物

