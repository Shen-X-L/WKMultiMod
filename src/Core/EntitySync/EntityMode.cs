using System;
using System.Collections.Generic;
using WKMPMod.Core;
using WKMPMod.NetWork;

namespace WKMPMod.EntitySync;

/// <summary>
/// 实体同步实验模式 (本地测试版, 未提交上游).
/// off    = 与上游一致;
/// smooth = 运动层: 快照缓冲插值 + 速度 + 按距离分级发送 (SnapshotTransformFeature);
/// full   = smooth + 之后的可拖动物体 / 生物行为 / 动态生成 (尚未实现, 目前等同 smooth).
/// 房主用 entitymode 指令写入房间数据, 所有人 (含房主) 都按房间数据决定; 只影响之后新加载的实体,
/// 建议开局前设好 (两边同一实体注册的模块不同会解析错).
/// </summary>
public static class EntityMode {
	public enum Mode { Off, Smooth, Full }

	public const string LOBBY_KEY = "wkmp_entity_mode";

	public static Mode Current {
		get {
			if (!MPCore.IsInLobby) return Mode.Off;
			string raw = MPSteamworks.Instance?.LobbyData?.GetValueOrDefault(LOBBY_KEY);
			return Parse(raw) ?? Mode.Off;
		}
	}

	public static bool AtLeastSmooth => Current >= Mode.Smooth;

	private static Mode? Parse(string raw) => raw?.Trim().ToLowerInvariant() switch {
		"off" => Mode.Off,
		"smooth" => Mode.Smooth,
		"full" => Mode.Full,
		_ => null,
	};

	public static void RegisterCommand() {
		CommandConsole.BuildCommand("entitymode", OnCommand).NotCheat()
			.Description("[off|smooth|full] 实体同步实验模式 (房主设置; 开局前设好)")
			.AutocompleteSingleFromArray(new[] { "off", "smooth", "full" });
	}

	private static void OnCommand(string[] args) {
		if (args.Length == 0) {
			CommandConsole.Log($"entitymode = {Current.ToString().ToLowerInvariant()}. 用法: entitymode off|smooth|full");
			return;
		}
		if (Parse(args[0]) is not Mode mode) {
			CommandConsole.LogError("用法: entitymode off|smooth|full");
			return;
		}
		if (!MPCore.IsInLobby || !MPSteamworks.IsHost) {
			CommandConsole.LogError("只有房主可以设置 entitymode");
			return;
		}
		MPSteamworks.Instance.SetLobbyData(LOBBY_KEY, mode.ToString().ToLowerInvariant());
		CommandConsole.Log($"entitymode 已设为 {mode.ToString().ToLowerInvariant()} (之后新加载的实体生效)");
		MPMain.LogInfo($"[MP EntityMode] Host set entity mode to {mode}");
	}
}
