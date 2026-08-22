using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using WKMPModa.Shared.Data;

namespace WKMPModa.Shared.Component;

/// <summary>
/// 挂载在玩家模型克隆体上的运行时控制器基类
/// </summary>
public abstract class CustomModelBehaviour : MonoBehaviour {
	/// <summary>
	/// 默认模型手持物品时的坐标变换
	/// </summary>
	/// <returns>return new() {{ "None",(Vector3.zero, Quaternion.identity, Vector3.one)}</returns>
	public virtual Dictionary<string, ItemPoseData> HandItemTransform { get; }

	/// <summary>
	/// 预制体处理
	/// </summary>
	public virtual void OnPrefabLoaded() { }

	/// <summary>
	/// 修改玩家颜色
	/// </summary>
	public virtual void ApplyPlayerColor(Color32 color) { }

	/// <summary>
	/// 切换玩家下蹲状态
	/// </summary>
	public virtual void SetCrouching(bool isCrouching) { }

	/// <summary>
	/// 处理玩家同步字典数据
	/// </summary>
	public virtual void HandlePlayerData(Dictionary<string, string> playerData) { }
}