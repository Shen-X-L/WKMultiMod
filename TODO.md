改进玩家抓握逻辑,互相抓握会弹开,会加重,互相拉取玩家会弹开,(受到锤击可能松手??)

理解物品同步代码

压缩玩家ID ulong->short 创建玩家ID字典类,数据储存在steamLobbyData中
分配规则??? 时间戳+(steamId hash)去除大部分冲突+LobbyData进行二次校验来进行偏移
LP组件使用压缩ID,RP中使用压缩ID+自定义玩家名字

岩钉被拔掉,和锤入时坐标偏移无法同步

使用WKLib构建部分UI

玩家ID TMP组件变为UI显示

记得修补TMP字体文件 按钮组件 UI_Manager.DisplayMessage

修改光亮
换个UI

UI_LobbyListPane.RefreshLobbyList换成对象池

他人想法:
游乐场模式击杀统计排行榜
自定义加载的场景死后不断连

完善反编译
M_Subregion M_Region M_Level WorldLoader M_Gamemode M_GenerationBranch CL_GameManager CL_SaveManager
