using UnityEngine;

namespace WKMultiMod.src.Util;

public class TickTimer {
	// 特定频率
	private float _interval;
	private float _lastUpdateTime;
	// 如果想看还差多久,可以提供只读属性
	public float Progress => Mathf.Clamp01((Time.time - _lastUpdateTime) / _interval);

	//设置固定时间时触发
	public TickTimer(float tick) {
		_interval = tick;
		_lastUpdateTime = -_interval; // 初始值设为负数,确保第一次检查立即通过
	}

	//设置固定频率时触发
	public TickTimer(int hz) {
		_interval = 1f / hz;
		_lastUpdateTime = -_interval; // 初始值设为负数,确保第一次检查立即通过
	}

	// 设置间隔
	public void SetTick(float tick) {
		_interval = tick;
	}

	// 设置频率
	public void SetHz(float hz) {
		_interval = 1f/hz;
	}

	/// <summary>
	/// 检查间隔是否到达。如果到达,则自动更新内部计时器并返回 true。
	/// </summary>
	public bool IsTick() {
		if (Time.time - _lastUpdateTime >= _interval) {
			_lastUpdateTime = Time.time;
			return true;
		}
		return false;
	}
}
