using DG.Tweening;
using Steamworks;
using Steamworks.Data;
using System;
using System.Collections;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using WKMPMod.Core;
using WKMPMod.NetWork;

namespace WKMPMod.UI;

public class UI_LobbyButton: MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, ISelectHandler, IDeselectHandler {
	public Lobby lobby;                         // 关联的大厅数据
	public M_Gamemode gamemode;                 // 关联的游戏模式
	public bool isOfficialGamemodes;			// 是否为官方游戏模式(影响显示逻辑)

	#region[原UI_Gamemode_Button字段]
	public UI_LerpOpen runInProgressDisplay;    // 进行中标识的动画组件
	private bool isHovering;                    // 是否正在悬停/选中
	public TMP_Text unlockText;                 // 锁定原因文本(显示在锁定图标旁边，解释为什么不可加入)
	#endregion

	#region[原UI_CapsuleButton字段]
	public float showDelayAnimation;            // 显示动画延迟时间
	public Selectable button;                  // 按钮组件引用
	public CanvasGroup group;					// CanvasGroup组件(用于控制透明度和交互)
	public UnityEngine.UI.Image unlockIcon;     // 未解锁时显示的锁定图标		
	#endregion

	public TMP_Text lobbyName;                  // 大厅名称文本
	public UnityEngine.UI.Image hostAvatar;     // 房主头像
	public TMP_Text hostName;                   // 房主名

	/// <summary>
	/// 初始化按钮 - 设置点击事件、图标、标题和统计文本
	/// </summary>
	public void Initialize(Lobby lobby) {
		hostAvatar = transform.Find("Roach Counter")?.gameObject.GetComponent<UnityEngine.UI.Image>();
		if (hostAvatar == null) {
			MPMain.LogError("[MP Debug] 蟑螂图标->主机头像未找到");
		}

		hostName = transform.Find("Roach Counter/Roaches")?.gameObject.GetComponent<TMP_Text>();
		if (hostName == null) {
			MPMain.LogError("[MP Debug] 蟑螂数量->主机名称未找到");
		}

		// 显示统计文本(目前用来显示房主名称,后续可以改成显示其他统计数据)
		transform.Find("Roach Counter")?.gameObject.SetActive(true);

		// 关联大厅数据
		this.lobby = lobby;
		// 移除之前的点击事件监听,避免重复添加
		var button = GetComponent<Button>();
		button.onClick.RemoveAllListeners();
		// 添加点击事件监听
		GetComponent<Button>().onClick.AddListener(() => {
			// 设置状态
			Joining();
			// 加入大厅
			MPSteamworks.Instance.JoinRoom(lobby, (success) => {
				if (success) {
					JoinSuccess();
				} else {
					JoinFailed();
				}
			});
		});

		// 获取游戏模式数据
		isOfficialGamemodes = MPGameModeManager.TryGetGameMode(lobby.GetData("gamemode"), out var gamemode);

		// 更新锁定图标显示
		if (unlockIcon != null) {
			unlockIcon.gameObject.SetActive(!isOfficialGamemodes);  // 自定义游戏模式显示锁定图标,官方游戏模式不显示
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

		unlockText.text = "[MP Debug] this lobby using custom gamemodes";
		// 设置标题(支持自定义名称)
		if (!string.IsNullOrEmpty(lobby.GetData("name"))) {
			lobbyName.text = lobby.GetData("name");
		} else {
			lobbyName.text = lobby.Id.ToString();
		}

		//string ownerIdStr = lobby.GetData("owner");


		// 预设状态
		hostName.text = "Fetching...";
		hostAvatar.enabled = false; // 先隐藏,等加载完再现. 以后写成默认加载中头像

		// 加载房主信息(头像和名称)
		_ = TrackAndLoadOwnerInfo();
	}

	private async Task TrackAndLoadOwnerInfo() {
		int retryCount = 0;
		const int maxRetries = 5;

		// 循环检查 ID 是否有效 (针对 lobby.Owner 延迟对齐的情况)
		while (this != null && (lobby.Owner.Id == 0)) {
			if (retryCount >= maxRetries) {
				MPMain.LogWarning($"[MP Debug] 无法获取房主ID, 使用手动设置的owner, 大厅ID: {lobby.Id}");
				hostName.text = "Unknown Host";
				break;
			}

			//强制刷新大厅数据
			lobby.Refresh();

			await Task.Delay(500); // 每次等 0.5 秒
			retryCount++;
		}

		if (this == null) return;

		var owner = lobby.Owner;
		if (lobby.Owner.Id == 0 && ulong.TryParse(lobby.GetData("owner"), out var ownerId)) {
			owner = new Friend(ownerId);

		}
			 
		hostName.text = string.IsNullOrEmpty(owner.Name) ? "Loading Name..." : owner.Name;

		// 异步加载头像 (这会强制触发 Steam 资料同步)
		var avatarResult = await owner.GetMediumAvatarAsync();

		if (this == null) return;

		// 最终赋值
		if (avatarResult.HasValue && hostAvatar != null) {
			var texture = SteamManager.ConvertSteamIcon(avatarResult.Value);
			hostAvatar.sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f));
			hostAvatar.enabled = true;

			hostName.text = owner.Name;
			MPMain.LogInfo($"[MP Debug] 成功加载房主: {owner.Name}");
			return;
		}
		MPMain.LogError($"[MP Debug] 无法加载房主头像, 大厅ID: {lobby.Id}, OwnerID: {owner.Id}, OwnerName: {owner.Name}");
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
	// 加入中 - 目前没有额外逻辑 后续实现Loading弹窗
	public void Joining() {
		MPCore.Instance.SetStatus(MPStatus.LOBBY_MASK, MPStatus.JoiningLobby);
		// 禁止按钮点击
		GetComponent<Button>().interactable = false;
		// 禁止同一标签页内按钮点击
		var pane = GetComponentInParent<UI_LobbyListPane>();
		if (pane != null) {
			pane.SetAllButtonsInteractable(false);
		}
	}
	// 加入失败 - 目前没有额外逻辑 后续实现弹窗提示失败
	public void JoinFailed() {
		MPCore.Instance.SetStatus(MPStatus.LOBBY_MASK, MPStatus.LobbyConnectionError);
		// 恢复按钮点击
		GetComponent<Button>().interactable = true;
		// 恢复同一标签页内按钮点击
		var pane = GetComponentInParent<UI_LobbyListPane>();
		if (pane != null) {
			pane.SetAllButtonsInteractable(true);
		}
	}

	// 加入成功 - 目前没有额外逻辑 后续实现Loading弹窗关闭
	public void JoinSuccess() {
		MPCore.Instance.SetStatus(MPStatus.LOBBY_MASK, MPStatus.InLobby);
	}

	#endregion

	#region[动画相关]
	/// <summary>
	/// 显示按钮动画 - 通常在容器启用时调用，实现按钮逐个出现的效果
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

