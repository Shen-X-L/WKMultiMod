## ObjectTagger作用

Creature	生物-可以被砍出肉
Handhold	把手-可以被抓握
Button		按钮-可以被互动
Damageable	受伤-可被伤害
Pickupable	实体-可被拖拽

## 伤害

```ini
锤子		类型:Melee		标签:Melee blunt	 hammer	player		伤害1-3
自动钻头	类型:piton		标签:piton player					伤害3
砖头		类型:			标签:player							伤害3
信号枪	类型:flare		标签:flare×3 incendiary-long player	伤害4
钢筋/骨矛		类型:rebar	标签:rebar player					伤害10
带绳钢筋		类型	:		标签:player							伤害10
神器长矛(投出/返回)	类型:returnrebar		标签:returnrebar player incendiary(过热)		伤害10
爆炸钢筋		类型:explosion		标签:explosion player						伤害10
			类型:rebarexplosion	标签:rebarexplosion explosion explosive		伤害10×3
爆炸钢筋(自伤)	类型:rebarexplosion	标签:rebarexplosion explosion explosive	伤害1
造冰枪(不蓄力/蓄力)	类型:ice		标签:ice	player			伤害10
					类型:		标签:explosion explosive	伤害 0×3
造冰枪(自伤)			类型:		标签:explosion explosive	伤害 0
手枪			类型:bullet	标签:bullet piercing bleed handgun player	伤害 2
刺剑			类型:Melee	标签:Melee piercing slashing player			伤害 蓄力3 不蓄力1.5
菜刀			类型:Melee	标签:Melee slashing player					伤害 1

蟑螂		类型:denizen			标签:denizen piercing		伤害0.7
tick
	附身	类型:tick			标签:tick Player				伤害0.5
	吸血	类型:tick			标签:						伤害0.08
爆炸蟑螂	
	冲撞	类型:denizen			标签:denizen Player			伤害0.7
	自爆	类型:explosion		标签:explosion×2 explosive	伤害2
血虫		类型:bloodbug		标签:bloodbug slashing		伤害0.3
小血虫	类型:bloodbug-swarmer	标签:bloodbug-swarmer LightAttack	伤害0.05 
绿血虫	类型:bloodbug-spitter	标签:bloodbug-spitter	伤害0.2×4~7
工作血虫	类型:bloodbug-worker		标签:bloodbug-worker		伤害0.3
moth	类型:moth		标签:moth						伤害0.3
藤壶
	舔中	类型:barnacle	标签:barnacle					伤害0
	啃咬	类型:barnacle	标签:barnacle					伤害0.6 
风扇		类型:fan			标签:fan							伤害4
磨床机	类型:grinder		标签:grinder						伤害1
焚烧		类型:fire		标签:fire						伤害0.5
蒸汽		类型:steam		标签:steam						伤害0.4
脏水		类型:nastywater	标签:nastywater					伤害1
筒仓门	类型:silodoor	标签:silodoor					伤害3-9 ???
水槽那个 类型:sturge		标签:sturge						伤害0.33
无人机	类型:drone		标签:drone						伤害1
粉碎机	类型:recycler	标签:recycler					伤害0.5
爆炸		类型:explosion	标签:explosion explosive			伤害3-0
溺水		类型:drowning	标签:drowning					伤害0.25
碾压死	类型:crushed		标签:crushed						伤害23.00712 ???
气囊		类型:gasbag		标签:gasbag explosion			伤害1
爆炸气囊	类型:gasbag		标签:gasbag explosion			伤害1.5
机枪		类型:Turret		标签:Turret bullet				伤害0.5
摔落途中	类型:falling		标签:falling fall-[hand-0|grab]	伤害0.1
摔落至地	类型:falling		标签:falling fall-land			伤害1-N
断腿跳跃	类型:			标签:							伤害0.3
手臂粉碎性骨折		类型:	标签:							伤害0.1
烧伤抓握	类型:			标签:							伤害0.1
流血		类型:bleed		标签:bleed						伤害0.2
teeth	类型:teeth		标签:teeth						伤害1
face	类型:face		标签:face						伤害1
hunter	类型:hunter		标签:hunter						伤害0.3
门		类型:engraveddoor	标签:engraveddoor			伤害0.15
饿死		类型:hunger		标签:hunger						伤害0/0.3
摸电门	类型:handhold-sharp	标签:handhold-sharp			伤害0.5 
血手		类型:ventthing	标签:ventthing					伤害0/1
闪电		类型:lightning	标签:lightning					伤害10
再生骨矛	类型:nonlethal	标签:nonlethal					伤害0.1/2
收音机	类型:d19			标签:d19							伤害0.2
垃圾		类型:garbage		标签:garbage						伤害1
小螃蟹	类型:sprider		标签:sprider						伤害0.2
大螃蟹	
	抓伤	类型:ravelin		标签:ravelin	SkipPropDelayedKill MassiveAttack	伤害2.5
	触电	类型:handhold-sharp	标签:handhold-sharp handhold	伤害0.1 
	弱点命中	类型:explosion	标签:explosion×2 explosive	伤害0
被mother吃	类型:eaten	标签:eaten						伤害0.5
aunt
	喷射	类型:aunt-spike	标签:aunt-spike					伤害0.6
	咬	类型:aunt		标签:aunt						伤害1
僵尸
	舔中	类型:barnacle	标签:barnacle					伤害0.1
	啃咬	类型:barnacle	标签:barnacle					伤害0.5
摸花触电	类型:handhold-sharp	标签:handhold-sharp handhold nonlethal	伤害0.1 
鱼		类型:myzont		标签:myzont Bite					伤害0.5

```

