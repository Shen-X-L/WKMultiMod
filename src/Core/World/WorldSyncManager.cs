using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using WKMPMod.Core;
using WKMPMod.Util;

namespace WKMPMod.World;

public class WorldSyncManager : MonoSingleton<WorldSyncManager> {
	private readonly Dictionary<string,ISyncModule> _modules = new();

	protected override void Awake() {
		base.Awake();
		SceneManager.sceneLoaded += OnSceneLoaded;
		// 注册所有同步模块 (以后有新模块只需在此 Add)
		RegisterModule(EnemySyncModule.Instance);
		RegisterModule(ClimbableSyncModule.Instance);
		RegisterModule(SceneItemModule.Instance);
		RegisterModule(DroppedItemModule.Instance);

	}

	public void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
		ResetAll();
	}

	public void RegisterModule(ISyncModule module) {
		_modules.Add(module.ModuleName,module);
	}

	public bool TryGetModule(string name,out ISyncModule module) { 
		return _modules.TryGetValue(name, out module);
	}

	private void LateUpdate() {
		// 全局调度前置条件校验
		if (!MPCore.CanSync) return;

		float deltaTime = Time.unscaledDeltaTime; // 不受游戏暂停影响

		foreach (var module in _modules.Values) {
			if (module.IsEnabled) module.OnSyncUpdate(deltaTime); // 每帧分发
		}
	}

	public void ResetAll() {
		foreach (var module in _modules.Values) module.OnReset();
	}

	public void LeaveAll() {
		foreach (var module in _modules.Values) module.OnLeave();
	}

	public void EndAll() {
		foreach (var module in _modules.Values) module.OnEnd();
	}
}
public interface ISyncModule {
	string ModuleName { get; }
	bool IsEnabled { get; set; }

	/// <summary>
	/// 定频 Tick 执行 (发送/定时校验)
	/// </summary>
	void OnSyncUpdate(float deltaTime);

	/// <summary>
	/// 场景重启状态重置 
	/// </summary>
	void OnReset();

	/// <summary>
	/// 大厅连接断线状态重置 
	/// </summary>
	void OnLeave();

	/// <summary>
	/// 退出游玩
	/// </summary>
	void OnEnd();
}