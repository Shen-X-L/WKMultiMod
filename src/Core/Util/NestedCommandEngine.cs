using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using WKMPMod.Core;
using WKMPMod.Patch;

namespace WKMPMod.Util;

public static class NestedCommandEngine {
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
	static NestedCommandEngine() {
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

	/// <summary>
	/// 通用的子命令嵌套补全外包核心
	/// </summary>
	/// <param name="autocomplete">控制台补全对象</param>
	/// <param name="defaultStartIndex">如果没有输入 :: 时, 子命令默认应该在第几个参数（索引从0开始算）</param>
	public static void ForwardAutocomplete(CommandConsole.CommandAutocomplete autocomplete, int defaultStartIndex) {
		// 熔断安全检查
		if (StatesRef == null || CommandsRef == null) return;

		// --- 动态计算当前正在输入的子命令实际起点 ---
		int subCmdStartIndex = defaultStartIndex;
		for (int i = defaultStartIndex; i <= autocomplete.activeArg; i++) {
			if (autocomplete.ArgumentAt(i) == "::") {
				subCmdStartIndex = i + 1; // 遇到 ::, 子命令起点向后移
			}
		}

		// 获取游戏原生的命令字典
		var statesStack = StatesRef(CommandConsole.instance);
		var currentState = statesStack.Cast<object>().First();
		var commandsDict = CommandsRef(currentState);

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
				var targetAutocompleteAction = AutocompleteRef(targetCmd);

				if (targetAutocompleteAction != null) {
					int originalActiveArg = autocomplete.activeArg;
					List<string> originalArgs = AutocompleteArgsRef(autocomplete);

					// 核心欺骗：左移参数索引并裁剪前缀数组
					SetAutocompleteActiveArgAction(autocomplete, originalActiveArg - subCmdStartIndex);
					AutocompleteArgsRef(autocomplete) = originalArgs.Skip(subCmdStartIndex).ToList();

					// 触发真正子命令的补全提示
					targetAutocompleteAction.Invoke(autocomplete);

					// 状态完美还原
					SetAutocompleteActiveArgAction(autocomplete, originalActiveArg);
					AutocompleteArgsRef(autocomplete) = originalArgs;
				}
			}
		}
	}

	/// <summary>
	/// 通用的子命令嵌套验证外包核心
	/// </summary>
	public static void ForwardValidator(CommandConsole.CommandValidator validator, int defaultStartIndex) {
		if (StatesRef == null || CommandsRef == null || ValidatorArgsRef == null) return;

		int originalActiveArg = validator.activeArg;
		List<string> originalArgs = ValidatorArgsRef(validator);

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

			var statesStack = StatesRef(CommandConsole.instance);
			var currentState = statesStack.Cast<object>().First();
			var commandsDict = CommandsRef(currentState);

			if (commandsDict.Contains(targetCmdName)) {
				var targetCmd = commandsDict[targetCmdName];
				var targetValidatorAction = ValidatorRef(targetCmd);

				if (targetValidatorAction != null) {
					// 核心欺骗
					SetValidatorActiveArgAction(validator, originalActiveArg - subCmdStartIndex);
					ValidatorArgsRef(validator) = originalArgs.Skip(subCmdStartIndex).ToList();

					// 触发真正子命令的验证逻辑
					targetValidatorAction.Invoke(validator);

					// 状态还原
					SetValidatorActiveArgAction(validator, originalActiveArg);
					ValidatorArgsRef(validator) = originalArgs;
				}
			}
		}
	}
}