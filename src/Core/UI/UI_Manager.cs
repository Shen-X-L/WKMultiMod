using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using System.Reflection;
using WKMPMod.Util;
using static ENT_Player;
using static UI_TabGroup;

namespace WKMPMod.UI;

public class UI_Manager : MonoSingleton<UI_Manager> {

	// 主菜单UI按钮容器
	const string MainMenu = "Canvas - Main Menu/Main Menu";
	const string MainMenuButtons = "Canvas - Main Menu/Main Menu/Main Menu Buttons";
	const string CanvasScreenPlay = "Canvas - Screens/Screens/Canvas - Screen - Play";
	GameObject MPButton;
	GameObject MPScreen;
	GameObject MPLobbyPane;

	#region[Unity组件生命周期函数]

	protected override void Awake() {
		base.Awake();
		SceneManager.sceneLoaded += OnSceneLoaded;
	}

	#endregion

	// 场景切换时重注册UI
	public void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
		switch (scene.name) {
			case "Main-Menu": {
				CreateMenuButton();
				CreateLobbyScreen();
				Initialize();
				break;
			}
			default:
				break;
		}
	}

	// 在主菜单创建多人模式按钮
	public void CreateMenuButton() {
		// 找到现有的菜单容器
		GameObject menuContent = GameObject.Find(MainMenuButtons);
		// 找到一个现有的按钮作为模版
		GameObject templateButton = menuContent.transform.Find("Cosmetics").gameObject;
		// 克隆并修改名称
		MPButton = GameObject.Instantiate(templateButton, menuContent.transform);
		MPButton.name = "Multi Play";
		// 修改层级
		MPButton.transform.SetSiblingIndex(1);
		// 修改文字
		var tmp = MPButton.GetComponentInChildren<TMPro.TextMeshProUGUI>();
		if (tmp != null) tmp.text = "MULTI PLAY";

	}
	// 创建多人模式大厅屏幕
	public void CreateLobbyScreen() {

		#region[创建大厅搜索菜单]
		// 找到现有的菜单容器
		GameObject screenContent = GameObject.Find(CanvasScreenPlay);
		// 找到主游戏屏幕作为模版
		GameObject templateScreen = screenContent.transform.Find("Play Menu")?.gameObject;
		// 克隆并修改名称
		MPScreen = Instantiate(templateScreen, screenContent.transform);
		MPScreen.name = "Multi Play Menu";
		// 修改层级
		MPScreen.transform.SetSiblingIndex(0);
		// 修改大厅列表子对象
		GameObject lobbyPaneContainer = MPScreen.transform.Find("Play Pane")?.gameObject;
		lobbyPaneContainer.name = "Lobby Pane";

		// 修复可能的UI_LerpOpen组件问题(如果存在的话)
		FixLerpComponent(lobbyPaneContainer);

		// 删除不需要的UI元素
		Destroy(MPScreen.transform.Find("GamemodeScreen")?.gameObject);
		Destroy(lobbyPaneContainer.transform.Find("Play Scroll View")?.gameObject);
		Destroy(lobbyPaneContainer.transform.Find("Tab Selection")?.gameObject);
		#endregion

		#region[创建标签页按钮]
		// 获取标签页容器
		GameObject screenTabs = lobbyPaneContainer.transform.Find("Tabs")?.gameObject;
		// 获取标签页按钮容器
		GameObject screenTabButtons = screenTabs.transform.Find("Tab Buttons")?.gameObject;
		// 克隆并修改标签页按钮
		GameObject tabButtonTamplate = screenTabButtons.transform.Find("ModeButton_Custom")?.gameObject;
		tabButtonTamplate.name = "ModeButton_Tamplate";
		tabButtonTamplate.transform.Find("Text (TMP)")
			?.gameObject.GetComponent<TextMeshProUGUI>()
			?.text = "TEMPLATE";
		GameObject newTabButton = GameObject.Instantiate(tabButtonTamplate, screenTabButtons.transform);
		newTabButton.name = "ModeButton_Lobby";
		newTabButton.transform.Find("Text (TMP)")
			?.gameObject.GetComponent<TextMeshProUGUI>()
			?.text = "LOBBY";
		// 修改层级
		tabButtonTamplate.transform.SetSiblingIndex(1);
		tabButtonTamplate.SetActive(true);
		newTabButton.transform.SetSiblingIndex(2);
		newTabButton.SetActive(true);
		// 删除不需要的标签页按钮
		// 跳过0号 1号 2号 -1号 0号是LB图标 1号是标签页按钮 2号是测试模板按钮 -1号是RB图标 其他的都是需要删除的标签页按钮
		for (var index = screenTabButtons.transform.childCount - 2; index > 2; --index) {
			Destroy(screenTabButtons.transform.GetChild(index).gameObject);
		}
		#endregion

		#region[创建标签页内容]
		// 获取内容容器
		GameObject screenTabObjects = lobbyPaneContainer.transform.Find("Tab Objects")?.gameObject;
		GameObject lobbyPaneTamplate = screenTabObjects.transform.Find("Play Pane - Scroll View Tab - Custom")?.gameObject;
		// 重命名模板并修改顺序
		lobbyPaneTamplate.name = "Lobby Pane - Scroll View Tab - Tamplate";
		lobbyPaneTamplate.transform.SetSiblingIndex(0);
		// 删除其他标签页内容
		// 跳过0号模板
		for (var index = screenTabObjects.transform.childCount - 1; index > 0; --index) {
			Destroy(screenTabObjects.transform.GetChild(index).gameObject);
		}

		// 克隆并修改标签页内容
		GameObject MPLobbyPane = GameObject.Instantiate(lobbyPaneTamplate, screenTabObjects.transform);
		MPLobbyPane.name = "Lobby Pane - Scroll View Tab - Lobby";
		MPLobbyPane.SetActive(true);
		// 隐藏不需要的UI元素
		var lobbyPaneContent = MPLobbyPane.transform.Find("Viewport/Content")?.gameObject;
		for (var index = lobbyPaneContent.transform.childCount - 1; index >= 0; --index) {
			Destroy(lobbyPaneContent.transform.GetChild(index).gameObject);
		}
		MPLobbyPane.AddComponent<UI_LobbyListPane>();
		#endregion

		#region[创建模式变体]
		GameObject MPScreenMutators = lobbyPaneContainer.transform.Find("Mutators")?.gameObject;
		MPScreenMutators.SetActive(false);
		#endregion

		#region[连接标签页UI事件]
		var tabGroup = screenTabs.GetComponent<UI_TabGroup>();
		tabGroup.tabs = new List<UI_TabGroup.Tab>() {
				new UI_TabGroup.Tab() {
					name = "lobby",
					button = newTabButton.GetComponent<UnityEngine.UI.Button>(),
					tabObject = MPLobbyPane,
					firstSelect = null,
					buttonText = newTabButton.transform.Find("Text (TMP)")?.gameObject.GetComponent<TextMeshProUGUI>(),
					onlyDev = false,
				},
				new UI_TabGroup.Tab() {
					name = "template",
					button = tabButtonTamplate.GetComponent<UnityEngine.UI.Button>(),
					tabObject = lobbyPaneTamplate,
					firstSelect = null,
					buttonText = tabButtonTamplate.transform.Find("Text (TMP)")?.gameObject.GetComponent<TextMeshProUGUI>(),
					onlyDev = true,
				}
			};
		#endregion

	}

	// 初始化UI关联
	public void Initialize() {
		// 移除点击事件
		var buttonComponent = MPButton.GetComponent<UnityEngine.UI.Button>();
		buttonComponent.onClick.RemoveAllListeners(); // 移除原有的功能
		// 修改关联菜单
		var menuButtonComponent = MPButton.GetComponent<UI_MenuButton>();
		menuButtonComponent.screen = MPScreen.GetComponent<UI_MenuScreen>();
		// 初始化函数
		GameObject menu = GameObject.Find(MainMenu);
		var uI_MenuComponent = menu.GetComponent<UI_Menu>();
		menuButtonComponent.Initialize(uI_MenuComponent);
	}


	private void FixLerpComponent(GameObject target) {
		var lerp = target.GetComponent<UI_LerpOpen>();
		if (lerp == null) return;

		// 获取私有字段的 FieldInfo
		// 游戏源码中的变量名：targetPosition, targetSize, rootPositon, rootScale
		Type type = typeof(UI_LerpOpen);
		BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;

		// 1. 修正目标位置为零（显示时的正常位置）
		type.GetField("targetPosition", flags)?.SetValue(lerp, Vector3.zero);
		type.GetField("rootPositon", flags)?.SetValue(lerp, Vector3.zero);

		// 2. 修正目标缩放为 1（正常显示的大小）
		type.GetField("targetSize", flags)?.SetValue(lerp, Vector3.one);
		type.GetField("rootScale", flags)?.SetValue(lerp, Vector3.one);
	}
}
