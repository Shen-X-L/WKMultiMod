using UnityEngine;
using WKMPMod.Util;

namespace WKMPMod.Component;

// 这个组件用来修改玩家名字
public class RemoteTag : MonoBehaviour {
	private Transform? _cameraTransform;
	private TextMesh? _textMesh;

	private float _lastUpdateDistance;
	private TickTimer updateTick = new TickTimer(1);

	[Header("Settings")]
	public const float MIN_DISTANCE_LABEL = 10.0f; // 超过此距离显示数字
	public const float DISTANCE_CHANGE_THRESHOLD = 1.0f; // 距离变化超过1米才刷新
	[Header("Id")]
	public ulong PlayerId;
	[Header("名字")]
	public string PlayerName = "";
	[Header("Message")]
	public string _message = "";
	public const float MIN_DISTANCE = 10.0f;

	public string Message { 
		get => _message;
		set {
			string limitedMsg = value.Length <= 15 ? value : value.Substring(0, 15);
			if (_message != limitedMsg) {
				_message = limitedMsg;
				RefreshName(); // 消息变了必须立刻刷新
			}
		}
	}

	void Awake() {
		_textMesh = GetComponent<TextMesh>();
		if (Camera.main != null) 
			_cameraTransform = Camera.main.transform;
	}


	private void LateUpdate() {
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
		if (_textMesh == null) return;

		// 使用 ToString("F0") 限制小数位数, 避免距离字符串过长且减少内存分配
		string distStr = currentDistance.ToString("F0");

		if (currentDistance < MIN_DISTANCE_LABEL) {
			_textMesh.text = $"{PlayerName}\n{_message}";
		} else {
			_textMesh.text = $"{PlayerName} ({distStr}m)\n{_message}";
		}
	}
}