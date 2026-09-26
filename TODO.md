远程钻头没有被攀爬物同步

远程生物受伤后仇恨索敌未同步

仅有吐射动画,没有射击物

重构为EnemySyncModule->NetworkGameEntity->INetSerializable子类

僵尸索敌坐标错误

DEN_Teeth.Awake AIC_Teeth_Chase.KillPlayerAnimation
DEN_Face.Start .Damage.HitShift
DEN_Hunter.TrackPlayerPosition .Start .TargetCollisionCheck
DEN_EngravedDoor
DEN_LadderNightmare.Update 
DEN_Mother.Damage 
DEN_VentThing.SetTarget
只索敌主玩家

HUNTER在场景切换后ID不一致(记录hunter生命周期触发) TEETH无法同步 NEST中部分生物的ID无法一致(僵尸)

关卡层级不使用子对象顺序

重构为 1. 所有者广播 去所有者广播 所有者hash 时间戳 2. 询问所有者机制 

复活时攀爬物同步/场景物品同步

重进指令 如果种子相同不刷新地图 仅同步

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

重构捷径同步 Patch_WorldLoader.GenerateLevels_GenParams_Off

修复玩家手持磁盘,部分新道具的姿势

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