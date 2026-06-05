using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using WKMPMod.Core;
using WKMPMod.Util;

namespace WKMPMod.Patch;

[HarmonyPatch(typeof(CommandConsole))]
public class Patch_CommandConsole {
	#region [Harmony 高性能反射缓存 (Zero-Alloc Reflection Cache)]

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

	//// 补丁类: 修复字符串逻辑
	//[HarmonyPatch("CommandValueAsString")]
	//[HarmonyPrefix]
	//public static bool CommandValueAsString_FixStringDisplay(Func<object> functor, ref string __result) {
	//	object obj = functor();

	//	if (obj is string str) {
	//		__result = $"Value: {str}";
	//		return false; // 跳过原方法的执行
	//	}

	//	return true; // 其他类型 继续执行原方法
	//}

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
		if (MPCore.IsInLobby && !MPCore.IsAllowCheats) {
			// 当前大厅不允许作弊
			CommandConsole.LogError(Localization.Get("CommandConsole.CheatsNotAllowed"));
			return false;
		} 
		else return true;
	}

	/// <summary>
	/// 强制执行一条命令 无视大厅作弊限制 同时标记本局已作弊.
	/// </summary>
	/// <param name="command">要执行的完整命令字符串</param>
	public static void ExecuteCommandForcefully(string command) {
		var console = CommandConsole.instance;
		if (console == null || string.IsNullOrEmpty(command)) return;

		// 备份执行前的真实作弊状态
		bool originalCheatsState = CommandConsole.cheatsEnabled;

		try {
			// 强行静默开启作弊权限，从而完美绕过 MayUseCommand 里的 CheatsNotEnabled 检查
			CommandConsole.cheatsEnabled = true;

			// 调用原版引擎执行指令
			console.ExecuteCommand(command, false);
		} catch (Exception ex) {
			MPMain.LogError(Localization.Get("Patch.ForceExecuteCommandCrash", ex.Message));
		} finally {
			// 无论指令执行成功还是抛出异常，都必须将 cheatsEnabled 还原为玩家原有的状态
			CommandConsole.cheatsEnabled = originalCheatsState;

			// 遵照需求: 强制将全局 hasCheated 变为 true 
			if (!CommandConsole.hasCheated) {
				CommandConsole.hasCheated = true;

				// 顺便帮原版激活防作弊追踪 UI 与日志
				if (CL_UIManager.instance?.cheatTracker != null) {
					CL_UIManager.instance.cheatTracker.SetActive(true);
				}
				CommandConsole.Log("WARNING: Cheat activated via Remote Management. Saving of stats has been disabled.");
			}
		}
	}
}