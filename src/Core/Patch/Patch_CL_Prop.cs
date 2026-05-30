using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using WKMPMod.Component;
using WKMPMod.Core;

namespace WKMPMod.Patch;

[HarmonyPatch] // 注意：动态重写不需要在此处指定函数名
public class Patch_CL_Prop {

	/// <summary>
	/// 白名单：这些函数 [绝对不拦截], 保持原样执行或走子类的 override 逻辑. 
	/// 凡是不在这个列表里的 CL_Prop 函数, 默认全部对 RemoteEntity 拦截并跳过 (返回 false). 
	/// </summary>
	private static readonly HashSet<string> Whitelist = new HashSet<string> {
		"Start",               // 需要执行 base.Start() 传递给更底层的 GameEntity 流程
		"Update",              // 已经在 RemoteEntity 中重写为空
		"Damage",              // 已经在 RemoteEntity 中重写了自定义 PVP/伤害计算
		"Kill",                // 已经在 RemoteEntity 中重写
		"Teleport",            // 已经在 RemoteEntity 中重写
		"AddForce",            // 已经在 RemoteEntity 中重写了网络通知
		"AddForceAtPosition",  // 已经在 RemoteEntity 中重写
		"TonguePull",          // 已经在 RemoteEntity 中重写
		"GetRigidbody",        // 允许外部正常获取刚体引用
		"GetColliders",        // 允许外部正常获取碰撞体列表
		"CanInteract"          // 保持正常的交互权限检查
	};

	/// <summary>
	/// 让 Harmony 动态决定这个 Patch 类要绑定到哪些目标函数上
	/// </summary>
	[HarmonyTargetMethods]
	public static IEnumerable<MethodBase> TargetMethods() {
		// 获取 CL_Prop 类中直接声明的所有方法 (不包含从父类继承来且未重写的方法) 
		var allMethods = AccessTools.GetDeclaredMethods(typeof(CL_Prop));

		foreach (var method in allMethods) {
			// 1. 如果在白名单内, 直接跳过, 不进行补丁
			if (Whitelist.Contains(method.Name)) continue;

			// 2. 安全过滤：跳过静态函数、构造函数
			if (method.IsStatic || method.IsConstructor) continue;

			// 3. 安全过滤：跳过编译器自动生成的底层代码 (如 lambda 匿名函数、携程/异步状态机生成的方法) 
			if (method.Name.StartsWith("<") || method.Name.Contains("$")) continue;

			// 满足条件的黑名单函数 (如 FixedUpdate, OnCollision*, Initialize, Drop 等), 全部送去补丁
			yield return method;
		}
	}

	/// <summary>
	/// 统一的前置拦截器
	/// </summary>
	/// <param name="__instance">当前触发函数的实例</param>
	/// <param name="__originalMethod">当前被拦截的原版函数信息 (可用于调试排查) </param>
	[HarmonyPrefix]
	public static bool Prefix_Skip(CL_Prop __instance, MethodBase __originalMethod) {
		// 如果当前实例是远程玩家傀儡, 直接返回 false, 掐断原版逻辑
		if (__instance is RemoteEntity) {
			//MPMain.Debug($"动态拦截并跳过了原版函数: {__originalMethod.Name}");
			return false;
		}

		// 正常的本地 CL_Prop 道具不受影响, 继续执行
		return true;
	}
}
