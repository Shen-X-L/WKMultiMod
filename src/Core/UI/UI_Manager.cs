using System;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.NetWork;
using WKMPMod.Util;

namespace WKMPMod.UI;

public class UI_Manager : MonoSingleton<UI_Manager> {


	public enum UIDisplayType {
		None,
		AscentHeader,// 最顶部
		TipHeader,//1/3处
		Header,//2/5处
		HighscoreHeader//3/5处
	}

	// 主菜单UI按钮容器路径
	const string MAIN_MENU_PATH = "Canvas - Main Menu/Main Menu";
	const string MAIN_MENU_BUTTONS_PATH = "Canvas - Main Menu/Main Menu/Main Menu Buttons";
	// Play屏幕UI容器路径
	const string CANVAS_SCREEN_PLAY_PATH = "Canvas - Screens/Screens/Canvas - Screen - Play";
	const string PLAY_PANE_PATH = "Play Pane";
	// 游戏模式信息屏幕UI容器路径
	const string GAMEMODE_SCREEN_PATH = "Canvas - Screens/Screens/Canvas - Screen - Play/Play Menu/GamemodeScreen";
	// 主菜单按钮
	GameObject? _mpButton;
	// 多人模式屏幕
	GameObject? _mpScreen;
	// Play屏幕
	GameObject? _lobbyPaneContainer;

	// 标签页选项总容器
	GameObject? _screenTabs;
	// 标签页选项按钮容器
	GameObject? _screenTabButtons;
	// 新标签页选项按钮
	GameObject? _newTabButton;
	GameObject? _tabButtonTemplate;

	// 标签页内容总容器
	GameObject? _screenTabObjects;
	// 新标签页内容容器
	GameObject? _mpLobbyPane;
	GameObject? _lobbyPaneTemplate;

	// 模式变体容器
	GameObject? _mutators;

	/// <summary>
	/// UI层级
	/// _lobbyPaneContainer
	/// ├-_screenTabs
	/// │ └─_screenTabButtons
	/// │   ├-LB
	/// │   ├-_newTabButton
	/// │   ├-_tabButtonTemplate
	/// │   └-RB
	/// └-_screenTabObjects
	///   ├-_mpLobbyPane
	///   └─_lobbyPaneTemplate
	/// </summary>
	/// 