---

## 可拾取物

```ini
笔记 Note	标签:	预制体名称: Item_Note_01

锤子 Hammer	标签:	预制体名称: Item_Hammer 

矛 Rebar					标签: rebar		预制体名称: Item_Artifact_Rebar_Return
游戏机 Artifact Remote	标签:			预制体名称: Item_Artifact_Remote
手套 Artifact Glove		标签:			预制体名称: Item_Artifact_EVAGlove
传送器 Translocator		标签:			预制体名称: Item_Artifact_Translocator
怀表 Can of What			标签:			预制体名称: Item_Artifact_Timepiece
异常罐头 Can of What		标签:			预制体名称: Item_Beans_Periphery
眼球 Blink Eye			标签: blinkeye	预制体名称: Item_BlinkEye

冰冻枪 cryogun		标签: cryogun	预制体名称: Item_Cryogun
饼干 Bean Bar		标签: food		预制体名称: Item_Food_Cookie
牛奶 Milk			标签:			预制体名称: Item_Milk
牛奶-空 Milk			标签:			预制体名称: Item_Milk_Empty
热可可 Hot Cocoa		标签:			预制体名称: Item_Cocoa_Full
圣诞节岩钉 Piton		标签: piton		预制体名称: Item_Piton_Holiday
圣诞节钢筋 Rebar		标签: rebar		预制体名称: Item_Rebar_Holiday
圣诞节带绳钢筋 Rebar	标签: rebar		预制体名称: Item_RebarRope_Holiday

扳手 Wrench				标签: hammer			预制体名称: Item_Pipewrench
电池 Powercell			标签: energy			预制体名称: Item_Powercell
扫描仪 Entity Scanner	标签: flashlight		预制体名称: Item_EntityScanner
注射器 Injector			标签:				预制体名称: Item_Inoculator

万圣节糖果 Can of Beans		标签: 预制体名称: Item_CandyCauldron
万圣节糖果-空 Can of Beans	标签: 预制体名称: Item_CandyCauldron_Empty

能量棒 Bean Bar				标签: food		预制体名称: Item_Food_Bar
肾上腺素 Injector				标签:			预制体名称: Item_Injector
耐力药 PillBottle			标签:			预制体名称: Item_Pillbottle
罐头 Can of Beans			标签:			预制体名称: Item_Beans
钢筋 Rebar					标签: rebar		预制体名称: Item_Rebar
爆炸钢筋 Rebar-Explosive		标签: rebar		预制体名称: Item_Rebar_Explosive
带绳钢筋 Rebar				标签: rebar		预制体名称: Item_RebarRope
岩钉 Piton					标签: piton		预制体名称: Item_Piton
自动岩钉 Auto Piton			标签: piton		预制体名称: Item_AutoPiton
信号枪 flaregun				标签: flaregun	预制体名称: Item_Flaregun
信号枪弹药 Flaregun Ammo		标签: flare		预制体名称: Item_Flaregun_Ammo
手电筒 Flashlight			标签: flashlight	预制体名称: Item_Flashlight

砖头 Rubble			预制体名称: Item_Rubble
金蟑螂 Roach			预制体名称: Denizen_Roach_Gold
银蟑螂 Roach			预制体名称: Denizen_Roach_Platinum
柠檬蟑螂 Roach		预制体名称: Denizen_Roach_Lemon
红宝石蟑螂 Roach		预制体名称: Denizen_Roach_Flying_Ruby
Grub虫 SlugGrub		预制体名称: Denizen_SlugGrub

存档软盘 Floppy Disk		标签: disk	预制体名称: Item_Floppy_T1
存档软盘 Floppy Disk		标签: disk	预制体名称: Item_Floppy_T3
存档软盘 Floppy Disk		标签: disk	预制体名称: Item_Floppy_T2

```

