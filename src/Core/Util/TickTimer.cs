using UnityEngine;

namespace WKMPMod.Util;

public class TickTimer {
	// 特定频率
	private float _interval;
	private float _lastTickTime;
	/// 当前Tick进度 (0-1)
	public float Progress => Mathf.Clamp01((Time.time - _lastTickTime) / _interval);
	/// 距离下次Tick还有多少秒
	public float TimeRemaining => Mathf.Max(0, _interval - (Time.time - _lastTickTime));
	/// 是否已经到达Tick时间(仅检查)
	public bool IsTickReached => Time.time - _lastTickTime >= _interval;

	/// <summary>
	/// 设置固定时间时触发
	/// </summary>
	public TickTimer(float tick) {
		_interval = tick;
		_lastTickTime = -_interval; // 初始值设为负数,确保第一次检查立即通过
	}

	/// <summary>
	/// 设置固定频率时触发
	/// </summary>
	public TickTimer(int hz) {
		_interval = 1f / hz;
		_lastTickTime = -_interval; // 初始值设为负数,确保第一次检查立即通过
	}

	/// <summary>
	/// 设置间隔
	/// </summary>
	public void SetInterval(float tick) {
		_interval = tick;
	}

	/// <summary>
	/// 设置频率
	/// </summary>
	public void SetFrequency(float hz) {
		_interval = 1f / hz;
	}

	/// <summary>
	/// 重置计时器,重新开始计时
	/// </summary>
	public void Reset() {
		_lastTickTime = Time.time;
	}

	/// <summary>
	/// 尝试触发一次Tick。如果到达间隔时间,则更新计时器并返回true
	/// </summary>
	public bool TryTick() {
		if (Time.time - _lastTickTime >= _interval) {
			_lastTickTime = Time.time;
			return true;
		}
		return false;
	}

	/// <summary>
	/// 强制触发一次Tick(无论是否到达间隔时间)
	/// </summary>
	public void ForceTick() {
		_lastTickTime = Time.time;
	}
}
