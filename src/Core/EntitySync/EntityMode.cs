using System;
using System.Collections.Generic;
using WKMPMod.Core;
using WKMPMod.NetWork;

namespace WKMPMod.EntitySync;

/// <summary>
/// 实体同步实验模式 (本地测试版, 未提交上游).
/// off    = 与上游一致;
/// smooth = 运动层: 快照缓冲插值 + 速度 + 按距离分级发送 (SnapshotTransformFeature);
/// full   = smooth + 可拖动物体 (箱子/木板/凳子等, PropSyncModule); 之后的生物行为 / 动态生成尚未实现.
/// 房主开房时默认写入 full, 可用 entitymode 指令改; 所有人 (含房主) 都按房间数据决定; 只影响之后新加载的实体,
/// 建议开局前设好 (两边同一实体注册的模块不同会解析错).
/// </summary>
public static class EntityMode {
	public enum Mode { Off, Smooth, Full }

	public const string LOBBY_KEY = "wkmp_entity_mode";

	// 自己开房时写入的模式 (房间数据里还没有该键时由房主写入); 加入别人的房间时没有该键仍按 off (对方可能是正式版).
	// 本地测试版默认 full; LobbyOptions 插件的开局界面按钮读写它 (字符串, 便于反射)
	public static Mode HostDefault { get; set; } = Mode.Full;

	public static string HostDefaultName {
		get => HostDefault.ToString().ToLowerInvariant();
		set {
			if (Parse(value) is not Mode mode) return;
			HostDefault = mode;
			// 已在自己开的房间里: 同时写入房间数据 (只影响之后新加载的实体)
			if (MPCore.IsInLobby && MPSteamworks.IsHost && MPSteamworks.Instance != null) {
				MPSteamworks.Instance.SetLobbyData(LOBBY_KEY, HostDefaultName);
				MPMain.LogInfo($"[MP EntityMode] Host set entity mode to {mode}");
			}
		}
	}

	public static Mode Current {
		get {
			if (!MPCore.IsInLobby) return Mode.Off;
			string raw = MPSteamworks.Instance?.LobbyData?.GetValueOrDefault(LOBBY_KEY);
			if (Parse(raw) is Mode mode) return mode;
			if (MPSteamworks.IsHost && MPSteamworks.Instance != null) {
				// 写入后要等 Steam 回调才进本地缓存, 期间读到的仍为空: 只记一次日志
				MPSteamworks.Instance.SetLobbyData(LOBBY_KEY, HostDefaultName);
				if (!_loggedDefault) MPMain.LogInfo($"[MP EntityMode] Lobby had no entity mode, set the default {HostDefault}");
				_loggedDefault = true;
				return HostDefault;
			}
			return Mode.Off;
		}
	}

	private static bool _loggedDefault;

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
		HostDefaultName = mode.ToString().ToLowerInvariant();
		CommandConsole.Log($"entitymode 已设为 {HostDefaultName} (之后新加载的实体生效)");
	}
}
