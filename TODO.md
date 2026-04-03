与自定义模式的兼容问题 UI_MenuButton.OpenScreen 无法正常打开UI_MenuScreen
UI_MenuButton.OpenScreen->UI_MenuScreen.Open->UI_MenuScreen.openEvent.Invoke->UI_LerpOpen.Show
UI_MenuScreen.CloseScreen->UI_MenuScreen.closeEvent.Invoke->UI_LerpOpen.Hide

按钮组件记得修补TMP字体文件
UI_LobbyListPane添加一个刷新按钮

TMP组件变为多行显示
字体shader会卸载,查找原因,想办法修复 -> 日后做成TMP UI组件
修改光亮

他人想法:
游乐场模式击杀统计排行榜
自定义加载的场景死后不断连