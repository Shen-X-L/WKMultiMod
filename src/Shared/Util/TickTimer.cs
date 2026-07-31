using UnityEngine;

namespace WKMPMod.Util;

public class TickTimer {
	// 触发频率
	private float _interval;
	// 上次触发
	private float _lastTickTime;
	// 使用 真实/游戏时间
	private bool _useUnscaledTime;

	/// <summary>
	/// 是否使用不受时间缩放影响的真实时间 (Time.unscaledTime)
	/// 可以在运行时动态切换, 计时器会自动补偿时间差以确保进度平滑过渡
	/// </summary>
	public bool UseUnscaledTime {
		get => _useUnscaledTime;
		set {
			// 如果状态没有改变, 直接返回
			if (_useUnscaledTime == value) return;

			// 在切换前, 计算在旧时间轴上已经流逝的时间
			float elapsed = CurrentTime - _lastTickTime;

			// 切换状态
			_useUnscaledTime = value;

			// 使用新时间轴当前的时间, 减去已经流逝的时间, 重新校准 _lastTickTime
			// 这样可以保证切换瞬间, TimeRemaining 和 Progress 保持不变
			_lastTickTime = CurrentTime - elapsed;
		}
	}

	/// <summary>
	/// 获取当前环境下的参照时间
	/// </summary>
	private float CurrentTime => _useUnscaledTime ? Time.unscaledTime : Time.time;

	/// <summary>
	/// 当前Tick进度 (0-1)
	/// </summary>
	public float Progress => Mathf.Clamp01((CurrentTime - _lastTickTime) / _interval);

	/// <summary>
	/// 距离下次Tick还有多少秒
	/// </summary>
	public float TimeRemaining => Mathf.Max(0, _interval - (CurrentTime - _lastTickTime));

	/// <summary>
	/// 是否已经到达Tick时间(仅检查)
	/// </summary>
	public bool IsTickReached => CurrentTime - _lastTickTime >= _interval;

	//
	/// <summary>
	/// 设置固定时间时触发
	/// </summary>
	/// <param name="tick">时间间隔(秒)</param>
	/// <param name="useUnscaledTime">是否使用真实时间(不受Time.timeScale影响)</param>
	public TickTimer(float tick, bool useUnscaledTime = false) {
		_useUnscaledTime = useUnscaledTime;
		_interval = tick;
		// 初始值设为当前时间减去间隔, 确保第一次检查立即通过
		_lastTickTime = CurrentTime - _interval;
	}

	/// <summary>
	/// 设置固定频率时触发
	/// </summary>
	/// <param name="hz">每秒触发次数</param>
	/// <param name="useUnscaledTime">是否使用真实时间(不受Time.timeScale影响)</param>
	public TickTimer(int hz, bool useUnscaledTime = false) {
		_useUnscaledTime = useUnscaledTime;
		_interval = 1f / hz;
		// 初始值设为当前时间减去间隔, 确保第一次检查立即通过
		_lastTickTime = CurrentTime - _interval;
	}

	//
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
		_lastTickTime = CurrentTime;
	}

	/// <summary>
	/// 尝试触发一次Tick. 如果到达间隔时间,则更新计时器并返回true
	/// </summary>
	public bool TryTick() {
		if (CurrentTime - _lastTickTime >= _interval) {
			_lastTickTime = CurrentTime;
			return true;
		}
		return false;
	}

	/// <summary>
	/// 强制触发一次Tick(无论是否到达间隔时间)
	/// </summary>
	public void ForceTick() {
		_lastTickTime = CurrentTime;
	}
}