---

## 特效

```ini
Denizen_Barnacle(_Small|_Harpoon|_Small_Icy|_Mechanical):
	Dripping Blood:	嘴里滴血
	Flies:	苍蝇
	Spikes:	嚼嚼乐
	Vomit_Medium:	藤壶死亡 方向性有色血块
	Barnacle_Tongue_Source/Goop:	藤壶舌头 方向性红色小方块
Denizen_Screecher:	寄生虫尖叫体(没有特效)
Denizen_Gasbag:		气囊
	Toots:		黄绿色雾气
Gasbag Explosion:	气囊爆炸
	Dripping Blood:	血
	Gib_Medium:	碎块
Denizen_Hopper_Explosive:	爆炸大蟑螂
	FX_Pollen:	背部火焰特效
Denizen_WasteStrider:	三足兽
	Effect_Splash:	喷水特效
Denizen_Death_Floor:	Mass特效
	Hand_Particle:		Mass的手,全分布
	Hand_Particle_Close:	Mass的手,环状分布
Denizen_Face_Body:	Face身体,大黑球
	Hand_Particle_Close:	Face的手,球状分布
Denizen_Face:	Face
	Residue_Particles:	大坨不断喷涌的多个球形Face
	Corruption Overlay Root/FX_CorruptionOverlay/Particle System:	神秘亮点 环状分布
	DEN_Face:	Face头
DEN_Teeth:	Teeth
	Effects:
		Effect_Hands:	Teeth头上黑手
		Screen Effects/Screen_Effect_Hands:	屏幕上黑手
	GEO_Root:	召唤Teeth节点
		Organelle_Zap:	汇集红色线条特效
		Teeth Cloud:	红雾
Gib_Large:		全向大量肉块
	Gib_Blood:	全向单色血块+大量拖尾
Gib_Sturge_Large:	水槽怪死亡特效 全向大量肉块
	GameObject:	紫色汇集特效
	Gib_Blood:	全向单色血块+大量拖尾
Gib_Small_Grub/Gib_Medium:	全向大量绿块
	Gib_Blood:	全向单色绿块+大量拖尾
Gib_Medium:	全向特大肉块
Gib_Small:	全向小肉块


CL_Player/Main Cam Root/Main Camera Shake Root/Main Camera/Overlay Camera/FX_Player_BloodSplatter_Screen.01: 玩家受伤血迹
Effect_Player_Splash_Acid:	喷火龙果了
Effect_Player_Splash_Dirty:	喷大酱了
Effect_Player_Splash_Clean:	喷脉动了
FX_Player_Hand_Gore/Dripping Blood:	方向性 拖尾 红色三角形


FX_Prop_Break_Box:	条板箱子碎裂
FX_Prop_Break_Cardboard:	纸壳箱子碎裂
FX_TrashExplode:	垃圾块碎裂
HAZ_FallingTrash:	垃圾块碎裂
Present_Break_(RedGreen|WhiteBlue|GreenWhite|RedWhite):	圣诞节礼物盒破碎
Handhold_Breakable_Basic/Shatter_Effect:	不固定把手破碎
Handhold_Icy/Shatter_Effect:	冰冻把手破碎
DEN_Turret_Basic:	机枪
	Particle-Hit:	机枪命中 白色小方块
	DEN_Turret_Gun/Particle-MuzzleFlash:	机枪口发射
Effect_Explosion_Timed:	爆炸
Explosion_Small/Effect_Explosion:		小爆炸
Roach_Explosion/Effect_Explosion:		蟑螂爆炸
Explosion_Medium/Effect_Explosion:		中爆炸
Item_Rebar_Hit_Explosion/Effect_Explosion:	钢筋爆炸


SiloDoor/Effect_Dust:	MASS突破筒仓闸门
ENV_Metafungus_Trampoline/Trigger_JumpPad/Particles:	空中花园跳跃节点红色粒子特效
RC_Artifact_Basic_Chamber/Prop_Rho_Monolith_01_Item_Giver:	1:3:9 神器黑方碑 返回
	Rho Particles:	聚集性Rho符文
M1_Silos_SafeArea_Endless/Prop_Rho_Monolith_01_Portal:		1:3:9 神器黑方碑 传送
	Rho Particles:	聚集性Rho符文
Event_Nuke_FX:	核爆辐射层
	Effect_Explosion:	爆炸贴图
WinTrigger:	胜利4方向礼炮
	Particle System:	单方向彩色礼炮


Item_Artifact_Timepiece/Reverse_Effect/BlinkTargetEffect:	怀表回溯特效
Item_Artifact_Watch/Reverse_Effect/BlinkTargetEffect:	怀表回溯特效
Item_Artifact_Remote_Handhold:	遥控器节点
	BlinkEndEffect:		未知
	BlinkTargetEffect:	遥控器神器节点汇聚
FX_RhoPerkWings:	Rho翅膀
Item_Hands_SlugGrub/Root_Placement/Slub_Hands_AnimRoot/Slub_Root/Gib_Medium:	Grub爆汁子 全向大量绿块
	Gib_Blood:	全向单色绿块+大量拖尾
Item_Hands_Artifact_Remote:	遥控器
	Effects/BlinkTargetEffect:	节点生成特效
		BlinkTargetEffect.01:	圆内白色折线
	Root_Placement/FX_HandStatic:	在手部特效 红色中心辐射
Projectile_Artifact_ReturnRebar:	神器矛投射物
	BlinkTargetEffect:		折线拖尾
	BlinkTargetEffect_Overlay:	未知
	Loop Particles 02:		未知
	Start Particles:		螺旋红色拖尾
	Return Icon:	回收特效+面向玩家
		Start Particles:	单层红色旋转特效
		Return Icon Primed:	多层红色旋转特效
Item_Hands_BlinkEye(_Marionette):	眼球
	Effects/BlinkTargetEffect:	传送点特效
		BlinkTargetEffect.01:	圆内红色折线
	Root_Placement/Item_Hands_Eye:	捏爆
		BlinkTarget_Local:	手中发亮 球面亮红斑点
		Gib_Medium:		爆汁子 全向大量小血块
			Gib_Blood:	全向单色小血块
Item_Hands_Artifact_Translocator:	传送仪
	Effects/BlinkTargetEffect:	传送点特效
		BlinkTargetEffect.01:	圆内红色折线
	Root_Placement/Item_Hands_RBE/BlinkTarget_Local:	手中发亮 环状亮红斑点
Item_Hands_Artifact_EVAGlove/Root_Placement/Item_Hands_EVAGlove:	神器手套
	Lowcharge_Effect:	紫色发散斑点
	Recharge Effect:	绿色汇聚斑点
	Effects/Star Effect:	绿色斑点
Item_Hands_Icegun/Root_Placement/Item_Hands_Icegun:	冰枪
	Effect_frost:		白色离散斑块
	Effect_frost_recharge:	白色汇聚斑块
	Shatter_Effect:		碎冰块特效
Handhold_Cryoshot_Small/Shatter_Effect:		冰枪小子弹生成碎冰特效
Handhold_Cryoshot_Platform/Shatter_Effect:	冰枪大子弹生成碎冰特效
Item_Hands_Flaregun/Root_Placement/Item_Hands_Flaregun:	信号枪
	Effect_Eject:	蛋壳
	Effect_Flare:	闪光
	Effect_Smoke:	枪口烟
Item_Hands_Roach_(Gold|Platinum|Lemon|Ruby)/Roach_Hands_AnimRoot/Particle System:	手持蟑螂的方形亮点
Item_Rebar_Hit_Handhold/FX_Hit_Spark:		钢筋击中特效 白色小方块
Item_Rebar_Hit_Handhold_Bone/FX_Hit_Spark:	骨矛击中特效 白色小方块
Item_RebarRope_Handhold/FX_Hit_Spark:		绳矛击中特效 白色小方块
Item_Rebar_Hit_Handhold_Holiday/FX_Hit_Spark:	圣诞节钢筋击中特效 白色小方块
Item_RebarRope_Handhold_Holiday/FX_Hit_Spark:	圣诞节绳矛击中特效 白色小方块
Item_Piton_Handhold/Piton-Particle:		岩钉击中特效 白色小方块
Item_Piton_Handhold_Holiday/Piton-Particle:	圣诞节岩钉击中特效 白色小方块
Item_AutoPiton_Handhold/Drill_Particle:		自动钻头击中特效 白色小方块
HandFX_Chalk:	未知
HandFX_LowGrav:	月岩特效
HandFX_Rho:	Mass抑制器 Rho符文特效
HandFX_Static:	未知

S1_GarbageChute_Start:	垃圾滑槽
	FX_TrashFall:	垃圾碎屑
	Entities/Hazards:	整个底部
		Hazard_Fire_Jet_Small:	喷射器火焰
		Hazard_Fire_Jet_Small_Timer:	喷射器火焰
		Hazard_Fire_Regular:	垃圾滑槽底部整个火焰
		Hazard_Fire_Regular_Timer:	垃圾滑槽底部火焰
T1_Foundations_Start/Entities/Props/Prop_UpgradeConsole_Training/OS_Computer/Lever_Pull/Hit_Blood:	喷血 方向性小正方形
M2_Pipeworks_Waste:	管道区花园
	Effects:
		Dripping_Effect:	滴水/下雨
		Effect_Waterfall_Splash:	大块喷水
		Leech_Particle:		蛆
	Entities:
		Waste Strider/DEN_WasteStrider/Effect_Splash:	大块喷水
Campaign_Interlude_Habitation_To_Abyss:		深渊开头
	Exterior/M4_Interlude_Collapse_Anim.01:
		Bigdoor_Audio/Crash_FX/FX_Tramline_Smoke:	大雾
		Tram_Interfacility_Offline_Front/Brake Sparks/Effect_Sparks_Brake:	列车摩擦火花
MX_Chimney_Gift/Snow:	下雪
MX_Playground:	游乐场
	Entities/Fan-ForceZone-Main/Wind Particles:	风 单方向长拖尾粒子
	New Stuff:
		DEN_Observer/Effect:	不知道是什么 黄色雾气+环绕拖尾金色粒子
		ENV_Destroyable_Wall_Concrete_Big:
			Effect_Debris:	大量土块
			Effect_Dust:	大量灰尘
		ENV_Destroyable_Wall_Concrete_Regular:
			Effect_Debris:	中量土块
			Effect_Dust:	中量灰尘
M2_Pipeworks_Organ_4:	管道区反应室
	Organelle_Zap/Organelle_Zap:	电流汇集特效
Rho_Alter_Trader:	祭坛
	Rho_TakeItemEffect:	拿走物品特效 红色圈
	Rho_TradeSuccessEvent:	给予物品 大量Rho符文
	Rho_WaitEffect:		等待物品 中心收束红点
FX/FX_Lambda_FireFly:	蓝色萤火虫
	FX_Lambda_FireFly_Child:	超多聚在一起萤火虫
Cable/Cable_Sparks:	间章 电梯电流特效
M1_Silos_Air:
	Wind Particles Up:	大范围低密度风 单方向长拖尾粒子
	Vent-Pull/Wind Particles Vent:	小范围高密度风 单方向长拖尾粒子
M3_Habitation_Pier:	迷失码头 放电区
	FX_Electric_Field:	空气电荷
	Discharge Hazards/Hazard_Discharge/FX_Electric_Field:	闪电预警
ENV_DeltaFissure:	宇宙之眼捏
	Warp_Zap:	微小深蓝色闪电轨迹
Effects:	实验室低重力区域 尘埃+头盔(坐标有偏移))
	FX_Antigravity Field:	大范围失重亮蓝色尘埃
	FX_Antigravity Field_Local:	小范围失重亮蓝色尘埃
M3_Habitation_Lab/Effects/Organelle_Zap.01:	实验室机枪 粉红色汇流
M2_Pipeworks_Drainage_1:	管道区
	Particle_Drift:	大范围长条尘埃
	Particle_Drips/Drop Emitter:	滴水
FX_1/FX_StrangeDust:	棕色萤火虫
	FX_StrangeDust:	聚集棕色萤火虫
MH_Extraction_Prison/Scripting/Warden Lines/Trigger - Warden Announce - Elevator Shock/FX_ThroatSpark:	寄生虫电梯启动火花
	FX_Spark:	火花
M3_Habitation_Shaft_To_Pier/Service Elevator/Elevator_Service_01/Effects/Effect_Sparks:	维修竖井电梯火花
Rho_Artifacts/Face-Safety-Zone:	祭坛离散Rho符文
M4_Abyss_Endless_Start:	无尽深渊开头
	Entities/Props:	物品生成火花(自带符文)
MX_Chimney_Interlude/Marionette_Event_Animator/BlinkTargetEffect:	圣诞节关卡彩蛋微小眼球特效
M3_Delta_Pier_Intro/Warp_Zap:	迷失码头入口青色风


Prop_VendingMachine:			售货机
	Effect_Explosion:		爆炸
		Hazard_Fire_Regular:	着火
Prop_VendingMachine_T2:			高级售货机
	Effect_Explosion:		爆炸
		Hazard_Fire_Regular:	着火
Prop_VendingMachine_Big:		8物品售货机
	Effect_Explosion:		爆炸
		Hazard_Fire_Regular:	着火
Prop_Recycler:		粉碎机
	FX_Spark:	粉碎某物
	FX_Player_Hand_Gore/Dripping Blood:		手部喷血
	Event FXs:	爆炸
		FX_Grub_Hand_Gore/Dripping Blood:	粉碎Grub绿血
Prop_UpgradeConsole_(Main(_PerkOnly|_Endless)?|Experimental|NoDisk( Variant)?|Basic|ScreenAndDisk( Variant)?):	带升级终端
	OS_Computer/Lever_Pull/Hit_Blood:	手部喷血
Prop_EquipmentLocker/ItemInserter/Zap:	保险柜 未知
Prop_ATM/Roach_Gib:	粉碎蟑螂
FX_Hit_Cryoshot_Melt:	冰枪补充机火花
	FX_Fire_Effect_Small:	冰枪补充机着火
Prop_CryoRefueler/FX:	冰枪补充机
	Effect_frost:	白色离散斑块
	Shatter_Effect:	碎冰块
Prop_Rho_ArtifactDevice/Particles:	PCR外围催化反应堆
	FX_Spark:
		FX_Spark:	小火花
		FX_Spark_Main:	大火花
		Lightning: 	球内红色折线闪电
	FX_Sparl_Electric:	火花
	Rho Shield:		符文
		Shield Lightning:	放射性红光束

App_SolarKnight_World/Particles:
	FX_Hit_Explosion_Big:		大像素爆炸
	FX_Hit_Explosion_Medium:	中像素爆炸
	FX_Hit_Explosion_Small:		小像素爆炸
	FX_Hit_Spark:	辐射型闪光
(Event_Weather_Snow|Event_Cold)/Event_Blizzard
	Blizzard Snow:	大雪
	Snow:		小雪
M2_Pipeworks_Organ/Entities/Steam/Steam_Vent:	蒸汽喷孔
	Steam Leak:	蒸汽喷射特效


FX_Hit_Spark:	小火花
FX_Spores:	孢子 白色缓慢扩散方块
Hit_Hammer:	锤子击中的坚固物体的小火花
Hit_Blood:	锤子击中出血
Hit_Blood_Barnacle:	锤子击中出血 更红
Roach_Gib:	蟑螂死亡
GlobalFX_Blood_Effect_Small:	出血 更红
GlobalFX_Fire_Effect_Small:	未知
GlobalFX_Blood_Grub:	Grub死亡
Rebar_Break:	砖头散开破碎
Hit_Brick:	砖头方向性破碎
FX_Artifact_Destroy/Shield Lightning:	神器消散 全方向长拖尾红色波浪辐射粒子

```

