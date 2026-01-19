using UnityEngine;

namespace WKMPMod.Component;

// 这个组件用来修改玩家名字
public class RemoteTag : MonoBehaviour {
	private TextMesh? _textMesh;
	[Header("Id")]
	public ulong PlayerId;
	[Header("名字")]
	public string? PlayerName;

	void Awake() {
		_textMesh = GetComponent<TextMesh>();
	}

	/// <summary>
	/// 初始化设置(由 CreateNameTagObject 调用一次)
	/// </summary>
	public void Initialize(ulong playerId, string playerName) {
		PlayerId = playerId;
		PlayerName = playerName;
		// 初始显示 Steam 名称
		RefreshName();
	}

	/// <summary>
	/// 更新名称
	/// </summary>
	public void RefreshName() {
		if (_textMesh == null) return;
		_textMesh.text =
			$"{PlayerName}\n" +
			$"ID: {PlayerId.ToString()}\n";
	}

	/// <summary>
	/// 接收远程消息：例如玩家说话、头衔变更等
	/// </summary>
	public void SetDynamicMessage(string message) {
		if (_textMesh == null) return;

		// 示例：显示 "名字\n: 消息内容"
		_textMesh.text =
			$"{PlayerName}\n" +
			$"ID: {PlayerId.ToString()}\n" +
			$"{(message.Length <= 15 ? message : message.Substring(0, 15))}";

	}
}