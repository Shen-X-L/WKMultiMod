using Steamworks.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using WKMPMod.Core;
using WKMPMod.NetWork;

namespace WKMPMod.UI;

public class UI_LobbyListPane : MonoBehaviour {
	// 模板对象路径
	public const string TEMPLATE_PATH = "Canvas - Screens/Screens/Canvas - Screen - Play/Multi Play Menu/Lobby Pane/Tab Objects/Lobby Pane - Scroll View Tab - Tamplate/Viewport/Content/Mode Selection Button - Endless";
	public GameObject template;

	// 大厅ID与对应UI_LobbyButton的字典,用于快速查找和更新UI
	public Dictionary<ulong, GameObject> LobbyDic = new Dictionary<ulong, GameObject>();

	// 内容容器路径
	public const string CONTENT_PATH = "Viewport/Content";
	public Transform contentTransform;

	// 用来防止刷新大厅生成的新按钮在刷新过程中被点击导致错误,刷新过程中所有按钮不可交互,刷新完成后恢复交互
	public bool interactable = true;
	// 启用时调用MPSteamworks的RefreshLobbyList方法,刷新大厅列表
	private void Start() {
		try {
			SetupTemplate();
		} catch (Exception ex) {
			MPMain.LogError($"[MP Debug] {ex.Message}");
		}
		contentTransform = transform.Find(CONTENT_PATH);
	}

	private void OnEnable() {
		_ = RefreshLobbyList();
	}

	/// <summary>
	/// 刷新大厅列表并创建对应的UI_LobbyButton
	/// </summary>
	public async Task RefreshLobbyList() {
		await MPSteamworks.Instance.RefreshLobbyList();
		// 获取最新的大厅列表
		List<Lobby> lobbies = MPSteamworks.Instance.LastFetchedLobbies;
		HashSet<ulong> activeIds = lobbies.Select(lobby => lobby.Id.Value).ToHashSet();

		// 移除已关闭的大厅对应的UI_LobbyButton
		foreach (var id in LobbyDic.Keys.Where(id => !activeIds.Contains(id)).ToList()) { 
			Destroy(LobbyDic[id]);
		}

		// 添加新的大厅对应的UI_LobbyButton
		foreach (var lobby in lobbies.Where(l => !LobbyDic.ContainsKey(l.Id))) {
			LobbyDic[lobby.Id] = CreateLobbyButton(lobby);
		}
	}

	/// <summary>
	/// 创建大厅按钮并初始化数据
	/// </summary>
	public GameObject CreateLobbyButton(Lobby lobby) {
		GameObject newButton = Instantiate(template, contentTransform);
		newButton.name = $"LobbyButton_{lobby.Id}";
		newButton.SetActive(true); // 确保新按钮可见
		newButton.GetComponent<Button>()?.interactable = interactable; // 设置按钮交互性
		var lobbyButtonComponent = newButton.GetComponent<UI_LobbyButton>();
		if (lobbyButtonComponent != null) {
			lobbyButtonComponent.Initialize(lobby); // 初始化按钮数据
		} else {
			Debug.LogError($"[MP Debug] UI_LobbyButton组件未找到: {newButton.name}");
		}
		return newButton;
	}

	/// <summary>
	/// 切换所有大厅按钮的可操作性
	/// </summary>
	/// <param name="interactable">是否可以点击</param>
	public void SetAllButtonsInteractable(bool interactable) {
		this.interactable = interactable;

		foreach (var buttonObject in LobbyDic.Values) {
			if (buttonObject != null) {
				var button = buttonObject.GetComponent<Button>();
				if (button != null) {
					button.interactable = interactable;
				}
			}
		}
		// 以后给刷新按钮添加交互控制 
		// refreshButton.interactable = interactable;
	}

	#region[初始化克隆对象]
	/// <summary>
	/// 修改原对象模板,添加UI_LobbyButton组件,并移除原有的UI_Gamemode_Button等组件
	/// </summary>
	public void SetupTemplate() {
		if (template != null && template.GetComponent<UI_LobbyButton>() != null) {
			return;
		}

		template = GameObject.Find(TEMPLATE_PATH)?.gameObject;
		if (template == null) {
			throw new Exception("[MP Debug] 模板未找到");
		}

		// 添加UI_LobbyButton组件
		var lobbyButton = template.AddComponent<UI_LobbyButton>();

		if (!template.TryGetComponent<UI_Gamemode_Button>(out var gamemodeButton)) {
			throw new Exception("[MP Debug] UI_Gamemode_Button未找到");
		}
		if (!template.TryGetComponent<UI_CapsuleButton>(out var capsuleButton)) {
			throw new Exception("[MP Debug] UI_CapsuleButton未找到");
		}

		// 获取原UI_Gamemode_Button组件的相关字段(如果存在),以便复用其功能
		lobbyButton.runInProgressDisplay = gamemodeButton.runInProgressDisplay;
		lobbyButton.unlockText = gamemodeButton.unlockText
			?? transform.Find("Lock Image/Unlock Requirement")?.gameObject.GetComponent<TMP_Text>();
		if (lobbyButton.unlockText == null) {
			throw new Exception("[MP Debug] 未解锁信息未找到");
		}

		// 获取原UI_CapsuleButton组件的相关字段(如果存在),以便复用其功能
		lobbyButton.button = template.GetComponent<Selectable>();
		lobbyButton.group = template.GetComponent<CanvasGroup>();
		lobbyButton.unlockIcon = capsuleButton.unlockIcon
			?? transform.Find("Lock Image")?.gameObject.GetComponent<UnityEngine.UI.Image>();
		if (lobbyButton.unlockIcon == null) {
			throw new Exception("[MP Debug] 未解锁图标未找到");
		}

		lobbyButton.showDelayAnimation = capsuleButton.showDelayAnimation;
		// 赋值新的UI_LobbyButton特有的字段
		lobbyButton.lobbyName = gamemodeButton.title
			?? transform.Find("Mode Name")?.gameObject.GetComponent<TMP_Text>();
		if (lobbyButton.lobbyName == null) {
			throw new Exception("[MP Debug] 模式名称->大厅名未找到");
		}

		// 移除不需要的子物体(如果存在),避免显示错误信息
		Destroy(transform.Find("Medal")?.gameObject);
		Destroy(transform.Find("High Score Tracker")?.gameObject);
		// 移除原有的UI_Gamemode_Button和UI_CapsuleButton组件
		DestroyImmediate(gamemodeButton);
		// 暂时不移除 会导致UI_CapsuleContainer数组越界
		//DestroyImmediate(capsuleButton);
		capsuleButton.enabled = false;
	}
	#endregion
}

