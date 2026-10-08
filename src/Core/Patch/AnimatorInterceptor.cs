using DarkMachine.AI;
using HarmonyLib;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using UnityEngine;
using WKMPMod.Components;
using WKMPMod.Core;
using WKMPMod.NetWork;
using WKMPMod.World;

namespace WKMPMod.Patch;

public static class AnimatorInterceptor {
	#region [IL 定位]
	public readonly struct TargetScope {
		public Type TargetType { get; }
		public string[] MethodNames { get; }

		/// <summary>
		/// 是否全类扫描 MethodNames 为空时代表全扫描
		/// </summary>
		public bool IsAllMethods => MethodNames == null || MethodNames.Length == 0;

		// 指定特定方法
		public TargetScope(Type targetType, params string[] methodNames) {
			TargetType = targetType;
			MethodNames = methodNames;
		}

		// 声明全扫描类
		public static TargetScope All(Type targetType) => new(targetType, null);
	}

	public static readonly TargetScope[] Targets = new[] {
		TargetScope.All(typeof(DEN_Ravelin)),// 大螃蟹 含内部函数+协程
		TargetScope.All(typeof(DEN_Crawler)),// 小螃蟹 含内部函数+协程
		// 噬菌体
		new TargetScope(typeof(DEN_Phage),nameof(DEN_Phage.Damage)),
		// 基础生物
		new TargetScope(typeof(DEN_BasicDenizen),
			nameof(DEN_BasicDenizen.AnimationUpdate),nameof(DEN_BasicDenizen.Damage),
			"OnJump","OnLand"),
		// 飞行基础生物
		new TargetScope(typeof(DEN_BasicDenizen_Flier),nameof(DEN_BasicDenizen.Update)),
		// 藤壶僵尸
		new TargetScope(typeof(DEN_Barnacle_Zombie),nameof(DEN_Barnacle_Zombie.Update)),
		// 藤壶
		new TargetScope(typeof(DEN_Barnacle),
			nameof(DEN_Barnacle.Damage),
			nameof(DEN_Barnacle.PrepareTongue),nameof(DEN_Barnacle.ShootTongue)),
		// 漫游者
		new TargetScope(typeof(DEN_Strider),nameof(DEN_Strider.FixedUpdate)),
		// 基础寻路生物
		new TargetScope(typeof(DEN_BasicNavigator),
			nameof(DEN_BasicNavigator.Update),nameof(DEN_BasicNavigator.Pickup),
			nameof(DEN_BasicNavigator.Drop),nameof(DEN_BasicNavigator.SetAnimationTrigger),
			"OnJump","OnLand"),
		// FACE
		new TargetScope(typeof(DEN_Face),nameof(DEN_Face.Update)),

		// TEETH
		new TargetScope(typeof(DEN_Teeth),"Animation",nameof(DEN_Teeth.Leap)),
		new TargetScope(typeof(AIC_Teeth_Burrow),"BurrowAnimation"),
		new TargetScope(typeof(AIC_Teeth_Chase),nameof(AIC_Teeth_Chase.Execute)),
		// 管道血手
		//TargetScope.All(typeof(DEN_VentThing)),

        // 基础攻击模块
		new TargetScope(typeof(DenizenAttackModule_Basic),nameof(DenizenAttackModule_Basic.OnCollisionEnter)),
		new TargetScope(typeof(DenizenAttackModule_Basic.AttackClass),
			nameof(DenizenAttackModule_Basic.AttackClass.Attack),
			nameof(DenizenAttackModule_Basic.AttackClass.ChargeAttack)),
		// 攀爬移动模块
		new TargetScope(typeof(DenizenMovement_Climber),"Movement"),
	};

	// 记录 Animator 和 对应替换函数 的映射
	private static readonly Dictionary<MethodInfo, MethodInfo> MethodMap = new();

	public static void Apply() {
		if (MPMain.HarmonyInstance == null) {
			MPMain.LogError("[AnimatorInterceptor] HarmonyInstance in MPMain is null!");
			return;
		}

		var transpilerMethod = new HarmonyMethod(typeof(AnimatorInterceptor), nameof(Transpile));

		foreach (var scope in Targets) {
			if (scope.TargetType == null) continue;

			// 一次性查出当前 scope 对应的所有目标 MethodInfo
			foreach (var rawMethod in FindMethodsForScope(scope)) {
				PatchSingleMethod(rawMethod, transpilerMethod, scope.TargetType.Name);
			}
		}

		MPMain.LogDebug($"[AnimatorInterceptor] Successfully applied IL Interceptor to {Targets.Length} target scopes.");
	}

