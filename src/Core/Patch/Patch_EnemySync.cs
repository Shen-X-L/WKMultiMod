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
using WKMPMod.Data;
using WKMPMod.NetWork;
using WKMPMod.World;

namespace WKMPMod.Patch;

[HarmonyPatch(typeof(GameEntity))]
public class Patch_GameEntity {
	#region[伤害同步]

	// DEN_Mother 没调用父类Damage
	[HarmonyPatch(nameof(GameEntity.Damage))]
	[HarmonyPrefix]
	public static void Patch_Damage(GameEntity __instance, Damageable.DamageInfo info) {
		EnemySyncModule.Instance.SendEnemyDamage(__instance, info);
	}

	#endregion

	#region[实体生命周期拦截]

	// DEN_Teeth DEN_EngravedDoor 没调用父类OnDisable OnEnable
	// DEN_Hunter 没调用父类OnDisable

	/// <summary>
	/// 监听 GameEntity 启用/生成,提供增量扫描数据源
	/// </summary>
	[HarmonyPatch(nameof(GameEntity.OnEnable))]
	[HarmonyPostfix]
	public static void Patch_OnEnable(GameEntity __instance) {
		EnemySyncModule.Instance?.OnEntityEnabled(__instance);
	}

	/// <summary>
	/// 监听 GameEntity 禁用/销毁,立即通知注销记录
	/// </summary>
	[HarmonyPatch(nameof(GameEntity.OnDisable))]
	[HarmonyPrefix]
	public static void Patch_OnDisable(GameEntity __instance) {
		EnemySyncModule.Instance?.OnEntityDisabled(__instance);
	}

	#endregion

	#region[生物死亡同步]

	/// <summary>
	/// 监听 GameEntity 死亡,立即通知注销记录
	/// </summary>
	[HarmonyPatch(nameof(GameEntity.Kill), new[] { typeof(string), typeof(Damageable.DamageInfo) })]
	[HarmonyPrefix]
	public static void Patch_Prefix_Kill(GameEntity __instance, out bool __state) {
		__state = __instance.dead;
	}
	[HarmonyPatch(nameof(GameEntity.Kill), new[] { typeof(string), typeof(Damageable.DamageInfo) })]
	[HarmonyPostfix]
	public static void Patch_Postfix_Kill(GameEntity __instance, string type, bool __state) {
		// 执行前未死亡,执行后死亡 视为第一次死亡
		if (!__state && __instance.dead) EnemySyncModule.Instance.OnEntityKill(__instance, type);
	}

	#endregion
}

/// <summary>
/// 管道血手
/// </summary>
[HarmonyPatch(typeof(DEN_VentThing))]
public class Patch_DEN_VentThing {
	[HarmonyPatch(nameof(DEN_VentThing.Damage))]
	[HarmonyPrefix]
	public static void Patch_Damage(DEN_VentThing __instance, Damageable.DamageInfo info) {
		EnemySyncModule.Instance.SendEnemyDamage(__instance, info);
	}
}

/// <summary>
/// 无人机
/// </summary>
[HarmonyPatch(typeof(DEN_Drone))]
public static class Patch_DEN_Drone {

	/// <summary>
	/// 将 DEN_Drone.OnCollisionEnter 中旧版的
	/// CreateDamageInfo(float, string)
	/// 替换为 Mod 提供的带 Drone Source 的版本
	/// </summary>
	[HarmonyTranspiler]
	[HarmonyPatch(typeof(DEN_Drone), "OnCollisionEnter")]
	public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
		var codes = new List<CodeInstruction>(instructions);

		// 定义目标方法与 Helper 方法
		var originalMethod = AccessTools.Method(
			typeof(Damageable.DamageInfo),
			nameof(Damageable.DamageInfo.CreateDamageInfo),
			new[] { typeof(float), typeof(string) }
		);
		var helperMethod = AccessTools.Method(typeof(Patch_DEN_Drone), nameof(Patch_DEN_Drone.CreateDamageInfo));

		// 方法存在性校验
		if (originalMethod == null) {
			MPMain.LogWarning("[Transpiler] DamageInfo.CreateDamageInfo(float,string) 不存在");
			return codes;
		}

		// 寻找调用 CreateDamageInfo 的 IL 指令索引
		int targetIndex = codes.FindIndex(c => c.Calls(originalMethod));
		if (targetIndex == -1) {
			MPMain.LogWarning("[Transpiler] DEN_Drone.OnCollisionEnter -> DamageInfo.CreateDamageInfo(float,string) 不存在");
			return codes;
		}

		// 直接把原 Call 指令修改为 Ldarg_0
		codes[targetIndex].opcode = OpCodes.Ldarg_0;
		codes[targetIndex].operand = null;

		// 在 targetIndex + 1 处追加调用 Helper 方法
		codes.Insert(targetIndex + 1, new CodeInstruction(OpCodes.Call, helperMethod));
		return codes;
	}

	/// <summary>
	/// 实际由 IL 调用的辅助函数。
	/// </summary>
	public static Damageable.DamageInfo CreateDamageInfo(float amount, string objectType, DEN_Drone source) {
		return Damageable.DamageInfo.CreateDamageInfo(amount, objectType, new List<string> { "drone" }, source);
	}
}