	// Loading界面路径
	const string LOADING_SCREEN_PATH = "Canvas - Screens/Screens";
	// Loading界面模版
	GameObject? loadingTemplate;
	// 新Loading界面
	GameObject? newloading;

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
				try {
					// 每次进入主菜单，彻底清理旧对象，防止重复创建
					if (_mpButton != null) Destroy(_mpButton);
					if (_mpScreen != null) Destroy(_mpScreen);

					// 执行子逻辑
					CreateMenuButton();
					CreateLobbyScreen();
					Initialize();
				} catch (Exception ex) {
					// 捕获所有未预期的崩溃，并记录日志
					MPMain.LogError(Localization.Get("UI_Manager.CreateMenuUIFailed", ex.Message));
				}
				try {
					//MPMain.LogWarning("[MP Debug] 创建loading");
					CreateLoadingScreen();
				} catch (Exception ex) {
					// 捕获所有未预期的崩溃，并记录日志
					MPMain.LogError(Localization.Get("UI_Manager.CreateMenuUIFailed", ex.Message));
				}
				break;
			}
			default:
				break;
		}
	}

	#region[主菜单UI]

	// 在主菜单创建多人模式按钮
	public void CreateMenuButton() {
		// 找到现有的菜单容器
		GameObject menuContent = GameObject.Find(MAIN_MENU_BUTTONS_PATH);
		if (menuContent == null) {
			MPMain.LogError(Localization.Get("UI_Manager.MainMenuContainerNotFound"));
			return;
		}
		// 找到一个现有的按钮作为模版
		GameObject? templateButton = menuContent.transform.Find("Cosmetics")?.gameObject;
		if (templateButton == null) {
			MPMain.LogError(Localization.Get("UI_Manager.ButtonTemplateNotFound"));
			return;
		}
		// 克隆并修改名称
		_mpButton = Instantiate(templateButton, menuContent.transform);
		_mpButton.name = "Multi Play";
		// 修改层级
		_mpButton.transform.SetSiblingIndex(1);
		// 修改文字
		_mpButton.GetComponentInChildren<TMPro.TextMeshProUGUI>()?.text = "MULTI PLAY";
	}

	#endregion

	#region[多人模式菜单]

	// 创建多人模式大厅屏幕
	public void CreateLobbyScreen() {
		// 准备和克隆UI容器
		if (!PrepareRootContainers()) return;

		// 设置标签页按钮和内容
		if (!SetupTabButtons()) return;
		if (!SetupTabContents()) return;

		// 细节处理与事件绑定
		SetupMutators();
		BindTabEvents();
		MPMain.LogInfo(Localization.Get("UI_Manager.MultiplayerLobbyUIBuildComplete"));
	}

	// 准备和克隆UI容器, 返回是否成功
	private bool PrepareRootContainers() {
		GameObject screenContent = GameObject.Find(CANVAS_SCREEN_PLAY_PATH);
		if (screenContent == null) return Error(Localization.Get("UI_Manager.PlayScreenContainerNotFound"));

		GameObject? templateScreen = screenContent.transform.Find("Play Menu")?.gameObject;
		if (templateScreen == null) return Error(Localization.Get("UI_Manager.PlayMenuTemplateNotFound"));

		// 克隆大厅屏幕
		_mpScreen = Instantiate(templateScreen, screenContent.transform);
		_mpScreen.name = "Multi Play Menu";
		_mpScreen.transform.SetSiblingIndex(0);

		// 缓存核心容器
		_lobbyPaneContainer = _mpScreen.transform.Find(PLAY_PANE_PATH)?.gameObject;
		if (_lobbyPaneContainer == null) return Error(Localization.Get("UI_Manager.LobbyPaneContainerPathError"));
		_lobbyPaneContainer.name = "Lobby Pane";
		// 修复UI_LerpOpen组件可能存在的目标位置和缩放问题,防止界面打开动画异常
		FixLerpComponent(_lobbyPaneContainer);

		// 缓存模式变体容器
		_mutators = _lobbyPaneContainer.transform.Find("Mutators")?.gameObject;
		if (_mutators == null) return Error(Localization.Get("UI_Manager.MutatorsContainerPathError"));

		// 清理原版不需要的元素
		Destroy(_mpScreen.transform.Find("GamemodeScreen")?.gameObject);
		Destroy(_lobbyPaneContainer.transform.Find("Play Scroll View")?.gameObject);
		Destroy(_lobbyPaneContainer.transform.Find("Tab Selection")?.gameObject);

		return true;
	}

	// 设置标签页按钮, 返回是否成功
	private bool SetupTabButtons() {
		_screenTabs = _lobbyPaneContainer!.transform.Find("Tabs")?.gameObject;
		_screenTabButtons = _screenTabs?.transform.Find("Tab Buttons")?.gameObject;

		if (_screenTabButtons == null) return Error(Localization.Get("UI_Manager.TabButtonContainerNotFound"));

		_tabButtonTemplate = _screenTabButtons.transform.Find("ModeButton_Custom")?.gameObject;
		if (_tabButtonTemplate == null) return Error(Localization.Get("UI_Manager.TabButtonTemplateNotFound"));

		// 配置模板
		_tabButtonTemplate.name = "ModeButton_Template";
		_tabButtonTemplate.transform.Find("Text (TMP)")
			?.gameObject.GetComponent<TextMeshProUGUI>()
			?.text = "TEMPLATE";

		// 创建新按钮
		_newTabButton = Instantiate(_tabButtonTemplate, _screenTabButtons.transform);
		_newTabButton.name = "ModeButton_Lobby";
		_newTabButton.transform.Find("Text (TMP)")
			?.gameObject.GetComponent<TextMeshProUGUI>()
			?.text = "LOBBY";

		_tabButtonTemplate.transform.SetSiblingIndex(1);
		_newTabButton.transform.SetSiblingIndex(2);
		_newTabButton.SetActive(true);

		// 清理其他按钮
		// 跳过0号 1号 2号 -1号 0号是LB图标 1号是标签页按钮 2号是测试模板按钮 -1号是RB图标
		for (int i = _screenTabButtons.transform.childCount - 2; i > 2; i--) {
			Destroy(_screenTabButtons.transform.GetChild(i).gameObject);
		}

		return true;
	}

	// 设置标签页内容, 返回是否成功
	private bool SetupTabContents() {
		// 缓存标签页内容容器
		_screenTabObjects = _lobbyPaneContainer!.transform.Find("Tab Objects")?.gameObject;
		if (_screenTabObjects == null) return Error(Localization.Get("UI_Manager.TabContentContainerNotFound"));

		// 缓存内容模板
		_lobbyPaneTemplate = _screenTabObjects.transform.Find("Play Pane - Scroll View Tab - Custom")?.gameObject;
		if (_lobbyPaneTemplate == null) return Error(Localization.Get("UI_Manager.ContentTemplateNotFound"));

		// 重命名模板并修改顺序
		_lobbyPaneTemplate.name = "Lobby Pane - Scroll View Tab - Template";
		_lobbyPaneTemplate.transform.SetSiblingIndex(0);

		// 清理其他标签页
		for (int i = _screenTabObjects.transform.childCount - 1; i > 0; i--) {
			Destroy(_screenTabObjects.transform.GetChild(i).gameObject);
		}

		// 创建多人大厅面板
		_mpLobbyPane = Instantiate(_lobbyPaneTemplate, _screenTabObjects.transform);
		_mpLobbyPane.name = "Lobby Pane - Scroll View Tab - Lobby";

		// 清理面板内部
		Transform? content = _mpLobbyPane.transform.Find("Viewport/Content");
		if (content != null) {
			foreach (Transform child in content) Destroy(child.gameObject);
		}

		// 添加大厅列表组件
		_mpLobbyPane.AddComponent<UI_LobbyListPane>();
		return true;
	}

	// 配置模式变体标签页
	private bool SetupMutators() {
		if (_mutators == null) return Error(Localization.Get("UI_Manager.MutatorsContainerNotFound"));

		#region[刷新按钮]

		// 克隆一个选项按钮作为刷新按钮
		GameObject refresh = Instantiate(_mutators.transform.Find("Options"), _mutators.transform).gameObject;
		refresh.name = "Refresh";

		var refreshFitter = refresh.GetComponent<ContentSizeFitter>() ?? refresh.AddComponent<ContentSizeFitter>();
		refreshFitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;

		// 修改标签页文字和字体
		var refreshText = refresh.GetComponent<TextMeshProUGUI>() ?? refresh.AddComponent<TextMeshProUGUI>();
		refreshText.enableWordWrapping = false;
		refreshText.overflowMode = TextOverflowModes.Overflow;
		refreshText.font = _mutators.transform.Find("Ironman Toggle/Background/Label (1)")?.GetComponent<TextMeshProUGUI>()?.font;
		refreshText.text = "Refresh";
		refreshText.fontSize = 24;

		// 添加点击事件
		var refreshButton = refresh.AddComponent<Button>();
		refreshButton.onClick.AddListener(() => {
			// 触发大厅列表刷新事件
			MPEventBusGame.NotifyRefreshLobbyList();
		});

		#endregion

		#region[Discord链接]

		// 克隆一个选项按钮作为Discord链接
		GameObject discord = Instantiate(_mutators.transform.Find("Options"), _mutators.transform).gameObject;
		discord.name = "Discord";

		var discordFitter = discord.GetComponent<ContentSizeFitter>() ?? discord.AddComponent<ContentSizeFitter>();
		discordFitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;

		// 修改文字和字体
		var discordText = discord.GetComponent<TextMeshProUGUI>() ?? discord.AddComponent<TextMeshProUGUI>();
		discordText.enableWordWrapping = false;
		discordText.overflowMode = TextOverflowModes.Overflow;
		discordText.font = _mutators.transform.Find("Ironman Toggle/Background/Label (1)")?.GetComponent<TextMeshProUGUI>()?.font;
			discordText.text = "MPMod Discord";
			discordText.fontSize = 24;
		

		// 添加点击事件
		var discordButton = discord.AddComponent<Button>();
		discordButton.onClick.AddListener(() => {
			// 打开Discord链接
			Application.OpenURL("https://discord.gg/DVr4h6Gc9w");
		});

		#endregion

		// 调整层级 0号是标题 1号放刷新按钮 2号放Discord按钮 其他的隐藏
		refresh.transform.SetSiblingIndex(1);
		discord.transform.SetSiblingIndex(2);
		for (int i = 3; i < _mutators.transform.childCount; i++) {
			_mutators.transform.GetChild(i).gameObject.SetActive(false);
		}

		if (_mutators.transform is RectTransform containerRect) {
			LayoutRebuilder.ForceRebuildLayoutImmediate(containerRect);
		}

		return true;
	}

	// 绑定标签页事件
	private void BindTabEvents() {
		if (_screenTabs == null || _newTabButton == null || _mpLobbyPane == null) return;

		var tabGroup = _screenTabs.GetComponent<UI_TabGroup>();
		if (tabGroup == null) return;

		tabGroup.tabs = new List<UI_TabGroup.Tab> {
			new UI_TabGroup.Tab {
				name = "lobby",
				button = _newTabButton.GetComponent<UnityEngine.UI.Button>(),
				tabObject = _mpLobbyPane,
				buttonText = _newTabButton.transform.Find("Text (TMP)")?.GetComponent<TextMeshProUGUI>()
			},
			new UI_TabGroup.Tab {
				name = "template",
				button = _tabButtonTemplate!.GetComponent<UnityEngine.UI.Button>(),
				tabObject = _lobbyPaneTemplate!,
				buttonText = _tabButtonTemplate.transform.Find("Text (TMP)")?.GetComponent<TextMeshProUGUI>(),
				onlyDev = true
			}
		};
	}

	#endregion

	#region[初始化UI关联]

	// 初始化UI关联
	public void Initialize() {
		// 移除点击事件
		_mpButton?.GetComponent<UnityEngine.UI.Button>()?.onClick.RemoveAllListeners();
		// 修改关联菜单
		var menuButtonComponent = _mpButton?.GetComponent<UI_MenuButton>();
		if (menuButtonComponent == null) {
			MPMain.LogError(Localization.Get("UI_Manager.MenuButtonComponentNotFound"));
			return;
		}
		menuButtonComponent.screen = _mpScreen?.GetComponent<UI_MenuScreen>();
		// 初始化函数
		GameObject menu = GameObject.Find(MAIN_MENU_PATH);
		var uI_MenuComponent = menu.GetComponent<UI_Menu>();
		menuButtonComponent.Initialize(uI_MenuComponent);
	}

	#endregion

	#region[Loading界面构建]

	public void CreateLoadingScreen() {		
		loadingTemplate = GameObject.Find("Canvas - Main Menu")?.transform.Find("Loading")?.gameObject;
		if (loadingTemplate == null) {
			MPMain.LogError($"[MP Debug] loadingTemplate can not find");
			return;
		}
		var screenTransform = GameObject.Find(LOADING_SCREEN_PATH).transform;
		newloading = Instantiate(loadingTemplate, screenTransform);
		newloading.AddComponent<UI_LoadingDisplay>();
		// 激活新Loading界面
		newloading.SetActive(true);
		// 调整层级到最前
		newloading.transform.SetSiblingIndex(screenTransform.childCount - 1);
	}

	#endregion

	#region[工具函数]

	// 修复UI_LerpOpen组件可能存在的目标位置和缩放问题
	private void FixLerpComponent(GameObject target) {
		var lerp = target.GetComponent<UI_LerpOpen>();
		if (lerp == null) return;

		// 获取私有字段的 FieldInfo
		// 游戏源码中的变量名：targetPosition, targetSize, rootPositon, rootScale
		Type type = typeof(UI_LerpOpen);
		BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;

		// 可能的字段名列表，考虑到大小写错误
		string[] posFields = { "rootPositon", "rootPosition", "targetPosition" };
		string[] scaleFields = { "rootScale", "targetSize" };
		// 修正目标位置
		foreach (var fieldName in posFields) {
			type.GetField(fieldName, flags)?.SetValue(lerp, Vector3.zero);
		}
		// 修正目标缩放
		foreach (var fieldName in scaleFields) {
			type.GetField(fieldName, flags)?.SetValue(lerp, Vector3.one);
		}
	}

	// 统一的错误日志函数，返回false方便在条件语句中使用
	private bool Error(string msg) {
		MPMain.LogError(msg);
		return false;
	}
	#endregion

	#region[主游戏屏幕显示]

	public void DisplayMessage(string message, UIDisplayType type) {
		switch (type) {
			case UIDisplayType.AscentHeader:
				CL_GameManager.gMan.uiMan.ascentHeader.ShowText(message);
				break;
			case UIDisplayType.TipHeader:
				CL_GameManager.gMan.uiMan.tipHeader.ShowText(message);
				break;
			case UIDisplayType.Header:
				CL_GameManager.gMan.uiMan.header.ShowText(message);
				break;
			case UIDisplayType.HighscoreHeader:
				CL_GameManager.gMan.uiMan.highscoreHeader.ShowText(message);
				break;
			default:
				break;
		}
	}

	#endregion
}