	/// <summary>
	/// 统一处理单个 MethodInfo 的解包 (协程转 MoveNext) 与 Patch 动作
	/// </summary>
	private static void PatchSingleMethod(MethodInfo rawMethod, HarmonyMethod transpiler, string typeName) {
		if (rawMethod == null) return;

		// 如果是协程 (普通协程/内部协程),自动定位到其 StateMachine 的 MoveNext()
		var targetMethod = GetActualTargetMethod(rawMethod);

		if (targetMethod == null || targetMethod.IsAbstract || targetMethod.GetMethodBody() == null) return;

		try {
			MPMain.HarmonyInstance.Patch(targetMethod, transpiler: transpiler);

			if (targetMethod != rawMethod) {
				MPMain.LogInfo($"[AnimatorInterceptor] Patched Coroutine: {typeName}.{rawMethod.Name} -> {targetMethod.DeclaringType?.Name}.MoveNext()");
			} else {
				MPMain.LogInfo($"[AnimatorInterceptor] Patched Method: {typeName}.{rawMethod.Name}");
			}
		} catch (Exception ex) {
			MPMain.LogError($"[AnimatorInterceptor] Failed to patch {typeName}.{rawMethod.Name}: {ex.Message}");
		}
	}

	/// <summary>
	/// 查找 MethodInfo
	/// </summary>
	/// <summary>
	/// 一次性获取 Scope 下所有匹配的 MethodInfo（兼顾全扫描与精准匹配）
	/// </summary>
	private static IEnumerable<MethodInfo> FindMethodsForScope(TargetScope scope) {
		const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance 
									| BindingFlags.Static | BindingFlags.DeclaredOnly;
		// 反射全部函数
		var allMethods = scope.TargetType.GetMethods(flags);

		// 全量扫描该类型所有方法
		if (scope.IsAllMethods) {
			foreach (var m in allMethods) yield return m;
			yield break;
		}

		// 按名称列表精准查找
		foreach (var methodName in scope.MethodNames) {
			bool foundAny = false;

			// 格式 "Kill.DieLayer" -> 匹配 "<Kill>g__DieLayer|"
			if (methodName.Contains(".")) {
				var parts = methodName.Split('.');
				string targetPattern = $"<{parts[0]}>g__{parts[1]}|";
				foreach (var m in allMethods) {
					if (m.Name.Contains(targetPattern)) {
						foundAny = true;
						yield return m;
					}
				}
			} else {
				// 格式 "DieLayer" -> 优先检查内部函数特征 "g__DieLayer|"
				string localPattern = $"g__{methodName}|";
				foreach (var m in allMethods) {
					if (m.Name.Contains(localPattern)) {
						foundAny = true;
						yield return m;
					}
				}

				// 如果没找到内部函数,再进行普通方法名匹配
				if (!foundAny) {
					foreach (var m in allMethods) {
						if (m.Name == methodName) {
							foundAny = true;
							yield return m;
						}
					}
				}
			}

			// 未查找到对应函数时打印日志
			if (!foundAny) {
				MPMain.LogWarning($"[AnimatorInterceptor] Method '{methodName}' not found in {scope.TargetType.Name}");
			}
		}
	}

