其他支线的地图同步
记录旧关卡坐标,执行协程完成后记录新关卡坐标,计算偏移,每次切换关卡时根据偏移调整玩家坐标

岩钉被拔掉,和锤入时坐标偏移无法同步

玩家ID TMP组件变为UI显示

记得修补TMP字体文件 按钮组件 UI_Manager.DisplayMessage

修改光亮
换个UI
Patch_UI_GamemodeScreen.Postfix 改成根据反射onClick激活的函数来确定应该在哪个页面克隆按钮

UI_LobbyListPane.RefreshLobbyList换成对象池

他人想法:
游乐场模式击杀统计排行榜
自定义加载的场景死后不断连

完善反编译
M_Subregion M_Region M_Level WorldLoader M_Gamemode M_GenerationBranch CL_GameManager CL_SaveManager
