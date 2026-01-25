using System;
using System.Collections.Generic;
using UnityEngine;
using WKMPMod.Component;
using WKMPMod.Core;
using WKMPMod.RemoteManager;
using WKMPMod.Shared.MK_Component;
using WKMPMod.Util;
using Object = UnityEngine.Object;
using Quaternion = UnityEngine.Quaternion;
using Vector3 = UnityEngine.Vector3;

namespace WKMPMod.Test;

public class Test : MonoBehaviour {
	public static void Main(string[] args) {

		switch (args[0]) {
			case "0":
				GetGraphicsAPI();
				break;
			case "1":
				GetMPStatus();
				break;
			case "2":
				GetMassData();
				break;
			case "3":
				GetSystemLanguage();
				break;
			case "4":
				CreateRemotePlayer();
				break;
			default:
				break;
		}
	}

	public static void GetGraphicsAPI() {
		// 方法1：直接获取当前API
		Debug.Log($"当前图形API: {SystemInfo.graphicsDeviceType}");

		// 方法2：获取详细版本信息
		Debug.Log($"图形API版本: {SystemInfo.graphicsDeviceVersion}");

		// 方法3：获取Shader Model级别
		int smLevel = SystemInfo.graphicsShaderLevel;
		Debug.Log($"Shader Model: {smLevel / 10}.{smLevel % 10}");

		// 方法4：检查具体功能支持
		Debug.Log($"支持计算着色器: {SystemInfo.supportsComputeShaders}");
		Debug.Log($"支持几何着色器: {SystemInfo.supportsGeometryShaders}");
		Debug.Log($"支持曲面细分: {SystemInfo.supportsTessellationShaders}");
		Debug.Log($"支持GPU实例化: {SystemInfo.supportsInstancing}");
	}

	public static void GetMPStatus() {
		Debug.Log($"{((int)(MPCore.MultiPlayerStatus)).ToString()}");
	}

	public static void GetMassData() {
		var data = DEN_DeathFloor.instance.GetSaveData();
		Debug.Log($"高度:{data.relativeHeight}, 是否活动:{data.active}, 速度:{data.speed}, 速度乘数:{data.speedMult}");
	}

	public static void GetSystemLanguage() {
		Debug.Log($"系统语言:{Localization.GetGameLanguage()}");
	}

	public static void CreateRemotePlayer() {
		MPCore.Instance.RPManager.PlayerCreate(1);
	}
}