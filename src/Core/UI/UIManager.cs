using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using WKMPMod.Core;
using WKMPMod.Util;

namespace WKMPMod.UI;

public class UIManager: MonoSingleton<UIManager> {

	const string MainMenuButtons = "Canvas - Main Menu/Main Menu/Main Menu Buttons";


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
				SetupMainMenu();
				MPMain.LogInfo("[MP Debug]TestA");
				break; 
			} 
			default:
				break;
		}
	}

	// 在主菜单添加多人模式按钮
	public void SetupMainMenu() {
		// 找到现有的菜单容器
		GameObject menuContent = GameObject.Find(MainMenuButtons);
		// 找到一个现有的按钮作为模版
		GameObject templateBtn = menuContent.transform.Find("Cosmetics").gameObject;

		// 克隆并修改
		GameObject MPBtn = GameObject.Instantiate(templateBtn, menuContent.transform);
		MPBtn.name = "Multi Play";

		// 修改层级
		MPBtn.transform.SetSiblingIndex(1);

		// 修改文字
		var tmp = MPBtn.GetComponentInChildren<TMPro.TextMeshProUGUI>();
		if (tmp != null) tmp.text = "MULTI PLAY";

		// 修改点击事件
		var btnComponent = MPBtn.GetComponent<UnityEngine.UI.Button>();
		btnComponent.onClick.RemoveAllListeners(); // 移除原有的退出功能
		btnComponent.onClick.AddListener(() => {
			MPMain.LogInfo("[MP Debug]TestB");
		});

	}

}
