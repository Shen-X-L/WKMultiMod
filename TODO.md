发现接管流程
发现本地实体, 注册网络实体 ID
    -> 检查本地所有权记录
        -> 已有有效所有者 向所有者发送订阅请求
        -> 没有所有者 向当前 Steam Lobby Owner 请求接管 (需要网络延迟窗口 避免每帧请求)
            -> Host 裁决新所有者
            -> Host广播所有权变更
            -> 所有客户端 更新本地所有权记录
            -> 根据新所有者建立数据同步关系

放弃接管流程
成员离开事件/停止接管广播
    -> 检查该玩家拥有的实体/检查对应实体
    -> 使旧所有权记录失效
    -> 停止接受旧所有者的数据
    -> 所有订阅者请求当前 Host 重新裁决 (分帧或批量打包)
        -> Host 选择新所有者
        -> Host 广播所有权变更 
        -> 所有客户端 更新本地所有权记录
        -> 根据新所有者建立数据同步关系

数据广播流程
    -> 客机 B 发现实体
    -> 查询本地所有权记录
        -> 直接获取所有者 A
        -> 记录缺失或失效 向当前 Host 查询所有者
            -> Host 返回所有者 A
            -> 客机 B 更新本地记录
    -> 客机 B 向所有者 A 请求订阅
    -> 所有者 A 登记 B 的订阅
    -> 所有者 A 发送实体初始化数据
    -> 所有者 A 持续广播实体更新
    -> 客机 B 接收并应用实体数据

新玩家加入 Steam Lobby
    -> 请求同步所有权记录
    -> 现有同步架构 Host 创建分包同步协程
        -> 分批序列化所有权记录
        -> 逐包发送给新玩家
        -> 等待或按现有机制调度下一批
    -> 新玩家 接收并组装所有权记录
    -> 根据记录建立数据订阅
    -> 缺失记录的实体 进入发现接管流程

生物索敌进行请求,乐观索敌,受到拒绝后放弃为目标

仅有吐射动画,没有射击物

僵尸索敌坐标错误

DEN_Teeth.Awake AIC_Teeth_Chase.KillPlayerAnimation
DEN_Face.Start .Damage.HitShift
DEN_Hunter.TrackPlayerPosition .Start .TargetCollisionCheck
DEN_EngravedDoor(忽略)
DEN_LadderNightmare.Update 
DEN_Mother.Damage 
DEN_VentThing.SetTarget
DEN_Remains_Active
DEN_RatKing
DEN_Phage
只索敌主玩家

HUNTER在场景切换后ID不一致(记录hunter生命周期触发) TEETH无法同步 NEST中部分生物的ID无法一致(僵尸)

远程钻头没有被攀爬物同步

复活时攀爬物同步/场景物品同步

重进指令 如果种子相同不刷新地图 仅同步

盗版创建房间失败 ?

lobbyrestart指令

支持Lua自定义命令

投射物同步

修改死亡物品掉落 尝试同步

玩家离开时 所属物品销毁同步

玩家手持物品销毁同步

刷新大厅时刷新大厅数据

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

