using DG.Tweening;
using Steamworks.Data;
using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using WKMPMod.Core;
using WKMPMod.NetWork;

namespace WKMPMod.UI;

public class UI_LobbyButton: MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, ISelectHandler, IDeselectHandler {
	public Lobby lobby;							// 关联的大厅数据
	public M_Gamemode gamemode;                 // 关联的游戏模式
	public bool isCustomGamemodes;				// 是否为自定义游戏模式(影响显示逻辑)

	#region[原UI_Gamemode_Button字段]
	public UI_LerpOpen runInProgressDisplay;    // 进行中标识的动画组件
	private bool isHovering;                    // 是否正在悬停/选中
	#endregion

	#region[原UI_CapsuleButton字段]
	public float showDelayAnimation;			// 显示动画延迟时间
	private Selectable button;					// 按钮组件引用
	private CanvasGroup group;					// CanvasGroup组件(用于控制透明度和交互)
	public UnityEngine.UI.Image unlockIcon;		// 未解锁时显示的锁定图标
	#endregion

	public TMP_Text lobbyName;                  // 大厅名称文本
	public UnityEngine.UI.Image hostAvatar;     // 房主头像
	public TMP_Text hostName;                   // 房主名
	private void Start() {
		button = GetComponent<Selectable>();
		group = GetComponent<CanvasGroup>();
	}

	/// <summary>
	/// 初始化按钮 - 设置点击事件、图标、标题和统计文本
	/// </summary>
	public void Initialize() {
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
		isCustomGamemodes = MPGameModeManager.TryGetGameMode(lobby.GetData("gamemode"), out var gamemode);
		this.gamemode = gamemode;

		// 更新锁定图标显示
		if (unlockIcon != null) {
			unlockIcon.gameObject.SetActive(isCustomGamemodes);  // 自定义游戏模式显示锁定图标,官方游戏模式不显示
		}
		// 设置按钮交互和透明度
		if (group != null) {
			group.interactable = !isCustomGamemodes;      
			group.alpha = isCustomGamemodes ? 0.5f : 1f; 
		}
		// 官方游戏模式显示胶囊图标,自定义游戏模式不显示(后续可以考虑添加自定义图标支持)
		if (!isCustomGamemodes) {
			// 设置胶囊图标
			GetComponent<UnityEngine.UI.Image>()?.sprite = gamemode.capsuleArt;
		}

		// 设置标题(支持自定义名称)
		if (!string.IsNullOrEmpty(lobby.GetData("name"))) {
			lobbyName.text = lobby.GetData("name");
		} else {
			lobbyName.text = lobby.Id.ToString();
		}

		// 设置房主头像 - 需要异步加载Steam头像
		// 应该写成一个独立的函数来加载头像,但为了简化代码直接写在这里了
		((Action)(async () => {
			// 获取房主的Steam头像
			var avatar = await SteamManager.GetAvatar(lobby.Owner.Id);
			if (this == null || avatar == null) {
				return;
			}

			var texture = SteamManager.ConvertSteamIcon((Steamworks.Data.Image)avatar);
			if (hostAvatar != null) { // 增加对 UI 组件的空检查
				hostAvatar.sprite = Sprite.Create(
					texture,
					new Rect(0, 0, texture.width, texture.height),
					new Vector2(0.5f, 0.5f));
			}
		}))();

		// 设置房主名称
		hostName.text = lobby.Owner.Name;
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
	}
	// 加入失败 - 目前没有额外逻辑 后续实现弹窗提示失败
	public void JoinFailed() {
		MPCore.Instance.SetStatus(MPStatus.LOBBY_MASK, MPStatus.LobbyConnectionError);
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

