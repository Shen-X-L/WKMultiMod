using JetBrains.Annotations;
using Steamworks;
using Steamworks.Data;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.NetWork;
using WKMPMod.Util;

namespace WKMPMod.UI;

public class UI_LobbyListPane : MonoBehaviour {
	// 模板对象路径
	public const string TEMPLATE_PATH = "Canvas - Screens/Screens/Canvas - Screen - Play/Multi Play Menu/Lobby Pane/Tab Objects/Lobby Pane - Scroll View Tab - Template/Viewport/Content/Mode Selection Button - Endless";
	public GameObject? template;

	// 大厅ID与对应UI_LobbyButton的字典,用于快速查找和更新UI
	public Dictionary<ulong, UI_LobbyJoinButton> LobbyDic = new Dictionary<ulong, UI_LobbyJoinButton>();

	// 内容容器路径
	public const string CONTENT_PATH = "Viewport/Content";
	public Transform? contentTransform;

	// 用来防止刷新大厅生成的新按钮在刷新过程中被点击导致错误,刷新过程中所有按钮不可交互,刷新完成后恢复交互
	public bool interactable = true;

	// 事件处理器引用,用于在OnDestroy中解绑事件
	private Func<Task> refreshHandle;

	public void Awake() {
		refreshHandle = async () => {
			MPEventBusGame.NotifyShowLoading(10f);
			await RefreshLobbyList();
			MPEventBusGame.NotifyHideLoading();
		};
		// 刷新事件绑定,当接收到刷新事件时调用RefreshLobbyList方法刷新大厅列表
		MPEventBusGame.OnRefreshLobbyList += refreshHandle;
		// 大厅数据更新事件绑定,当接收到大厅数据更新事件时调用RefreshLobbyList方法刷新大厅列表,以确保UI显示最新的数据
		SteamMatchmaking.OnLobbyDataChanged += OnSteamLobbyDataUpdate;
	}

	public void OnDestroy() {
		MPEventBusGame.OnRefreshLobbyList -= refreshHandle;
		SteamMatchmaking.OnLobbyDataChanged -= OnSteamLobbyDataUpdate;
	}

	// 启用时调用MPSteamworks的RefreshLobbyList方法,刷新大厅列表
	private void Start() {
		try {
			SetupTemplate();
		} catch (Exception ex) {
			MPMain.LogError(Localization.Get("UI_LobbyListPane.ButtonTemplateBuildFailed", ex.Message));
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
			Destroy(LobbyDic[id].gameObject); 
			LobbyDic.Remove(id);
		}

		if (template == null) {
			MPMain.LogError(Localization.Get("UI_LobbyListPane.LobbyButtonTemplateNull"));
			return;
		}

		StartCoroutine(CreateLobby());

		// 添加新的大厅对应的UI_LobbyButton
		IEnumerator CreateLobby() {
			foreach (var lobby in lobbies.Where(l => !LobbyDic.ContainsKey(l.Id))) {
				var newButton = CreateLobbyButton(lobby);
				if (newButton != null) {
					LobbyDic[lobby.Id] = newButton;
					lobby.Refresh();
				}
				yield return null;
			}
			yield break;
		}

	}

	/// <summary>
	/// 创建大厅按钮并初始化数据
	/// </summary>
	public UI_LobbyJoinButton? CreateLobbyButton(Lobby lobby) {
		if (template == null) return null;
		GameObject? newButtonObj = Instantiate(template, contentTransform);
		if (newButtonObj == null) {
			MPMain.LogError(Localization.Get("UI_LobbyListPane.LobbyButtonCloneFailed"));
			return null;
		};
		newButtonObj.name = $"LobbyButton_{lobby.Id}";
		newButtonObj.SetActive(true);
		// 获取UI_LobbyButton组件并初始化数据
		Button btnComp = newButtonObj.GetComponent<Button>() ?? newButtonObj.AddComponent<Button>();
		UI_LobbyJoinButton lobbyBtn = newButtonObj.GetComponent<UI_LobbyJoinButton>();
		// 设置是否可点击
		btnComp.interactable = interactable;

		if (lobbyBtn != null) {
			lobbyBtn.Initialize(lobby);
			return lobbyBtn;
		} else {
			MPMain.LogError(Localization.Get("UI_LobbyListPane.LobbyButtonComponentNotFound",newButtonObj.name));
			Destroy(newButtonObj);
			return null;
		}
	}

	/// <summary>
	/// 切换所有大厅按钮的可操作性
	/// </summary>
	/// <param name="interactable">是否可以点击</param>
	public void SetAllButtonsInteractable(bool interactable) {
		this.interactable = interactable;

		foreach (var buttonObject in LobbyDic.Values) {
			if (buttonObject != null) {
				buttonObject.GetComponent<Button>()?.interactable = interactable;
			}
		}
		// 以后给刷新按钮添加交互控制 
		// refreshButton.interactable = interactable;
	}

