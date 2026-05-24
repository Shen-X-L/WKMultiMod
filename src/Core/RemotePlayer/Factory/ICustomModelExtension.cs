using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace WKMPMod.RemotePlayer;

public interface ICustomModelExtension {
	/// <summary>
	/// 唯一的模型ID
	/// </summary>
	string ModelId { get; }

	/// <summary>
	/// 模型预制体资产名字是什么 (通过 WKLib 加载/自定义路径) 
	/// </summary>
	string PrefabAssetName { get; }

	/// <summary>
	/// 自定义生成特效的资产名字 (目前仅支持游戏内资产名) (如果为 null 则使用主Mod默认特效) 
	/// </summary>
	string SpawnEffectAssetName { get; }

	/// <summary>
	/// 自定义死亡特效的资产名字 (目前仅支持游戏内资产名) (如果为 null 则使用主Mod默认特效) 
	/// </summary>
	string DeathEffectAssetName { get; }

	/// <summary>
	/// 自定义受伤特效的资产名字 (目前仅支持游戏内资产名) (如果为 null 则使用主Mod默认特效) 
	/// </summary>
	string DamageEffectAssetName { get; }

	/// <summary>
	/// 当预制体模板第一次被从AB包加载到内存时调用
	/// 全局只触发一次
	/// </summary>
	/// <param name="prefabTemplate">内存中的预制体</param>
	/// <param name="assetHelper">资源加载辅助器</param>
	public void OnPrefabLoaded(GameObject prefab, IAssetHelper assetHelper);

	/// <summary>
	/// 当某个具体的远程玩家进入房间, 克隆出实例时调用
	/// 每个人进来都会触发一次
	/// </summary>
	/// <param name="playerInstance">被克隆出来的玩家实例克隆体</param>
	void OnPlayerInstanceCreated(GameObject playerInstance);
}
