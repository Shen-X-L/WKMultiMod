using DG.Tweening;
using Newtonsoft.Json;
using Steamworks;
using Steamworks.Data;
using System;
using System.Collections;
using System.Diagnostics;
using System.Threading.Tasks;
using TMPro;
using Unity.Entities.UniversalDelegates;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.NetWork;
using WKMPMod.Util;
using static WKMPMod.Core.MPGameModeManager;

namespace WKMPMod.UI;

public class UI_LobbyJoinButton : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, ISelectHandler, IDeselectHandler {
	public Lobby lobby;                         // 关联的大厅数据
	public M_Gamemode? gamemode;                // 关联的游戏模式
	public bool isOfficialGamemodes;            // 是否为官方游戏模式(影响显示逻辑)
	public GameModeData? gameModeData;           // 游戏模式数据(仅官方游戏模式有效,包含名称、图标、饰品和设置等信息)

	#region[原UI_Gamemode_Button字段]
	public UI_LerpOpen? runInProgressDisplay;   // 进行中标识的动画组件
	private bool isHovering;                    // 是否正在悬停/选中
	public TMP_Text? unlockText;                // 锁定原因文本(显示在锁定图标旁边, 解释为什么不可加入)
	#endregion

	#region[原UI_CapsuleButton字段]
	public float showDelayAnimation;            // 显示动画延迟时间
	public Selectable? button;                  // 按钮组件引用
	public CanvasGroup? group;                  // CanvasGroup组件(用于控制透明度和交互)
	public UnityEngine.UI.Image? unlockIcon;    // 未解锁时显示的锁定图标		
	#endregion

	public TMP_Text? lobbyName;                 // 大厅名称文本
	public UnityEngine.UI.Image? hostAvatar;    // 房主头像
	public TMP_Text? hostName;                  // 房主名
	public Button? btnComp;                      // 按钮引用
	public UnityEngine.UI.Image? lobbyImage;    // 大厅图标

	/// <summary>
	/// 初始化按钮 - 设置点击事件、图标、标题和统计文本
	/// </summary>
	public void Initialize(Lobby lobby) {
		// 关联大厅数据
		this.lobby = lobby;
		// 添加点击事件监听
		btnComp!.onClick.AddListener(async () => {
			// 禁用按钮并显示加入中状态
			Joining();
			try {
				// await 连接大厅,等待结果
				bool success = await MPSteamworks.Instance.JoinRoomAsync(lobby);
				if (success) {
					// 加入成功,更新状态
					JoinSuccess();
				} else {
					// 加入失败,恢复按钮交互并显示错误状态
					JoinFailed();
				}
			} catch (Exception ex) {
				// 连接过程中发生异常,记录错误并恢复按钮交互
				MPMain.LogError(Localization.Get("UI_LobbyJoinButton.JoinLobbyFailed", ex.Message));
				JoinFailed();
			}
		});

		unlockIcon?.gameObject.SetActive(false);

		// 获取游戏模式数据
		var rawData = lobby.GetData("gamemode");
		try {
			gameModeData = JsonConvert.DeserializeObject<GameModeData>(rawData);
		} catch (JsonException ex) {
			MPMain.LogError($"[MP Debug] JSON 格式解析失败,原始数据: {rawData} 错误: {ex.Message}");
		}

		isOfficialGamemodes = TryGetGameMode(gameModeData?.gameModeName ?? "", out var gamemode);
		// 自定义游戏模式显示锁定图标,显示未知游戏模式文本
		if (!isOfficialGamemodes) {
			unlockIcon?.gameObject.SetActive(true);
			unlockText?.text = "Unknown gamemode";
		}

		var v1 = new Version(MPMain.ModVersion);
		if (!Version.TryParse(lobby.GetData("modversion"), out var v2) ||
			v1.Major != v2.Major || v1.Minor != v2.Minor) {
			unlockIcon?.gameObject.SetActive(true);
			string modVersion = lobby.GetData("modversion");
			unlockText?.text = $"Incompatible mod version: {(string.IsNullOrEmpty(modVersion) ? "unknown" : modVersion)}";
		}

		// 设置按钮交互和透明度
		if (group != null) {
			group.interactable = isOfficialGamemodes;
			group.alpha = isOfficialGamemodes ? 1f : 0.5f;
		}

		// 官方游戏模式显示胶囊图标,自定义游戏模式不显示(后续可以考虑添加自定义图标支持)
		if (isOfficialGamemodes) {
			this.gamemode = gamemode;
			// 设置胶囊按钮图标
			GetComponent<UnityEngine.UI.Image>()?.sprite = gamemode.capsuleArt;
		}

		// 设置标题(支持自定义名称)
		string nameData = lobby.GetData("name");
		lobbyName?.text = !string.IsNullOrEmpty(nameData) ? nameData : lobby.Id.ToString();

		// 加载房主信息(头像和名称)
		StartCoroutine(TrackAndLoadOwnerInfoCoroutine());
	}

