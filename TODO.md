deathfloor根据地图改为mass
玩家ID TMP组件变为UI显示
不知道为什么会卡顿 怀疑是UI_LobbyListPane创建按钮开销过大?

换个UI

Restart以后做成 HOOK 拦截 + 启动同步协程 + Loading界面

记得修补TMP字体文件 按钮组件 UI_Manager.DisplayMessage

修改光亮

他人想法:
游乐场模式击杀统计排行榜
自定义加载的场景死后不断连