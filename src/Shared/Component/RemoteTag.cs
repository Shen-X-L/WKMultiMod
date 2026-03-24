using System.Collections;
using TMPro;
using UnityEngine;
using WKMPMod.Util;

namespace WKMPMod.Component;

// 这个组件用来修改玩家名字
public class RemoteTag : MonoBehaviour {
	private Transform? _cameraTransform;
	private TextMeshPro? _textMeshPro;
	private float _lastUpdateDistance;
	private TickTimer updateTick = new TickTimer(1);

	[Header("Settings")]
	public const float MIN_DISTANCE_LABEL = 10.0f; // 超过此距离显示数字
	public const float DISTANCE_CHANGE_THRESHOLD = 1.0f; // 距离变化超过1米才刷新
	public const float MESSAGE_TIMEOUT = 15.0f; // 消息显示15秒后消失
	[Header("Id")]
	public ulong PlayerId;
	[Header("名字")]
	public string PlayerName = "";
	[Header("Message")]
	public string _message = "";
	private Coroutine? _messageTimeoutCoroutine;

	public string Message { 
		get => _message;
		set {
			string limitedMsg = value.Length <= 15 ? value : value.Substring(0, 15);
			// 如果消息没变化,直接返回
			if (_message == limitedMsg) return;
			_message = limitedMsg;

			// 停止现有的协程
			if (_messageTimeoutCoroutine != null) {
				StopCoroutine(_messageTimeoutCoroutine);
			}
			// 启动新的协程,15秒后清空消息
			_messageTimeoutCoroutine = StartCoroutine(MessageTimeoutRoutine());

			// 立即刷新显示
			RefreshName();
		}
	}

	void Awake() {
		_textMeshPro = GetComponent<TextMeshPro>();
		if (Camera.main != null) 
			_cameraTransform = Camera.main.transform;
	}

	void OnDestroy() {
		// 清理协程
		if (_messageTimeoutCoroutine != null) {
			StopCoroutine(_messageTimeoutCoroutine);
		}
	}

	private void Update() {
		if (!updateTick.TryTick())
			return;
		if (_cameraTransform == null) {
			if (Camera.main != null) _cameraTransform = Camera.main.transform;
			else {
				Debug.LogError("[MP RemoteTag]No main camera found");
				return;
			}
		}
		float currentDistance = Vector3.Distance(transform.position, _cameraTransform.position);

		if (Mathf.Abs(currentDistance - _lastUpdateDistance) >= DISTANCE_CHANGE_THRESHOLD) {
			RefreshName(currentDistance);
		}
	}

	/// <summary>
	/// 初始化设置
	/// </summary>
	public void Initialize(ulong playerId, string playerName) {
		PlayerId = playerId;
		PlayerName = playerName;
	}

	/// <summary>
	/// 更新名称
	/// </summary>
	public void RefreshName() {
		if (_cameraTransform == null) return;
		RefreshName(Vector3.Distance(transform.position, _cameraTransform.position));
	}

	private void RefreshName(float currentDistance) {
		_lastUpdateDistance = currentDistance;
		if (_textMeshPro == null) return;

		// 使用 ToString("F0") 限制小数位数, 避免距离字符串过长且减少内存分配
		string distStr = currentDistance.ToString("F0");

		// 根据距离决定显示格式
		if (currentDistance < MIN_DISTANCE_LABEL) {
			_textMeshPro.text = string.IsNullOrEmpty(_message)
				? PlayerName
				: $"{PlayerName}\n{_message}";
		} else {
			_textMeshPro.text = string.IsNullOrEmpty(_message)
				? $"{PlayerName} ({distStr}m)"
				: $"{PlayerName} ({distStr}m)\n{_message}";
		}
	}

	/// <summary>
	/// 消息超时协程 - 15秒后清空消息
	/// </summary>
	private IEnumerator MessageTimeoutRoutine() {
		yield return new WaitForSeconds(MESSAGE_TIMEOUT);

		// 清空消息
		_message = "";

		// 刷新显示(只显示名字)
		RefreshName();

		// 清除协程引用
		_messageTimeoutCoroutine = null;
	}
}