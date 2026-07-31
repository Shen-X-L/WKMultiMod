using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using WKMPMod.Core;
using WKMPMod.NetWork;
using WKMPMod.Util;

namespace WKMPMod.Patch;

[HarmonyPatch(typeof(CommandConsole))]
public class Patch_CommandConsole {
	#region[常规补丁]

	// 启用时注册命令
	[HarmonyPatch("Awake")]
	[HarmonyPostfix]
	public static void Awake_RegisterCommands() {
		MPCore.Instance.RegisterCommands();
		return;
	}

	// 在allowCheats为false时禁止作弊
	[HarmonyPatch("EnableCheatsCommand")]
	[HarmonyPrefix]
	public static bool EnableCheatsCommand_BlockIfNotAllowed() {
		// 在大厅且不允许作弊
		if (MPCore.IsInLobby && !MPCore.IsAllowCheats && !MPSteamworks.Instance.IsHost) {
			// 当前大厅不允许作弊
			CommandConsole.LogError(Localization.Get("CommandConsole.CheatsNotAllowed"));
			return false;
		} 
		else return true;
	}

	#endregion

	#region [RCON (远程控制台)标记追踪]

	// 时空穿越特权标记
	private const string RCON_MARKER = "/*mp_rcon*/";

	// 嵌套特权计数器 (防止多段 delay 交叉执行时提前关闭权限)
	private static int _rconCount = 0;
	public static bool IsRconExecuting => _rconCount > 0;

	public static bool _isCheatsEnabled = false;


	public static void PushPrivilege() {
		_rconCount++;
		if (_rconCount == 1) {
			CommandConsole.cheatsEnabled = true;
			CommandConsole.hasCheated = true;

			// 激活原版防作弊追踪 UI 与日志
			if (CL_UIManager.instance?.cheatTracker != null) {
				CL_UIManager.instance.cheatTracker.SetActive(true);
			}
		}
	}


	public static void PopPrivilege() {
		_rconCount--;
		if (_rconCount < 0) _rconCount = 0;
		if (_rconCount == 0) {
			// 特权流彻底执行完毕, 安全收回权限
			CommandConsole.cheatsEnabled = _isCheatsEnabled;
		}
	}

	/// <summary>
	/// 强制执行一条命令, 完美继承特权至所有被 delay 延迟的子指令
	/// </summary>
	public static void ExecuteCommandForcefully(string command) {
		_isCheatsEnabled = CommandConsole.cheatsEnabled;
		var console = CommandConsole.instance;
		if (console == null || string.IsNullOrEmpty(command)) return;

		try {
			// 在头部贴上特权标记, 送入原版指令解析器
			string flaggedCommand = RCON_MARKER + command;
			console.ExecuteCommand(flaggedCommand, false);
		} catch (Exception ex) {
			MPMain.LogError(Localization.Get("Patch.ForceExecuteCommandCrash", ex.Message));
			// 极端熔断保护
			CommandConsole.cheatsEnabled = false;
			_rconCount = 0;
		}
	}

	#endregion

	#region [通过标记强制执行]

	// 拦截一切指令执行入口, 识别并剥离 RCON 特权标记
	[HarmonyPatch(typeof(CommandConsole), nameof(CommandConsole.ExecuteCommand),
		new Type[] { typeof(string), typeof(bool), typeof(bool) })]
	[HarmonyPrefix]
	public static void Pre_ExecuteCommand(ref string input, out bool __state) {
		__state = false;
		if (input != null && input.StartsWith(RCON_MARKER)) {
			// 剥离标记, 让原版 Lexer 正常解析干净的指令
			input = input.Substring(RCON_MARKER.Length);
			// 激活当前执行上下文的特权
			PushPrivilege();
			__state = true; // 传递状态给 Postfix
		}
	}

	[HarmonyPatch(typeof(CommandConsole), nameof(CommandConsole.ExecuteCommand),
		new Type[] { typeof(string), typeof(bool), typeof(bool) })]
	[HarmonyPostfix]
	public static void Post_ExecuteCommand(bool __state) {
		if (__state) {
			// 退出当前同步上下文时扣减计数
			PopPrivilege();
		}
	}

	// 拦截指令管道截断 (应对形式: delay 1s ; noclip)
	// 当原版从剩余 Token 中偷走文本准备送进协程时, 如果处于特权期间, 把标记重新补在头部
	[HarmonyPatch(typeof(CommandConsole), nameof(CommandConsole.CancelCommandExecution))]
	[HarmonyPostfix]
	public static void Post_CancelCommandExecution(ref string __result) {
		if (IsRconExecuting && !string.IsNullOrEmpty(__result)) {
			if (!__result.StartsWith(RCON_MARKER)) {
				__result = RCON_MARKER + __result;
			}
		}
	}

	// 拦截明确参数延迟 (应对形式: delay 1s noclip)
	// 如果指令用的是空格传参而不是分号分隔, 直接就地污染 args[1] 的内容, 使其自带特权
	[HarmonyPatch(typeof(CommandConsole), "DelayCommand")]
	[HarmonyPrefix]
	public static void Pre_DelayCommand(string[] args) {
		if (IsRconExecuting && args != null && args.Length > 1) {
			if (!args[1].StartsWith(RCON_MARKER)) {
				args[1] = RCON_MARKER + args[1];
			}
		}
	}

	[HarmonyPatch(typeof(CommandConsole), "MayUseCommand")]
	[HarmonyPrefix]
	public static bool Pre_MayUseCommand(ref object __result) {
		if (IsRconExecuting) {
			// 使用 Enum.ToObject 强制转换回私有枚举类型
			// CommandUsageKind.MayUse 的底层值通常是 0
			__result = Enum.ToObject(typeof(CommandConsole).GetNestedType("CommandUsageKind", BindingFlags.NonPublic), 0);
			return false;
		}
		return true;
	}

	#endregion
}