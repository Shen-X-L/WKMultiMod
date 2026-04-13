using System.Collections;
using UnityEngine;
using WKMPMod.Data;

namespace WKMPMod.UI;

public class UI_LoadingDisplay : MonoBehaviour {
	private Coroutine? _hideCoroutine;      // 当前的隐藏协程
	private float _hideTime = -1f;         // 隐藏时间(-1表示永久显示)
	private float _remainingTime = 0f;     // 剩余显示时间

	#region[Unity生命周期函数]

	private void Awake() {
		MPEventBusGame.OnShowLoading += HandleLoadingRequest;
		MPEventBusGame.OnHideLoading += HideImmediately;
		gameObject.SetActive(false);  // 初始状态为隐藏
	}

	private void OnDestroy() {
		MPEventBusGame.OnShowLoading -= HandleLoadingRequest;
		MPEventBusGame.OnHideLoading -= HideImmediately;
	}

	#endregion

	#region[事件订阅函数]

	private void HandleLoadingRequest(float duration) {
		if (duration <= 0) {
			ShowPermanent();
		} else {
			ShowForSeconds(duration);
		}
	}

	#endregion

	#region[状态设置函数]

	/// <summary>
	/// 设定对象一直显示
	/// </summary>
	public void ShowPermanent() {
		// 停止现有的隐藏协程
		if (_hideCoroutine != null) {
			StopCoroutine(_hideCoroutine);
			_hideCoroutine = null;
		}

		// 设置为永久显示
		_hideTime = -1f;
		_remainingTime = 0f;

		// 显示对象
		gameObject.SetActive(true);
	}

	/// <summary>
	/// 设定显示对象一段时间
	/// </summary>
	public void ShowForSeconds(float seconds) {
		// 参数校验
		if (seconds <= 0) {
			return;
		}

		// 停止现有的隐藏协程
		if (_hideCoroutine != null) {
			StopCoroutine(_hideCoroutine);
			_hideCoroutine = null;
		}

		// 设置新的隐藏时间
		_hideTime = seconds;
		_remainingTime = seconds;

		// 显示对象
		gameObject.SetActive(true);

		// 启动新的隐藏协程
		_hideCoroutine = StartCoroutine(HideAfterDelay());
	}

	/// 延长当前显示的时间(如果是永久显示则转换为定时显示)
	public void ExtendDuration(float seconds) {
		// 永久显示/永久隐藏状态不受影响
		if (_hideTime < 0) {
			return;
		}

		// 如果没有运行中的协程,说明已经隐藏了
		if (_hideCoroutine == null) {
			return;
		}

		// 修改剩余时间
		_remainingTime += seconds;

		// 如果剩余时间 <= 0,立即隐藏
		if (_remainingTime <= 0) {
			HideImmediately();
		} 
	}

	/// <summary>
	/// 立刻隐藏对象, 无论当前状态如何
	/// </summary>
	public void HideImmediately() {
		// 停止隐藏协程
		if (_hideCoroutine != null) {
			StopCoroutine(_hideCoroutine);
			_hideCoroutine = null;
		}

		// 重置状态
		_hideTime = -1f;
		_remainingTime = 0f;

		// 隐藏对象
		gameObject.SetActive(false);
	}

	#endregion

	#region[隐藏对象协程]

	/// <summary>
	/// 用来在设定的时间后隐藏对象的协程
	/// </summary>
	private IEnumerator HideAfterDelay() {
		while (_remainingTime > 0) {
			yield return null;  // 等待下一帧
			_remainingTime -= Time.deltaTime;
		}

		// 时间到,隐藏对象
		gameObject.SetActive(false);
		_hideCoroutine = null;
	}

	#endregion

	#region[状态获取函数]

	/// 获取剩余显示时间(如果是永久显示则返回0)
	public float GetRemainingTime() {
		return _remainingTime;
	}

	/// 获取总显示时间(如果是永久显示则返回-1)
	public bool IsShowing() {
		return gameObject.activeSelf;
	}

	/// 获取是否是永久显示
	public bool IsPermanent() {
		return _hideTime < 0 && gameObject.activeSelf;
	}

	#endregion
}