	/// <summary>
	/// 全局 Steam 回调：当任何一个大厅的数据同步到本地时触发
	/// </summary>
	private void OnSteamLobbyDataUpdate(Lobby lobby) {
		// 利用 Dictionary 快速定位对应的按钮
		if (LobbyDic.TryGetValue(lobby.Id.Value, out var button)) {
			if (button != null) {
				button.OnLobbyDataUpdated(lobby);
			}
		}
	}

	#region[初始化克隆对象]
	/// <summary>
	/// 修改原对象模板,添加UI_LobbyButton组件,并移除原有的UI_Gamemode_Button等组件
	/// </summary>
	public void SetupTemplate() {
		if (template != null && template.GetComponent<UI_LobbyJoinButton>() != null) {
			return;
		}

		template = GameObject.Find(TEMPLATE_PATH)?.gameObject;
		if (template == null) 
			throw new Exception("Template not found at path: " + TEMPLATE_PATH);

		// 添加UI_LobbyButton组件
		var lobbyButton = template.AddComponent<UI_LobbyJoinButton>();

		if (!template.TryGetComponent<UI_Gamemode_Button>(out var gamemodeButton))
			throw new Exception("UI_Gamemode_Button component missing on template");

		if (!template.TryGetComponent<UI_CapsuleButton>(out var capsuleButton))
			throw new Exception("UI_CapsuleButton component missing on template");

		// 获取原UI_Gamemode_Button组件的相关字段(如果存在),以便复用其功能
		lobbyButton.runInProgressDisplay = gamemodeButton.runInProgressDisplay;
		lobbyButton.unlockText = gamemodeButton.unlockText
			?? template.transform.Find("Lock Image/Unlock Requirement")?.gameObject.GetComponent<TMP_Text>();
		if (lobbyButton.unlockText == null) throw new Exception("Unlock text component missing");

		// 获取原UI_CapsuleButton组件的相关字段(如果存在),以便复用其功能
		lobbyButton.button = template.GetComponent<Selectable>();
		lobbyButton.group = template.GetComponent<CanvasGroup>();
		lobbyButton.unlockIcon = capsuleButton.unlockIcon
			?? template.transform.Find("Lock Image")?.gameObject.GetComponent<UnityEngine.UI.Image>();
		if (lobbyButton.unlockIcon == null) throw new Exception("Lock Image component missing");
		

		lobbyButton.showDelayAnimation = capsuleButton.showDelayAnimation;
		// 赋值新的UI_LobbyButton特有的字段
		lobbyButton.lobbyName = gamemodeButton.title
			?? template.transform.Find("Mode Name")?.gameObject.GetComponent<TMP_Text>();
		if (lobbyButton.lobbyName == null) throw new Exception("Mode Name component missing");
		lobbyButton.hostAvatar = template.transform.Find("Roach Counter")?.GetComponent<UnityEngine.UI.Image>();
		if (lobbyButton.hostAvatar == null) 
			MPMain.LogError(Localization.Get("UI_LobbyJoinButton.RoachCounterNotFound"));
		
		lobbyButton.hostName = template.transform.Find("Roach Counter/Roaches")?.GetComponent<TMP_Text>();
		if (lobbyButton.hostName == null) 
			MPMain.LogError(Localization.Get("UI_LobbyJoinButton.RoachCounterRoachesNotFound"));
		lobbyButton.btnComp = template.GetComponent<Button>() ?? template.AddComponent<Button>();
		lobbyButton.lobbyImage = GetComponent<UnityEngine.UI.Image>();
		if (lobbyButton.lobbyImage == null)
			MPMain.LogError(Localization.Get("UI_LobbyJoinButton.RoachCounterNotFound"));

		// 初始化一些基础状态
		lobbyButton.btnComp.onClick.RemoveAllListeners();
		template.transform.Find("Roach Counter")?.gameObject.SetActive(true);
		if (lobbyButton.hostAvatar != null) lobbyButton.hostAvatar.enabled = false;
		if (lobbyButton.hostName != null) lobbyButton.hostName.text = "Fetching...";

		// 预设不需要根据大厅变化的文字
		if (lobbyButton.unlockText != null) {
			lobbyButton.unlockText.text = Localization.Get("UI_LobbyJoinButton.CustomGamemodeNotice");
		}

		// 移除不需要的子物体(如果存在),避免显示错误信息
		Destroy(template.transform.Find("Medal")?.gameObject);
		Destroy(template.transform.Find("High Score Tracker")?.gameObject);
		// 移除原有的UI_Gamemode_Button和UI_CapsuleButton组件
		DestroyImmediate(gamemodeButton);
		DestroyImmediate(capsuleButton);
	}
	#endregion
}

