DEN_Teeth隐藏disableAI

神秘的模型偏移?上升电梯让CL_Prop类型跟随电梯运动,哪个组件做的?

DEN_EngravedDoor(忽略)
DEN_Teeth.Awake AIC_Teeth_Chase.KillPlayerAnimation
ENV_BearTrap
DEN_Face.Start .Damage.HitShift
DEN_Hunter.TrackPlayerPosition .Start .TargetCollisionCheck
DEN_Mother.Damage 
DEN_VentThing.SetTarget
DEN_Remains_Active
DEN_RatKing
只索敌主玩家

生物同步模块生物激活关闭先经过合并消除后再进行身份注册

仅有吐射动画,没有射击物

僵尸索敌坐标错误

HUNTER在场景切换后ID不一致(记录hunter生命周期触发) TEETH无法同步 NEST中部分生物的ID无法一致(僵尸)

借鉴 https://github.com/MirrorNetworking/Mirror/blob/master/Assets/Mirror/Core/NetworkWriter.cs 改进DataWriter显示重载
比如使用*(T*)ptr = value; 直接写入内存

可能重构AnimatorSyncFeature为检测Animator状态机参数而不是拦截IL

复活时攀爬物同步/场景物品同步

地图同步机制

重进指令 如果种子相同不刷新地图 仅同步

Noita和星际拓荒是怎么实现的盗版

切分MPCore

大厅可以用大厅码加入

lobbyrestart指令

剪刀跟随物

连接判断循环/完整玩家数据发送可能有卡顿

支持Lua自定义命令

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