/// <summary>
/// 噬菌体
/// </summary>
[HarmonyPatch(typeof(DEN_Phage))]
public static class Patch_DEN_Phage {

	[HarmonyPatch("OnAttackHit")]
	[HarmonyPrefix]
	public static void Patch_OnAttackHit(DEN_Phage __instance, GameEntity hitEntity) {
		if (!hitEntity.GetTagger().HasTag(MPKeys.REMOTE_PLAYER_TAG)) return;
		// 进行特殊攻击模块调用
		if (!__instance.TryGetComponent<NetworkedGameEntity>(out var identity)) return;

		return;
	}

	[HarmonyPatch(nameof(DEN_Phage.Damage))]
	[HarmonyPrefix]
	public static void Patch_Damage(DEN_Phage __instance, Damageable.DamageInfo info) {
		EnemySyncModule.Instance.SendEnemyDamage(__instance, info);
	}
}

#region[动画同步]

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
				MPMain.LogDebug($"[AnimatorInterceptor] Patched Coroutine: {typeName}.{rawMethod.Name} -> {targetMethod.DeclaringType?.Name}.MoveNext()");
			} else {
				MPMain.LogDebug($"[AnimatorInterceptor] Patched Method: {typeName}.{rawMethod.Name}");
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
		if (feature != null) feature.SetTrigger(hash);
		else anim.SetTrigger(hash);
	}

	public static void OnSetTriggerHash(Animator anim, int id) {
		if (!ShouldExecuteAnimCall(anim, out var feature)) return;
		if (feature != null) feature.SetTrigger(id);
		else anim.SetTrigger(id);
	}

	public static void OnSetBoolStr(Animator anim, string name, bool value) {
		if (!ShouldExecuteAnimCall(anim, out var feature)) return;
		int hash = GetHash(name);
		anim.SetBool(hash, value);
		if (feature != null && feature.Entity.IsOwnedByMe) feature.SetBoolDirty(hash, value);
	}

	public static void OnSetFloatStr(Animator anim, string name, float value) {
		if (!ShouldExecuteAnimCall(anim, out var feature)) return;
		int hash = GetHash(name);
		anim.SetFloat(hash, value);
		if (feature != null && feature.Entity.IsOwnedByMe) feature.SetFloatDirty(hash, value);
	}

	public static void OnSetInteger(Animator anim, string name, int value) {
		if (!ShouldExecuteAnimCall(anim, out var feature)) return;
		int hash = GetHash(name);
		anim.SetInteger(hash, value);
		if (feature != null && feature.Entity.IsOwnedByMe) feature.SetIntDirty(hash, value);
	}

	public static void OnSetLayerWeight(Animator anim, int layer, float weight) {
		if (!ShouldExecuteAnimCall(anim, out var feature)) return;
		anim.SetLayerWeight(layer, weight);
		if (feature != null && feature.Entity.IsOwnedByMe) feature.SetLayerWeightDirty(layer, weight);
	}

	private static bool ShouldExecuteAnimCall(Animator anim, out AnimatorSyncFeature feature) {
		feature = null;
		if (anim == null) return false;
		// 如果未处于联机状态或未开启生物同步, 正常播放本地动画
		if (!MPCore.CanSync || !EnemySyncModule.Instance.IsEnabled) return true;
		// 非同步实体正常执行
		if (!AnimatorSyncFeature.TryGetFeature(anim, out feature)) return true;
		// 正常允许本地 AI 修改动画
		if (feature != null && feature.Entity.IsOwnedByMe) return true;
		// 只有在远程网络未接管时(IsNetworkControlled == false) 才允许改变 Animator
		return feature.Entity.IsNetworkControlled == false;
	}
}

#endregion

#region[目标组件同步]

[HarmonyPatch(typeof(AITargetComponent))]
public static class Patch_AITargetComponent {

	[HarmonyPostfix]
	[HarmonyPatch(nameof(AITargetComponent.CheckForTarget))]
	public static void Patch_CheckForTarget(AITargetComponent __instance) {
		if(__instance?.target?.targetTagger == null) return;
		// 未锁定远程玩家
		if (!__instance.target.targetTagger.HasTag(MPKeys.REMOTE_PLAYER_TAG)) return;
		// 玩家没有接管
		if (!TargetSyncFeature.TryGetFeature(__instance, out var feature) || !feature.Entity.IsOwnedByMe) return;
		// 锁定远程玩家 发送成为目标请求
		if (__instance.target.targetEntity is RemotePlayer remotePlayer) {
			feature.SetRPAsTarget(remotePlayer.playerId);
		} else if (__instance.target.targetEntity is RemoteProp remoteProp) {
			feature.SetRPAsTarget(remoteProp.remoteEntity.playerId);
		}
		return;
	}
}

#endregion