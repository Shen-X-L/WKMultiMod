using Steamworks;
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using WKMultiMod.src.Core;

namespace WKMultiMod.src.Component;

// BillboardComponent: 使文本框始终面向摄像机
public class LootAt : MonoBehaviour {
	private Camera mainCamera;

	[Header("Scaling Settings")]
	public bool maintainScreenSize = true;
	public float baseScale = 1f; // 初始缩放比例
	public float maxScale = MPMain.NameTagSizeMax;  // 最大缩放
	public float minScale = MPMain.NameTagSizeMin;  // 最小缩放

	void LateUpdate() {
		// 持续检查并尝试获取主摄像机
		if (mainCamera == null) {
			mainCamera = Camera.main;

			// 如果仍然找不到,则跳过本帧
			if (mainCamera == null) {
				return;
			}
		}

		// 使 Transform (文本框) 朝向摄像机
		transform.rotation = mainCamera.transform.rotation;

		// 缩放抵消透视
		if (maintainScreenSize) {
			// 计算物体到相机的距离
			float distance = Vector3.Distance(transform.position, mainCamera.transform.position);

			// 核心公式：缩放值 = 距离 * 基础大小 * 修正系数
			float newScale = distance * baseScale * 0.1f;

			// 限制最小值,防止离太近时消失
			newScale = Mathf.Clamp(newScale, minScale, maxScale);

			transform.localScale = new Vector3(newScale, newScale, newScale);
		}
	}
}

// 这个组件用来修改玩家名字
public class RemoteTag : MonoBehaviour {
	private TextMesh _textMesh;
	public ulong SteamId;

	void Awake() {
		_textMesh = GetComponent<TextMesh>();
	}

	/// <summary>
	/// 初始化设置(由 CreateNameTagObject 调用一次)
	/// </summary>
	public void Initialize(ulong playId) {
		SteamId = playId;
		// 初始显示 Steam 名称
		RefreshName();
	}

	/// <summary>
	/// 更新名称
	/// </summary>
	public void RefreshName() {
		if (_textMesh == null) return;
		// 直接通过 SteamId 获取名称
		string playerName = new Friend(SteamId).Name;
		_textMesh.text =
			$"{playerName}\n" +
			$"ID: {SteamId}\n";
	}

	/// <summary>
	/// 接收远程消息：例如玩家说话、头衔变更等
	/// </summary>
	public void SetDynamicMessage(string message) {
		if (_textMesh == null) return;

		// 示例：显示 "名字\n: 消息内容"
		string playerName = new Friend(SteamId).Name;
		_textMesh.text =
			$"{playerName}\n" +
			$"ID: {SteamId}\n" +
			$"{(message.Length <= 10 ? message : message.Substring(0, 10))}";

	}
}
