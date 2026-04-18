# White Knuckle Multi Player Mod - 白色节点联机MOD

**中文** | [English](README.md)

## 概述

这是一个为《白色节点》制作的 Unity MOD, 实现了简易联网玩家映射.

 **重要声明** :

* **本人并非 Unity/C# 开发者, 日常工作不涉及此类开发.**
* 本项目中 **部分代码由 AI 生成** .
* 因此,  **部分代码质量可能非常糟糕** , 请谨慎参考.
* 联机功能相关的代码 **fork自之前存在的联机mod项目** .

 **可能的目标** :

```mermaid
graph RL
    %% 模块 1:玩家显示方面
    subgraph 玩家显示方面
        1d[显示其他玩家手持物]
    end

    %% 模块 2:玩家交互
    subgraph 玩家交互
        2b[可以抢夺物品]
        2c[添加新物品]
    end

    %% 模块 3:同步数据
    subgraph 同步数据
        3a["同步人造结构(岩钉,钢筋)"]
        3c[同步可拾取物品]
        3e[同步实体数据]
    end

    %% 依赖关系连接 (跨模块和模块内)
    1d --> 2b
```

---

## 安装MOD

### 前提条件

1. **游戏** :《White Knuckle》
2. **框架** :[BepInEx](https://github.com/BepInEx/BepInEx) (请使用与游戏版本兼容的版本)

### 安装步骤

在 [Releases](https://github.com/Shen-X-L/WKMultiMod/releases) 页面下载所需的 `.zip` 文件, 放入游戏目录下的 `BepInEx/plugins` 目录中解压即可.

## MOD 功能详情

### 联机功能

### 1.*/0.14

在游戏中开启作弊模式 (`cheats`) 后, 可使用以下命令:

* `host <名称> [最大玩家数]` - 创建大厅.
  * 示例:`host abcde`
* `getlobbyid` - 获取大厅大厅码
* `join <大厅码>` - 通过大厅码,加入大厅
  * 示例: `join 109775241951624817`
* `talk <文字(目前控制台不支持中文)>` - 来在头顶的标签上说话
  * 示例: `talk help me`
* `getallplayer` - 获取全部玩家及其steamId
* `tpto <steamId(后缀匹配)>` - 进行玩家间tp
  * 示例 `tpto 16422 或 tpto 22(目标steamId 561198279116422)` 
* `leave` - 离开当前连接的大厅.

### 1.3.1

* 使用主菜单UI进行加入大厅

新增命令:
* `getalllobby` - 获取所有大厅信息,包括大厅码和当前玩家数
* `join <名称>` - 通过大厅名称加入大厅,如果有多个同名大厅会无法加入,请使用大厅码加入
  * 示例: `join abcde`
* `changename <名称>` - 修改大厅名称,只能在创建者使用
  * 示例: `changename newname`
* `changemodel <模型名称>` - 修改远程玩家模型,目前支持default和slugcat
  * 示例: `changemodel slugcat`

### 1.3.4

新增命令:
* `lobbytype [public/private/friends]` - 修改大厅可见类型,public为公开,private为私密(只能通过大厅码加入),friends为好友可见(只能被好友看到并加入)
  * 示例: `lobbytype friends`
* `invite` - 邀请好友加入大厅

### 0.12(停止更新)

在游戏中开启作弊模式 (`cheats`) 后, 可使用以下命令:

* `host <端口号> [最大玩家数]` - 创建主机.
  * 示例:`host 22222`
* `join <IP地址> <端口号>` - 加入一个已创建的主机.
  * 示例:`join 127.0.0.1 22222` 或 `join [::1] 22222`
* `leave` - 离开当前连接的主机.

### 配置选项

`BepInEx/plugins/shenxl.MultiPlayerMod.cfg` 中
```
[Network]

## 设置每秒向其他玩家发送数据的次数.
# Setting type: Int32
DataSendFrequency = 20

[RemotePlayer]

## 这个值设置玩家头部名称的缩放倍率
# Setting type: Single
NameTagScale = 1

## 设置远程玩家使用的模型,默认值为'default',你可以设置为'slugcat'来使用蛞蝓猫模型.
# Setting type: String
Model = default

[RemotePlayerPvP]

## * 锤子 - 类型Hammer 伤害1
## * 自动钻头 - 类型piton 伤害3
## * 砖头 - 类型 伤害3
## * 信号枪 - 类型flare 伤害6
## * 钢筋/骨矛 - 类型rebar 伤害10
## * 带绳钢筋 - 类型 伤害10
## * 神器长矛(投出/返回) - 类型returnrebar 伤害10
## * 爆炸钢筋 - 类型explosion 伤害10 - 类型rebarexplosion 伤害10 × 2
## * 造冰枪(不蓄力/蓄力) - 类型ice 伤害10 - 类型 伤害 0 × 2
## 
## Active配置项控制玩家造成的伤害倍率
## Passive配置项控制玩家受到的伤害倍率
## 公式 : 最终伤害 = 基础伤害 × AllActive倍率 × AllPassive倍率 × 对应类型Active倍率 × 对应类型Passive倍率

## 玩家造成所有伤害类型的伤害倍率
# Setting type: Single.2
AllActive = 0.2

## 玩家受到所有伤害类型的伤害倍率
# Setting type: Single
AllPassive = 1

## 玩家可以使用锤子造成伤害的伤害倍率
# Setting type: Single
HammerActive = 5

## 玩家受到锤子伤害的伤害倍率
# Setting type: Single
HammerPassive = 1

## 玩家可以使用长矛类造成伤害的伤害倍率
# Setting type: Single
RebarActive = 1

## 玩家受到长矛类伤害的伤害倍率
# Setting type: Single
RebarPassive = 1

## 玩家使用自动钻头造成伤害的伤害倍率
# Setting type: Single
PitonActive = 1

## 玩家受到自动钻头伤害的伤害倍率
# Setting type: Single
PitonPassive = 1

## 玩家使用信号枪造成伤害的伤害倍率
# Setting type: Single
FlareActive = 1

## 玩家受到信号枪伤害的伤害倍率
# Setting type: Single
FlarePassive = 1

## 玩家使用神器长矛造成伤害的伤害倍率
# Setting type: Single
ReturnRebarActive = 1

## 玩家受到神器长矛伤害的伤害倍率
# Setting type: Single
ReturnRebarPassive = 1

## 玩家造成爆炸钢筋伤害的伤害倍率
# Setting type: Single
RebarExplosionActive = 1

## 玩家受到爆炸钢筋伤害的伤害倍率
# Setting type: Single
RebarExplosionPassive = 1

## 玩家造成爆炸溅射伤害的伤害倍率
# Setting type: Single
ExplosionActive = 1

## 玩家受到爆炸溅射伤害的伤害倍率
# Setting type: Single
ExplosionPassive = 1

## 玩家使用造冰枪冰锥造成伤害的伤害倍率
# Setting type: Single
IceActive = 1

## 玩家受到造冰枪冰锥伤害的伤害倍率
# Setting type: Single
IcePassive = 1

## 玩家造成其他伤害类型的伤害倍率
# Setting type: Single
OtherActive = 1

## 玩家受到其他伤害类型的伤害倍率
# Setting type: Single
OtherPassive = 1


```
## 开发指南

### 源码构建

**bash**

```
# 1. 克隆此仓库到本地
git clone https://github.com/Shen-X-L/WKMultiMod.git

# 2. 构建 MOD
# 方法A: 使用 Visual Studio 打开并构建 WhiteKnuckleMod.sln
# 方法B: 使用命令行
dotnet build -c Release
```

### 项目结构

```
WhiteKnuckleMod/
├──src/Core/        # Mod核心逻辑
│   ├─Asset/
│   │   └─MPAssetManager.cs # 负责获取游戏本体预制体,通过Resources.FindObjectsOfTypeAll<GameObject>()寻找特定预制体
│   ├─Component/            # 所有需要游戏本体库无法移至Unity项目的组件
│   │   ├─LocalPlayer.cs    # 组件类,负责玩家本地位置
│   │   └─RemoteEntity.cs   # 组件类,负责对其他玩家的伤害
│   ├─Core/
│   │   ├─MPConfig.cs           # 读取配置文件的数据
│   │   ├─MPCore.cs             # 核心类,负责主要事件处理
│   │   ├─MPGameModeManager.cs  # 负责定义可以网络传送的游戏模式数据和加载对应游戏模式
│   │   └─MPMain.cs             # 启动类,用来启动补丁
│   ├─Data/
│   │   ├─DataReader.cs         # 读取ArraySegment<byte>/byte[]内部数据
│   │   ├─DataWriter.cs         # 写入ArraySegment<byte>数据
│   │   ├─MPDataPool.cs         # 管理每个线程独立的读写对象池,避免频繁分配内存
│   │   ├─MPEventBusGame.cs     # 游戏内数据总线,负责游戏内事件的发布和订阅
│   │   └─MPEventBusNet.cs      # 网络数据总线,负责MPCore和MPSteamworks交流
│   ├─NetWork/
│   │   ├─MPLiteNet.cs          # 通过IP连接 暂时废弃
│   │   ├─MPPacketHandler.cs    # 处理接收数据包的类,根据协议分发数据
│   │   ├─MPPacketRouter.cs     # 通过反射构建 包类型-处理函数字典 根据包类型调用对应的处理函数
│   │   └─MPSteamworks.cs       # 拆分的steam网络逻辑类
│   ├─Patch/
│   │   ├─Patch.cs                  # 补丁,通过解锁进度+禁用翻转实现地图同步
│   │   ├─Patch_ENT_Player.cs       # 补丁,获取玩家的事件
│   │   └─Patch_SteamManager.cs     # 补丁,通过SteamManager的生命周期来初始化MPCore
│   ├─RemotePlayer/
│   │   ├─Factory/
│   │   │   ├─BaseRemoteFactory.cs  # 远程对象工厂基类,提供创建远程对象的接口,通过复制预制体创建远程玩家对象
│   │   │   └─SlugcatFactory.cs     # 对蛞蝓猫预制体模型进行特殊处理的工厂类
│   │   ├─RPContainer.cs        # 负责单个远程玩家对象的数据更新和生命周期
│   │   ├─RPFactoryManager.cs   # 负责创建远程玩家对象,并将其添加到RPManager中管理
│   │   └─RPManager.cs          # 管理全部远程玩家对象的数据更新和生命周期
│   ├─Test/
│   │   ├─Test.cs               # 不影响游戏的测试函数,可以快速修改
│   │   └─TestMonoSingleton.cs  # 测试用的MonoSingleton,可以快速修改
│   ├─UI/
│   │   ├─UI_LobbyButton.cs     # 大厅选项按钮组件,负责加入大厅界面按钮的功能
│   │   ├─UI_LobbyListPane.cs   # 大厅列表面板组件,负责显示大厅列表界面
│   │   └─UI_Manager.cs		    # UI管理器,负责创建和管理UI界面
│   └─Util/ 
│       ├─Localization/       
│       │   ├─Localization.cs   # 本地化工具类,获取本地化控制台文本
│       │   ├─json_sort.py      # 用于将Localization文件夹下的json文件排序和对比
│       │   ├─texts_en.json     # 英文文本
│       │   └─texts_zh.json     # 中文文本
│       ├─MonoSingleton.cs      # Unity组件单例基类,提供在Unity中使用的单例模式实现
│       └─Singleton.cs          # 普通单例基类,提供普通的单例模式实现
├──src/Shared/      # 提取的Unity组件逻辑,用于共享到unity项目快速构建预制体
│   ├─Component/    # 可以在Unity项目使用的组件
│   │   ├─LookAt.cs         # 让标签强制面向玩家,缩放标签使大小不变
│   │   ├─ObjectIdentity.cs # 标识该对象的创建工厂Id,用于对象正确销毁
│   │   ├─RemoteHand.cs     # 通过网络数据控制手部位置
│   │   ├─RemotePlayer.cs   # 通过网络数据控制玩家位置
│   │   ├─RemoteTag.cs      # 通过网络数据控制标签内容
│   │   └─SimpleArmIK.cs    # 通过IK使胳膊连接到手
│   ├─Data/ 
│   │   ├─HandData.cs           # 手部位置数据
│   │   └─PlayerData.cs         # 玩家位置数据
│   ├─MK_Component/     # 游戏内的组件,无法直接赋予,通过映射组件处理
│   │   ├─MK_CL_Handhold.cs     # 游戏内CL_Handhold的映射
│   │   ├─MK_ObjectTagger.cs    # 游戏内ObjectTagger的映射
│   │   └─MK_RemoteEntity.cs    # Mod的RemoteEntity的映射
│   └─Util/ 
│       ├─DictionaryExtensions.cs   # 字典工具类,有后缀匹配,字典做差等功能
│       └─TickTimer.cs              # Debug控制输出频率计数器
├── lib/                            # 外部依赖库目录 (需自行添加) 
│   └── README.md                   # 依赖库获取说明
├── WhiteKnuckleMod.sln             # Visual Studio 解决方案文件
├── WhiteKnuckleMod.csproj          # 项目配置文件
└── README.md                       # 本文档
```

### 环境设置

1. **安装 .NET SDK** :从 [Microsoft .NET官网](https://dotnet.microsoft.com/) 下载并安装.
2. **恢复 NuGet 包** :在项目根目录执行 `dotnet restore`.
3. **获取游戏 DLL** :请务必按照 `lib/README.md` 中的说明, 获取必要的游戏 DLL 文件并放入 `lib/` 目录.

### 依赖库说明

本项目编译需要引用游戏本体的部分 DLL 文件 ( **这些文件受版权保护, 请勿提交至本仓库** ) , 主要包括:

* `Assembly-CSharp.dll`
* `UnityEngine.dll`
* `UnityEngine.CoreModule.dll`
* 等文件 (详见 `lib/README.md`) .

### 构建配置要点

项目文件 (`WhiteKnuckleMod.csproj`) 中已配置关键引用和构建目标, 确保 `TargetFramework` 为 `netstandard2.1` 并允许不安全代码.

## 贡献指南

欢迎提交 Issue 报告问题或提出建议！也欢迎 Pull Request 贡献代码.

 **再次提醒** :本项目代码质量参差不齐, 且部分为AI生成, 贡献时请注意.

### 贡献流程

1. Fork 本仓库.
2. 创建您的特性分支 (`git checkout -b feature/你的新功能`).
3. 提交您的更改 (`git commit -m '添加了某个功能'`).
4. 推送至分支 (`git push origin feature/你的新功能`).
5. 开启一个 Pull Request.

### 代码规范建议

* 尽量遵循 C# 通用命名约定.
* 关键部分可添加注释说明.
* 新功能请进行充分测试.

## 重要版权声明:

* 游戏本体及其相关的 DLL 文件版权归原游戏开发商所有.
* 使用本 MOD 需确保您已拥有合法的《白色节点》游戏副本.

## 致谢

* **[Harmony](https://github.com/pardeike/Harmony)** - 强大的 .NET 运行时补丁库.
* **[BepInEx](https://github.com/BepInEx/BepInEx)** - 优秀的 Unity 游戏插件框架.
* **《白色节点》游戏社区** - 提供的灵感和测试帮助.
* **原联机 MOD 作者** - 为其开源代码奠定了基础.

## 联系方式

* **GitHub Issues** : [在此提交问题或建议](https://github.com/%E4%BD%A0%E7%9A%84%E7%94%A8%E6%88%B7%E5%90%8D/%E4%BB%93%E5%BA%93%E5%90%8D/issues)
* [**White Knuckle Discord**](https://discord.com/invite/f2CqdmUSap)
* [**联机Mod Discord**](https://discord.gg/huHkf6ChcV)
* **QQ 群** : 596296577
* **作者** : Shenxl - 819452727@qq.com
