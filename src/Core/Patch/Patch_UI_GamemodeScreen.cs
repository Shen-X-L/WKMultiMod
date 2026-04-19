using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using WKMPMod.Core;
using WKMPMod.UI;
using static System.Collections.Specialized.BitVector32;
using static UI_TabGroup;
using static UnityEngine.GUI;
using Object = UnityEngine.Object;

namespace WKMPMod.Patch;

[HarmonyPatch(typeof(UI_GamemodeScreen), nameof(UI_GamemodeScreen.Initialize))]
public class Patch_UI_GamemodeScreen {
	static void Postfix(UI_GamemodeScreen __instance, M_Gamemode mode) {
		// 获取刚刚显示出来的面板实例
		string panelId = mode.gamemodePanel.id;
		if (!__instance.activePanels.TryGetValue(panelId, out var panel)) return;

		// 检查是否已经注入过按钮, 防止重复
		if (panel.gameObject.GetComponentInChildren<UI_LobbyCreateButton>() != null) return;

		// 找 开始游戏 按钮作为我们的模板
		Transform? startButton = panel.transform.Find("Pages/Gamemode_Info_Screen/Tab Selection Hor/Play");
		// 如果是不是Play 即 "Escape" "Start Run" 则有详情界面
		if (startButton.GetComponentInChildren<TextMeshProUGUI>()?.text != "Play")
			startButton = panel.transform.Find("Pages/Gamemode_NewGame_Screen/Tab Selection Hor/Play");
		if (startButton != null) {
			// 克隆 开始游戏 按钮作为我们的模板
			GameObject lobbyBtnObj = Object.Instantiate(startButton.gameObject, startButton.parent);
			lobbyBtnObj.name = "Multi Play";

			// 调整顺序
			lobbyBtnObj.transform.SetSiblingIndex(startButton.GetSiblingIndex() + 1);

			// 添加脚本
			var createScript = lobbyBtnObj.AddComponent<UI_LobbyCreateButton>();
			createScript.gamemodePanel = panel; // 绑定引用

			// 修改文本
			var tmp = lobbyBtnObj.GetComponentInChildren<TMP_Text>();
			if (tmp != null) tmp.text = "Multi Play";

			// 处理存档显示逻辑
			panel.noSaveObjects.Add(lobbyBtnObj);
			panel.hasSaveObjects.Add(lobbyBtnObj);
		}
	}
}