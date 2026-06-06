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
	#region [Harmony反射缓存]

	//// --- 核心全局字段 ---
	//public static readonly AccessTools.FieldRef<CommandConsole, IEnumerable> StatesRef =
	//	AccessTools.FieldRefAccess<CommandConsole, IEnumerable>("states");
	//public static readonly AccessTools.FieldRef<object, IDictionary> CommandsRef =
	//	AccessTools.FieldRefAccess<object, IDictionary>(
	//		AccessTools.Field(AccessTools.Inner(typeof(CommandConsole), "CommandLineState"), "commands"));

	//// --- 补全器 (Autocomplete) 相关缓存 ---
	//public static readonly AccessTools.FieldRef<object, Action<CommandConsole.CommandAutocomplete>> AutocompleteRef =
	//	AccessTools.FieldRefAccess<object, Action<CommandConsole.CommandAutocomplete>>(
	//		AccessTools.Field(AccessTools.Inner(typeof(CommandConsole), "Command"), "autocomplete"));
	//public static readonly AccessTools.FieldRef<CommandConsole.CommandAutocomplete, List<string>> AutocompleteArgsRef =
	//	AccessTools.FieldRefAccess<CommandConsole.CommandAutocomplete, List<string>>("args");
	//public static readonly Action<CommandConsole.CommandAutocomplete, int> SetAutocompleteActiveArgAction =
	//	AccessTools.MethodDelegate<Action<CommandConsole.CommandAutocomplete, int>>(
	//		AccessTools.PropertySetter(typeof(CommandConsole.CommandAutocomplete), "activeArg"));

	//// --- 验证器 (Validator) 相关缓存 ---
	//public static readonly AccessTools.FieldRef<object, Action<CommandConsole.CommandValidator>> ValidatorRef =
	//	AccessTools.FieldRefAccess<object, Action<CommandConsole.CommandValidator>>(
	//		AccessTools.Field(AccessTools.Inner(typeof(CommandConsole), "Command"), "validator"));

	//public static readonly AccessTools.FieldRef<CommandConsole.CommandValidator, List<string>> ValidatorArgsRef =
	//	AccessTools.FieldRefAccess<CommandConsole.CommandValidator, List<string>>("args");

	//public static readonly Action<CommandConsole.CommandValidator, int> SetValidatorActiveArgAction =
	//	AccessTools.MethodDelegate<Action<CommandConsole.CommandValidator, int>>(
	//		AccessTools.PropertySetter(typeof(CommandConsole.CommandValidator), "activeArg"));

	// --- 核心全局字段 ---
	public static readonly AccessTools.FieldRef<CommandConsole, IEnumerable> StatesRef;
	public static readonly AccessTools.FieldRef<object, IDictionary> CommandsRef;

	// --- 补全器 (Autocomplete) 相关缓存 ---
	public static readonly AccessTools.FieldRef<object, Action<CommandConsole.CommandAutocomplete>> AutocompleteRef;
	public static readonly AccessTools.FieldRef<CommandConsole.CommandAutocomplete, List<string>> AutocompleteArgsRef;
	public static readonly Action<CommandConsole.CommandAutocomplete, int> SetAutocompleteActiveArgAction;

	// --- 验证器 (Validator) 相关缓存 ---
	public static readonly AccessTools.FieldRef<object, Action<CommandConsole.CommandValidator>> ValidatorRef;
	public static readonly AccessTools.FieldRef<CommandConsole.CommandValidator, List<string>> ValidatorArgsRef;
	public static readonly Action<CommandConsole.CommandValidator, int> SetValidatorActiveArgAction;

	// 静态构造函数：游戏启动时一次性预编译所有内存指针
	static Patch_CommandConsole() {
		try {
			var consoleType = typeof(CommandConsole);

			// 1. 缓存核心状态机与命令字典
			StatesRef = AccessTools.FieldRefAccess<CommandConsole, IEnumerable>("states");
			var stateType = AccessTools.Inner(consoleType, "CommandLineState");
			var commandsField = AccessTools.Field(stateType, "commands");
			CommandsRef = AccessTools.FieldRefAccess<object, IDictionary>(commandsField);

			// 2. 缓存内部嵌套类 Command 里的补全与验证 Action 字段
			var commandType = AccessTools.Inner(consoleType, "Command");
			var autocompleteField = AccessTools.Field(commandType, "autocomplete");
			AutocompleteRef = AccessTools.FieldRefAccess<object, Action<CommandConsole.CommandAutocomplete>>(autocompleteField);

			var validatorField = AccessTools.Field(commandType, "validator");
			ValidatorRef = AccessTools.FieldRefAccess<object, Action<CommandConsole.CommandValidator>>(validatorField);

			// 3. 预编译补全器 (CommandAutocomplete) 读写代理
			var autocompleteType = typeof(CommandConsole.CommandAutocomplete);
			AutocompleteArgsRef = AccessTools.FieldRefAccess<CommandConsole.CommandAutocomplete, List<string>>("args");
			var autocompleteActiveArgSetter = AccessTools.PropertySetter(autocompleteType, "activeArg");
			if (autocompleteActiveArgSetter != null) {
				SetAutocompleteActiveArgAction = AccessTools.MethodDelegate<Action<CommandConsole.CommandAutocomplete, int>>(autocompleteActiveArgSetter);
			}

			// 4. 预编译验证器 (CommandValidator) 读写代理
			var validatorType = typeof(CommandConsole.CommandValidator);
			ValidatorArgsRef = AccessTools.FieldRefAccess<CommandConsole.CommandValidator, List<string>>("args");
			var validatorActiveArgSetter = AccessTools.PropertySetter(validatorType, "activeArg");
			if (validatorActiveArgSetter != null) {
				SetValidatorActiveArgAction = AccessTools.MethodDelegate<Action<CommandConsole.CommandValidator, int>>(validatorActiveArgSetter);
			}
		} catch (Exception ex) {
			MPMain.LogError(Localization.Get("Patch.HarmonyReflectionInitFailed", ex.Message));
		}
	}
	#endregion
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
			CommandConsole.cheatsEnabled = false;
		}
	}

	/// <summary>
	/// 强制执行一条命令, 完美继承特权至所有被 delay 延迟的子指令
	/// </summary>
	public static void ExecuteCommandForcefully(string command) {
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

/// <summary>
/// 嵌套命令执行引擎
/// </summary>
public static class NestedCommandEngine {

	/// <summary>
	/// 通用的子命令嵌套补全外包核心
	/// </summary>
	/// <param name="autocomplete">控制台补全对象</param>
	/// <param name="defaultStartIndex">如果没有输入 :: 时, 子命令默认应该在第几个参数（索引从0开始算）</param>
	public static void ForwardAutocomplete(CommandConsole.CommandAutocomplete autocomplete, int defaultStartIndex) {
		// 熔断安全检查
		if (Patch_CommandConsole.StatesRef == null || Patch_CommandConsole.CommandsRef == null) return;

		// --- 动态计算当前正在输入的子命令实际起点 ---
		int subCmdStartIndex = defaultStartIndex;
		for (int i = defaultStartIndex; i <= autocomplete.activeArg; i++) {
			if (autocomplete.ArgumentAt(i) == "::") {
				subCmdStartIndex = i + 1; // 遇到 ::, 子命令起点向后移
			}
		}

		// 获取游戏原生的命令字典
		var statesStack = Patch_CommandConsole.StatesRef(CommandConsole.instance);
		var currentState = statesStack.Cast<object>().First();
		var commandsDict = Patch_CommandConsole.CommandsRef(currentState);

		// 情况 A：光标刚好落在子命令的名字上, 提示所有可用的命令
		if (autocomplete.activeArg == subCmdStartIndex) {
			List<string> cmdNames = new List<string>();
			foreach (var key in commandsDict.Keys) {
				cmdNames.Add((string)key);
			}
			cmdNames.Add("::"); // 允许玩家继续叠加嵌套连击
			autocomplete.FromArray(cmdNames);
			return;
		}

		// 情况 B：正在输入子命令的后续参数, 执行外包代理
		if (autocomplete.activeArg > subCmdStartIndex) {
			string targetCmdName = autocomplete.ArgumentAt(subCmdStartIndex).ToLower();
			if (commandsDict.Contains(targetCmdName)) {
				var targetCmd = commandsDict[targetCmdName];
				var targetAutocompleteAction = Patch_CommandConsole.AutocompleteRef(targetCmd);

				if (targetAutocompleteAction != null) {
					int originalActiveArg = autocomplete.activeArg;
					List<string> originalArgs = Patch_CommandConsole.AutocompleteArgsRef(autocomplete);

					// 核心欺骗：左移参数索引并裁剪前缀数组
					Patch_CommandConsole.SetAutocompleteActiveArgAction(autocomplete, originalActiveArg - subCmdStartIndex);
					Patch_CommandConsole.AutocompleteArgsRef(autocomplete) = originalArgs.Skip(subCmdStartIndex).ToList();

					// 触发真正子命令的补全提示
					targetAutocompleteAction.Invoke(autocomplete);

					// 状态完美还原
					Patch_CommandConsole.SetAutocompleteActiveArgAction(autocomplete, originalActiveArg);
					Patch_CommandConsole.AutocompleteArgsRef(autocomplete) = originalArgs;
				}
			}
		}
	}

	/// <summary>
	/// 通用的子命令嵌套验证外包核心
	/// </summary>
	public static void ForwardValidator(CommandConsole.CommandValidator validator, int defaultStartIndex) {
		if (Patch_CommandConsole.StatesRef == null || Patch_CommandConsole.CommandsRef == null || Patch_CommandConsole.ValidatorArgsRef == null) return;

		int originalActiveArg = validator.activeArg;
		List<string> originalArgs = Patch_CommandConsole.ValidatorArgsRef(validator);

		// 如果当前输入的总参数量还没达到默认子命令的起点, 直接无需校验子命令
		if (originalActiveArg < defaultStartIndex) return;

		// --- 动态计算子命令实际起点 ---
		int subCmdStartIndex = defaultStartIndex;
		int searchLimit = Math.Min(originalActiveArg, originalArgs.Count - 1);
		for (int i = defaultStartIndex; i <= searchLimit; i++) {
			if (originalArgs[i] == "::") {
				subCmdStartIndex = i + 1;
			}
		}

		// 如果光标在 :: 上或在子命令名字上, 无需执行子命令本身的验证器
		if (originalActiveArg <= subCmdStartIndex) return;

		if (subCmdStartIndex < originalArgs.Count) {
			string targetCmdName = originalArgs[subCmdStartIndex].ToLower();

			var statesStack = Patch_CommandConsole.StatesRef(CommandConsole.instance);
			var currentState = statesStack.Cast<object>().First();
			var commandsDict = Patch_CommandConsole.CommandsRef(currentState);

			if (commandsDict.Contains(targetCmdName)) {
				var targetCmd = commandsDict[targetCmdName];
				var targetValidatorAction = Patch_CommandConsole.ValidatorRef(targetCmd);

				if (targetValidatorAction != null) {
					// 核心欺骗
					Patch_CommandConsole.SetValidatorActiveArgAction(validator, originalActiveArg - subCmdStartIndex);
					Patch_CommandConsole.ValidatorArgsRef(validator) = originalArgs.Skip(subCmdStartIndex).ToList();

					// 触发真正子命令的验证逻辑
					targetValidatorAction.Invoke(validator);

					// 状态还原
					Patch_CommandConsole.SetValidatorActiveArgAction(validator, originalActiveArg);
					Patch_CommandConsole.ValidatorArgsRef(validator) = originalArgs;
				}
			}
		}
	}
}