	/// <summary>
	/// 解析普通方法或协程方法, 提取出真正包含 IL 代码的 Target Method
	/// </summary>
	private static MethodInfo GetActualTargetMethod(MethodInfo method) {
		if (method == null) return null;
		// 优先通过 [IteratorStateMachine] 特性寻找协程状态机类
		var stateMachineAttr = method.GetCustomAttribute<IteratorStateMachineAttribute>();
		if (stateMachineAttr != null) {
			var moveNextMethod = stateMachineAttr.StateMachineType.GetMethod(
				"MoveNext", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
			if (moveNextMethod != null) return moveNextMethod;
		}

		// 普通方法直接返回原 MethodInfo
		return method;
	}

	#endregion

	#region[IL 替换器]

	static AnimatorInterceptor() {
		// Trigger 映射
		RegisterMap(
			AccessTools.Method(typeof(Animator), nameof(Animator.SetTrigger), new[] { typeof(string) }),
			AccessTools.Method(typeof(AnimatorInterceptor), nameof(OnSetTriggerStr))
		);
		RegisterMap(
			AccessTools.Method(typeof(Animator), nameof(Animator.SetTrigger), new[] { typeof(int) }),
			AccessTools.Method(typeof(AnimatorInterceptor), nameof(OnSetTriggerHash))
		);

		// Bool 映射
		RegisterMap(
			AccessTools.Method(typeof(Animator), nameof(Animator.SetBool), new[] { typeof(string), typeof(bool) }),
			AccessTools.Method(typeof(AnimatorInterceptor), nameof(OnSetBoolStr))
		);

		// Float 映射
		RegisterMap(
			AccessTools.Method(typeof(Animator), nameof(Animator.SetFloat), new[] { typeof(string), typeof(float) }),
			AccessTools.Method(typeof(AnimatorInterceptor), nameof(OnSetFloatStr))
		);

		// Integer 映射
		RegisterMap(
			AccessTools.Method(typeof(Animator), nameof(Animator.SetInteger), new[] { typeof(string), typeof(int) }),
			AccessTools.Method(typeof(AnimatorInterceptor), nameof(OnSetInteger))
		);

		// LayerWeight 映射
		RegisterMap(
			AccessTools.Method(typeof(Animator), nameof(Animator.SetLayerWeight), new[] { typeof(int), typeof(float) }),
			AccessTools.Method(typeof(AnimatorInterceptor), nameof(OnSetLayerWeight))
		);
	}

	private static void RegisterMap(MethodInfo target, MethodInfo interceptor) {
		if (target != null && interceptor != null) MethodMap[target] = interceptor;
	}

	/// <summary>
	/// 将Animator调用替换为拦截器方法
	/// </summary>
	public static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions) {
		foreach (var inst in instructions) {
			if ((inst.opcode == OpCodes.Call || inst.opcode == OpCodes.Callvirt) 
				&& inst.operand is MethodInfo method && MethodMap.TryGetValue(method, out var interceptor)) {

				inst.opcode = OpCodes.Call;
				inst.operand = interceptor;
			}
			yield return inst;
		}
	}

	#endregion

	private static readonly ConcurrentDictionary<string, int> HashCache = new();

	public static int GetHash(string name) {
		if (string.IsNullOrEmpty(name)) return 0;
		return HashCache.GetOrAdd(name, Animator.StringToHash);
	}

	public static void OnSetTriggerStr(Animator anim, string name) {
		if (!ShouldExecuteAnimCall(anim, out var feature)) return;
		int hash = GetHash(name);
		anim.SetTrigger(hash);
		if (MPSteamworks.IsHost && feature != null) 
			EnemySyncModule.Instance.BroadcastAnimTriggerHash(feature.Entity.networkId,feature.FeatureIndex, hash);
	}

	public static void OnSetTriggerHash(Animator anim, int id) {
		if (!ShouldExecuteAnimCall(anim, out var feature)) return;
		anim.SetTrigger(id);
		if (MPSteamworks.IsHost && feature != null) 
			EnemySyncModule.Instance.BroadcastAnimTriggerHash(feature.Entity.networkId, feature.FeatureIndex, id);
	}

	public static void OnSetBoolStr(Animator anim, string name, bool value) {
		if (!ShouldExecuteAnimCall(anim, out var feature)) return;
		int hash = GetHash(name);
		anim.SetBool(hash, value);
		if (MPSteamworks.IsHost && feature != null) feature.SetBoolDirty(hash, value);
	}

	public static void OnSetFloatStr(Animator anim, string name, float value) {
		if (!ShouldExecuteAnimCall(anim, out var feature)) return;
		int hash = GetHash(name);
		anim.SetFloat(hash, value);
		if (MPSteamworks.IsHost && feature != null) feature.SetFloatDirty(hash, value);
	}

	public static void OnSetInteger(Animator anim, string name, int value) {
		if (!ShouldExecuteAnimCall(anim, out var feature)) return;
		int hash = GetHash(name);
		anim.SetInteger(hash, value);
		if (MPSteamworks.IsHost && feature != null) feature.SetIntDirty(hash, value);
	}

	public static void OnSetLayerWeight(Animator anim, int layer, float weight) {
		if (!ShouldExecuteAnimCall(anim, out var feature)) return;
		anim.SetLayerWeight(layer, weight);
		if (MPSteamworks.IsHost && feature != null) feature.SetLayerWeightDirty(layer, weight);
	}

	private static bool ShouldExecuteAnimCall(Animator anim, out AnimatorSyncFeature feature) {
		feature = null;
		if (anim == null) return false;
		// 如果未处于联机状态或未开启生物同步, 正常播放本地动画
		if (!MPCore.CanSync || !EnemySyncModule.Instance.IsEnabled) return true;
		// 非同步实体正常执行
		if (!AnimatorSyncFeature.TryGetFeature(anim, out feature)) return true;
		// 主机: 正常允许本地 AI 修改动画
		if (MPSteamworks.IsHost) return true;
		// 客机: 只有在远程网络未接管时(IsNetworkControlled == false) 才允许改变 Animator
		return feature.Entity.IsNetworkControlled == false;
	}
}
