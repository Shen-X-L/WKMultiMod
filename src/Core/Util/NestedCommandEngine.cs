using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using WKMPMod.Patch;

namespace WKMPMod.Util;

public static class NestedCommandEngine {

	/// <summary>
	/// 通用的子命令嵌套补全外包核心
	/// </summary>
	/// <param name="autocomplete">控制台补全对象</param>
	/// <param name="defaultStartIndex">如果没有输入 :: 时，子命令默认应该在第几个参数（索引从0开始算）</param>
	public static void ForwardAutocomplete(CommandConsole.CommandAutocomplete autocomplete, int defaultStartIndex) {
		// 熔断安全检查
		if (Patch_CommandConsole.StatesRef == null || Patch_CommandConsole.CommandsRef == null) return;

		// --- 动态计算当前正在输入的子命令实际起点 ---
		int subCmdStartIndex = defaultStartIndex;
		for (int i = defaultStartIndex; i <= autocomplete.activeArg; i++) {
			if (autocomplete.ArgumentAt(i) == "::") {
				subCmdStartIndex = i + 1; // 遇到 ::，子命令起点向后移
			}
		}

		// 获取游戏原生的命令字典
		var statesStack = Patch_CommandConsole.StatesRef(CommandConsole.instance);
		var currentState = statesStack.Cast<object>().First();
		var commandsDict = Patch_CommandConsole.CommandsRef(currentState);

		// 情况 A：光标刚好落在子命令的名字上，提示所有可用的命令
		if (autocomplete.activeArg == subCmdStartIndex) {
			List<string> cmdNames = new List<string>();
			foreach (var key in commandsDict.Keys) {
				cmdNames.Add((string)key);
			}
			cmdNames.Add("::"); // 允许玩家继续叠加嵌套连击
			autocomplete.FromArray(cmdNames);
			return;
		}

		// 情况 B：正在输入子命令的后续参数，执行外包代理
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

		// 如果当前输入的总参数量还没达到默认子命令的起点，直接无需校验子命令
		if (originalActiveArg < defaultStartIndex) return;

		// --- 动态计算子命令实际起点 ---
		int subCmdStartIndex = defaultStartIndex;
		int searchLimit = Math.Min(originalActiveArg, originalArgs.Count - 1);
		for (int i = defaultStartIndex; i <= searchLimit; i++) {
			if (originalArgs[i] == "::") {
				subCmdStartIndex = i + 1;
			}
		}

		// 如果光标在 :: 上或在子命令名字上，无需执行子命令本身的验证器
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