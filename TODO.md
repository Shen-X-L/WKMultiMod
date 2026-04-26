UI换页不管用

玩家ID TMP组件变为UI显示

Restart以后做成 HOOK 拦截 + 启动同步协程 + Loading界面

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
