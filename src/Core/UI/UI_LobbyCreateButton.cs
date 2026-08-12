using Steamworks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using WKMPMod.Component;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.NetWork;
using WKMPMod.Util;

namespace WKMPMod.UI;

public class UI_LobbyCreateButton : MonoBehaviour {

	public Button? button; // 按钮组件引用
	public UI_GamemodeScreen_Panel? gamemodePanel; // 游戏模式详情面板引用
	public List<Button> otherButtons = new List<Button>(); // 同一标签页内的其他按钮列表(用于控制交互状态)

	private void Awake() {
		// 强制清理并重新绑定
		Button oldBtn = GetComponent<Button>();
		if (oldBtn != null) {
			// 仅仅 RemoveAllListeners 不够, 因为那不包含 Inspector 里的持久化事件
			// 可以通过把按钮的 onClick 设为一个新的 UnityEvent 来强行覆盖
			oldBtn.onClick = new Button.ButtonClickedEvent();
			button = oldBtn;
		} else {
			button = gameObject.AddComponent<Button>();
		}
		button.onClick.AddListener(CreateLobby);
	}

	private void Start() {
		// 自动获取同级容器下的所有按钮(排除自己)
		if (transform.parent != null) {
			foreach (var btn in transform.parent.GetComponentsInChildren<Button>()) {
				if (btn != button) otherButtons.Add(btn);
			}
		}
	}

	// 创建大厅的异步方法
	public async void CreateLobby() {
		var name = SteamClient.Name;
		MPMain.LogInfo(Localization.Get("MPCore.CreatingLobby", name));

		// 预设置大厅数据
		var lobbyData = new Dictionary<string, string>() {
			{ MPKeys.LOBBY_NAME, name + "'s game" },         // 大厅名称
		};

		// 设置状态为正在创建
		Creating();

		try {
			// 直接 await 异步版本
			bool success = await MPSteamworks.Instance.CreateRoomAsync(8, lobbyData);

			if (this == null || gameObject == null) return;

			if (success) {
				// 连接成功后的逻辑处理
				CreateSuccess();
			} else {
				// 失败处理
				CreateFailed();
			}
		} catch (Exception ex) {
			if (this == null) return;
			// 捕获任何未预料的崩溃
			CreateFailed();
			MPMain.LogError(Localization.Get("UI_LobbyCreateButton.CreateLobbyFailed", ex.Message));
		}
	}

	#region[大厅创建事件回调]

	// 创建中 - 目前没有额外逻辑
	public void Creating() {
		MPCore.SetStatus(MPStatus.LOBBY_MASK, MPStatus.JoiningLobby);
		// 禁止同一标签页内按钮点击
		button?.interactable = false;
		foreach (var btn in otherButtons) {
			btn.interactable = false;
		}
		// 显示Loading弹窗
		MPEventBusGame.NotifyShowLoading(10f);
	}

	// 创建失败 - 目前没有额外逻辑
	public void CreateFailed() {
		MPCore.SetStatus(MPStatus.LOBBY_MASK, MPStatus.LobbyConnectionError);
		MPCore.Instance.ResetStateVariables();
		// 恢复同一标签页内按钮点击
		button?.interactable = true;
		foreach (var btn in otherButtons) {
			btn.interactable = true;
		}
		// 关闭Loading弹窗
		MPEventBusGame.NotifyHideLoading();
	}

	// 创建成功 - 目前没有额外逻辑 后续实现Loading弹窗关闭
	public void CreateSuccess() {
		MPCore.SetStatus(MPStatus.LOBBY_MASK, MPStatus.InLobby);
		//MPCore.SetStatus(MPStatus.INIT_MASK, MPStatus.Initialized);
		// 关闭Loading弹窗
		MPEventBusGame.NotifyHideLoading();
		// 加载游戏模式
		if (gamemodePanel == null) {
			MPMain.LogError(Localization.Get("UI_LobbyCreateButton.GameModeDetailPanelNull"));
			return;
		}
		gamemodePanel.LoadGamemode();
	}
	#endregion
}