---

## 游戏模式

```ini
holderId:basedatabase databaseName:WK_AssetDatabase databaseId:basedatabase

name:GM_DEV_Organism_Gastric	|Organism-Gastric
name:GM_DEV_Organism_TestLevel	|Organism-TestLevel
name:GM_EV_Parasite_Base		|Parasite
name:GM_Abyss_Playtest			|Abyss Playtest
name:GM_Campaign				|Campaign
name:GM_Cheatrooms				|Cheatrooms
name:GM_Chimney_Endless			| Chimney
name:GM_Chimney_IcegunTest		|Icegun Test
name:GM_Endless_Abyss			|Endless Abyss
name:GM_Endless_Habitation		|Endless Habitation
name:GM_Endless_Pipeworks		|Endless Pipeworks
name:GM_Endless_SB17			|Endless Substructure
name:GM_Endless_Silos			|Endless Silos
name:GM_Endless_Superstructure	|Endless Superstructure
name:GM_Endless_Underworks		|Endless Underworks
name:GM_Habitation_Playtest		|Habitation Playtest
name:GM_Ladder					|Ladder
name:GM_Level_Tester			|Level Tester
name:GM_Playground				|Playground
name:GM_Playtest_TangledSink	|Tangled Sink Playtest
name:GM_Section_Builder			|Testing Area
name:GM_Testing_Area			|Testing Area
name:GM_Training_Sector			|Training Sector
name:GM_Tutorial				|Tutorial
name:GM_CH_01_Advanced			|Advanced Course
name:GM_CH_02_Shattered			|Fractured Territory
name:GM_CH_03_RoachRun			|Roach Run
name:GM_CH_04_Comms				|Comms Array
name:GM_CH_05_Shutterworld		|Shuttered Rift
name:GM_CH_06_BoostCourse		|Boost Course

```
---
## BUFF
```
[
"addPocketCapacity","addPocketBigItemCapacity","addCapacity","divideCapacity","slowTime",
"intoxication","addReach-hand-0","addReach","addPlayerScale","addReach-hand--1","addStamina-hand-0",
"divideStamina-hand-0","limitGripStrength-hand-0","addStamina","divideStamina","limitGripStrength",
"grabAnything","pilled","boosted","addStaminaRegen","regenerateGripStrength","addReach-hand-1",
"addStamina-hand-1","divideStamina-hand-1","limitGripStrength-hand-1","roided","gooped","unbound",
"bloodied","freezing","warming","poisoned","sway","swaySpeed","addFOV","addPlayerWidth","addGravity",
"forceCrouchIfGrounded","flight","addAirControl","addSpeed","addSlow","divideSpeed","addDrag",
"addExtraJumps","addGroundedSpeed","massResist","groundedRegeneration","slowTimeInventory","addLimp",
"addGroundedJump","addJump","addStaminaAfterJump","addGripStrength","addGripStrength-hand-1",
"addClimb","addHangSpeed","addHangSpeed-hand-0","addHangSpeed-hand-1","addClimb-hand-1",
"addSlow-hand-1","divideJumpStrain","addGripStrength-hand-0","addClimb-hand-0","addSlow-hand-0",
"reduceFallDamageDistance","addJumpBoost","addPitonSecure","buffTimeMult","addThrow","addStrike",
"damageResist","damageMult","addStaminaAfterExtraJump","addStillGripStrength","addRestingFriction"
]

```
## 饰品/绑定

名称 前缀Trinket_
GoldNugget
MassDamper
Beta
Carabiner
Chalk
EmployeeID
MoonRock
PhotoOfHome
Pouch
BagExpander
Headlamp

名称 前缀Binding_
HalfInventory
HighGravity
NoPerks
PitonAndBeans
WeakArms
Survival
NoShops
