using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using WKMPMod.Core;
using WKMPMod.Util;

namespace WKMPMod.World;

public class WorldSyncManager : MonoSingleton<WorldSyncManager> {
	private readonly Dictionary<string, ISyncModule> _modules = new();

	protected override void Awake() {
		base.Awake();
		SceneManager.sceneLoaded += OnSceneLoaded;
		// 注册所有同步模块
		RegisterAllModules(typeof(WorldSyncManager).Assembly);
	}

	public void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
		ResetAll();
	}

	public void RegisterModule(ISyncModule module) {
		MPMain.LogDebug(module.ModuleName);
		_modules.Add(module.ModuleName, module);
	}

	public bool TryGetModule(string name, out ISyncModule module) {
		return _modules.TryGetValue(name, out module);
	}

	private void LateUpdate() {
		// 全局调度前置条件校验
		if (!MPCore.IsReady) return;
		float deltaTime = Time.unscaledDeltaTime;
		// Ready 阶段: 不依赖连接
		foreach (var module in _modules.Values) {
			if (module.IsEnabled) module.ReadyUpdate(deltaTime);
		}
		// Sync 阶段: 依赖连接
		if (!MPCore.CanSync) return;
		foreach (var module in _modules.Values) {
			if (module.IsEnabled) module.SyncUpdate(deltaTime);
		}
	}

	public void ResetAll() {
		foreach (var module in _modules.Values) module.OnResetMap();
	}

	public void LeaveAll() {
		foreach (var module in _modules.Values) module.OnLeave();
	}

	public void EndAll() {
		foreach (var module in _modules.Values) module.OnEnd();
	}

	private void RegisterAllModules(Assembly assembly) {
		Type singletonType = typeof(Singleton<>);

		foreach (Type type in assembly.GetTypes()) {
			// 过滤抽象类, 接口
			if (!type.IsClass || type.IsAbstract) continue;
			// 必须实现 ISyncModule
			if (!typeof(ISyncModule).IsAssignableFrom(type)) continue;
			// 查找 Singleton 基类
			Type baseType = type.BaseType;
			if (baseType == null 
				|| !baseType.IsGenericType // 泛型
				|| baseType.GetGenericTypeDefinition() != typeof(Singleton<>)) {
				continue;
			}
			// 获取泛型参数
			if (baseType.GetGenericArguments()[0] != type) continue;
			// 获取 Singleton 实例
			PropertyInfo property = baseType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
			if (property == null) continue;
			var instance = property.GetValue(null) as ISyncModule;
			if (instance != null) RegisterModule(instance);
		}
	}
}
public interface ISyncModule {
	string ModuleName { get; }
	bool IsEnabled { get; set; }

	/// <summary>
	/// 定频 Tick 执行 (发送/定时校验)
	/// </summary>
	void SyncUpdate(float deltaTime) { }

	void ReadyUpdate(float deltaTime) { }

	/// <summary>
	/// 场景重启状态重置 
	/// </summary>
	void OnResetMap();

	/// <summary>
	/// 大厅连接断线状态重置 
	/// </summary>
	void OnLeave();

	/// <summary>
	/// 退出游玩
	/// </summary>
	void OnEnd();
}