	/// <summary>
	/// 异步加载房主信息, 包括头像和名称
	/// </summary>
	private IEnumerator TrackAndLoadOwnerInfoCoroutine() {
		if (this == null) yield break;

		// 获取 Owner 数据
		var owner = lobby.Owner;
		if (owner.Id == 0 && ulong.TryParse(lobby.GetData("owner"), out var ownerId)) {
			owner = new Friend(ownerId);
		}

		hostName.text = string.IsNullOrEmpty(owner.Name) ? "Loading Name..." : owner.Name;

		// 异步加载头像
		// 由于 GetMediumAvatarAsync 返回 Task, 我们可以在协程里等待 Task 完成
		var avatarTask = owner.GetMediumAvatarAsync();

		// 轮询直到 Task 完成, 不阻塞主线程
		while (!avatarTask.IsCompleted) {
			yield return null;
		}

		// 空对象 退出协程
		if (this == null) yield break;

		var avatarResult = avatarTask.Result;

		// 赋值头像
		if (avatarResult.HasValue && hostAvatar != null) {
			var texture = SteamManager.ConvertSteamIcon(avatarResult.Value);
			hostAvatar.sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f));
			hostAvatar.enabled = true;
			hostName.text = owner.Name;
		}
	}

	/// <summary>
	/// 当大厅数据发生变动时, 由父面板调用此方法同步显示
	/// </summary>
	public void OnLobbyDataUpdated(Lobby updatedLobby) {
		// 更新本地大厅引用, 确保后续点击加入时用的是最新对象
		this.lobby = updatedLobby;

		// 重新检测游戏模式
		isOfficialGamemodes = MPGameModeManager.TryGetGameMode(lobby.GetData("gamemode"), out var gamemode);

		// 更新名称
		if (!string.IsNullOrEmpty(lobby.GetData("name"))) {
			lobbyName?.text = lobby.GetData("name");
		}

		// 更新 UI 状态
		if (unlockIcon != null) unlockIcon.gameObject.SetActive(!isOfficialGamemodes);
		if (group != null) {
			group.interactable = isOfficialGamemodes;
			group.alpha = isOfficialGamemodes ? 1f : 0.5f;
		}

		if (isOfficialGamemodes && gamemode != null) {
			this.gamemode = gamemode;
			GetComponent<UnityEngine.UI.Image>().sprite = gamemode.capsuleArt;
		}

		// 重新加载房主信息
		if (hostName?.text == "Fetching..." || hostName?.text == "Loading Name...") {
			StartCoroutine(TrackAndLoadOwnerInfoCoroutine());
		}
	}

	#region[事件接口实现]

	/// <summary>
	/// 鼠标悬停进入 - 播放悬停动画
	/// </summary>
	public void OnPointerEnter(PointerEventData eventData) {
		if (button != null && button.interactable) {
			// 脉冲缩放效果(快速小幅度)
			transform.DOPunchScale(Vector3.one * 0.04f, 0.25f, 5, 0.5f);
			// 放大到1.05倍
			transform.DOScale(1.05f, 0.25f);
		}
		isHovering = true;
	}

	/// <summary>
	/// 鼠标悬停离开 - 恢复原始大小
	/// </summary>
	public void OnPointerExit(PointerEventData eventData) {
		if (button != null && button.interactable) {
			transform.DOScale(1f, 0.25f);
		}
		isHovering = false;
	}

	/// <summary>
	/// 键盘/手柄选择进入 - 播放选中动画
	/// </summary>
	public void OnSelect(BaseEventData data) {
		if (button != null && button.interactable) {
			// 脉冲缩放效果
			transform.DOPunchScale(Vector3.one * 0.04f, 0.25f, 5, 0.5f);
			// 放大到1.05倍
			transform.DOScale(1.05f, 0.25f);
		}
		isHovering = true;
	}

	/// <summary>
	/// 键盘/手柄选择离开 - 恢复原始大小
	/// </summary>
	public void OnDeselect(BaseEventData data) {
		if (button != null && button.interactable) {
			transform.DOScale(1f, 0.25f);
		}
		isHovering = false;
	}

	#endregion

	#region[大厅加入事件回调]
	// 加入中 - 目前没有额外逻辑
	public void Joining() {
		MPCore.SetStatus(MPStatus.LOBBY_MASK, MPStatus.JoiningLobby);
		// 禁止同一标签页内按钮点击
		button!.interactable = false;
		var pane = GetComponentInParent<UI_LobbyListPane>();
		if (pane != null) {
			pane.SetAllButtonsInteractable(false);
		}
		// 显示Loading弹窗
		// 显示最长10秒的Loading,实际关闭由JoinSuccess或JoinFailed触发
		MPEventBusGame.NotifyShowLoading(10f);
	}
	// 加入失败 - 目前没有额外逻辑
	public void JoinFailed() {
		MPCore.SetStatus(MPStatus.LOBBY_MASK, MPStatus.LobbyConnectionError);
		// 恢复同一标签页内按钮点击
		button!.interactable = true;
		var pane = GetComponentInParent<UI_LobbyListPane>();
		if (pane != null) {
			pane.SetAllButtonsInteractable(true);
		}
		// 关闭Loading弹窗
		MPEventBusGame.NotifyHideLoading();
	}

	// 加入成功 - 目前没有额外逻辑
	public void JoinSuccess() {
		MPCore.SetStatus(MPStatus.LOBBY_MASK, MPStatus.InLobby);
		// 关闭Loading弹窗
		MPEventBusGame.NotifyHideLoading();
	}

	#endregion

	#region[动画相关]
	/// <summary>
	/// 显示按钮动画 - 通常在容器启用时调用, 实现按钮逐个出现的效果
	/// </summary>
	public void Show() {
		if (button != null && button.gameObject.activeInHierarchy) {
			StartCoroutine(ShowAnimation());
		}
	}

	/// <summary>
	/// 显示动画协程 - 延迟后播放脉冲缩放效果
	/// </summary>
	public IEnumerator ShowAnimation() {
		yield return new WaitForSeconds(showDelayAnimation);
		transform.DOPunchScale(Vector3.one * 0.04f, 0.5f, 5, 0.5f);
	}
	#endregion
}

