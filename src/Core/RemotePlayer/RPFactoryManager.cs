using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using WKMPMod.Component;
using WKMPMod.Core;
using WKMPMod.Util;
using Object = UnityEngine.Object;

namespace WKMPMod.RemotePlayer;

public class RPFactoryManager : Singleton<RPFactoryManager> {
	// 模型工厂缓存
	private static readonly Dictionary<string, ModelRegistration> _registry = new();
	// 预制体缓存
	private static readonly Dictionary<string, GameObject> _cachedPrefabs = new();

	private readonly string mainBundlePath = Path.Combine(MPMain.path, "player_prefab");

	public static List<string> ModelIDs { get => _registry.Keys.ToList(); }

	#region[生命周期/RAII函数]

	private RPFactoryManager() {
		// 构造时注册主Mod自带的默认模型
		RegisterDefaultModels();
	}

	/// <summary>
	/// 注册主Mod内置的默认皮肤配置
	/// </summary>
	private void RegisterDefaultModels() {
		// 内置默认模型 1: 胶囊体保底
		RegisterModelExtension(new DefaultModelExtension(), mainBundlePath);
		// 内置默认模型 2: 蛞蝓猫
		RegisterModelExtension(new SlugcatModelExtension(), mainBundlePath);
	}

	/// <summary>
	/// 当彻底断开服务器、大退或重置大厅时, 清空内存中的预制体母本缓存, 释放内存
	/// </summary>
	public void ClearPrefabCache() {
		foreach (var prefab in _cachedPrefabs.Values) {
			if (prefab != null) {
				Object.Destroy(prefab);
			}
		}
		_cachedPrefabs.Clear();
		MPMain.LogInfo("[RPFactoryManager] 全局预制体母本缓存已清空释放");
	}

	#endregion
	#region[静态接口]

	/// <summary>
	/// 供第三方Mod或主Mod注册自定义模型扩展
	/// </summary>
	/// <param name="extension">模型接口实现类</param>
	/// <param name="bundlePath">AB包路径</param>
	public static void RegisterModelExtension(ICustomModelExtension extension, string bundlePath) {
		if (extension == null || string.IsNullOrEmpty(extension.ModelId)) return;

		if (_registry.ContainsKey(extension.ModelId)) {
			MPMain.LogWarning(Localization.Get("RPFactoryManager.FactoryAlreadyRegistered", extension.ModelId));
			return;
		}

		_registry[extension.ModelId] = new ModelRegistration {
			Extension = extension,
			BundlePath = bundlePath
		};
		MPMain.LogInfo(Localization.Get("RPFactoryManager.FactoryRegistered", extension.ModelId));
	}

	/// <summary>
	/// 获取指定模型的扩展配置接口
	/// </summary>
	public static ICustomModelExtension GetExtension(string modelId) {
		if (string.IsNullOrEmpty(modelId) || !_registry.TryGetValue(modelId, out var model)) {
			return null;
		}
		return model.Extension;
	}
	#endregion
	#region[对象生成/清理]

	/// <summary>
	/// 统一的模型与对象工厂生成接口
	/// </summary>
	public GameObject Create(string modelId) {
		// 优先从配置注册表获取整套包裹数据
		if (!_registry.TryGetValue(modelId, out var registration)) {
			MPMain.LogWarning($"[RPFactoryManager] 找不到指定的模型注册信息: {modelId}, 将尝试使用 default 模型");
			if (!_registry.TryGetValue("default", out registration)) {
				MPMain.LogError("[RPFactoryManager] default 模型未注册");
				return null;
			}
		}

		ICustomModelExtension extension = registration.Extension;
		string assetName = extension?.PrefabAssetName ?? "CapsulePlayerPrefab";

		// 如果预制体还没加载过, 进行加工
		if (!_cachedPrefabs.TryGetValue(assetName, out GameObject prefabTemplate)) {
			// 将找到的完整 registration 传入加载器, 以便其提取动态路径
			prefabTemplate = LoadAndPreparePrefab(modelId, registration, assetName);
			if (prefabTemplate == null) return null;

			_cachedPrefabs[assetName] = prefabTemplate;
		}

		// 实例化预制体
		GameObject instance = Object.Instantiate(prefabTemplate);

		// 触发实例创建后的处理
		try {
			extension?.OnPlayerInstanceCreated(instance);
		} catch (Exception ex) {
			MPMain.LogError($"[MP Debug] 模型 {modelId} 实例构建回调崩溃: {ex.Message}");
		}

		return instance;
	}

	/// <summary>
	/// 解构原 BaseRemoteFactory 的 LoadAndPrepare 核心逻辑
	/// </summary>
	private GameObject LoadAndPreparePrefab(string modelId, ModelRegistration registration, string assetName) {
		AssetBundle bundle = null;
		GameObject rawPrefab = null;

		// 彻底抛弃硬编码路径, 动态采用注册时提供的 BundlePath
		string path = registration.BundlePath;

		try {
			// 绝对安全的物理路径加载
			bundle = AssetBundle.LoadFromFile(path);
			if (bundle == null) {
				MPMain.LogError(Localization.Get("RPBaseFactory.UnableToLoadResources") + $" 路径: {path}");
				return null;
			}

			rawPrefab = bundle.LoadAsset<GameObject>(assetName);
			if (rawPrefab == null) {
				MPMain.LogError(Localization.Get("RPBaseFactory.PrefabNotLoaded", assetName));
				return null;
			}

			// 执行主框架默认的通用修复 (Shader、影子组件转换等)
			RPPrefabProcessor.RunDefaultPipeline(rawPrefab, modelId);

			// 实例化一个安全的上下文辅助箱, 将当前 Bundle 传进去
			var helper = new AssetHelper(bundle);

			// 移交控制权: 触发第三方的特化处理钩子
			registration.Extension?.OnPrefabLoaded(rawPrefab, helper);

		} catch (Exception ex) {
			MPMain.LogError($"预制体 [{assetName}] 读改异常: {ex.Message}");
			rawPrefab = null;
		} finally {
			// 完美保持原版的安全卸载: 传 false 意味着内存中加载出来的 GameObject 资产不会被销毁
			if (bundle != null) {
				bundle.Unload(false);
			}
		}

		return rawPrefab;
	}

	/// <summary>
	/// 统一的对象回收与资源清理
	/// </summary>
	public void Cleanup(GameObject instance) {
		if (instance == null) return;

		// 如果没有池化需求, 直接销毁即可
		Object.Destroy(instance);

		MPMain.LogInfo($"[RPFactoryManager] 成功清理模型实例, ID: {instance.name}");
	}

	#endregion
	#region[Debug]

	/// <summary>
	/// 仅供调试使用,列出所有注册的工厂信息
	/// </summary>
	public void ListAllFactory() {
		foreach (var (factoryId, registration) in _registry) {
			// 现在完美映射自内嵌的 ModelRegistration 结构
			var ext = registration.Extension;
			MPMain.LogWarning(Localization.Get("RPFactoryManager.DebugFactoryInfo",
				factoryId, ext?.PrefabAssetName ?? "None", registration.BundlePath));
		}
	}

	/// <summary>
	/// 调试API：遍历所有注册的 BundlePath, 打印出对应 AssetBundle 内部的所有资源名称
	/// </summary>
	public void DebugDumpAllBundleContents() {
		// 获取去重后的所有 AB 包路径
		var uniquePaths = _registry.Values.Select(r => r.BundlePath).Distinct().ToList();

		MPMain.LogWarning($"\n=== [RPFactoryManager Debug] 开始扫描所有 AB 包内容 (共 {uniquePaths.Count} 个包) ===");

		foreach (var path in uniquePaths) {
			MPMain.LogWarning($"正在检查物理文件路径: {path}");
			if (!File.Exists(path)) {
				MPMain.LogError($"[错误] 该路径上的物理文件并不存在！");
				continue;
			}

			AssetBundle bundle = null;
			try {
				// 临时加载包读取信息
				bundle = AssetBundle.LoadFromFile(path);
				if (bundle == null) {
					MPMain.LogError($"[错误] 无法载入该 AssetBundle 物理文件！");
					continue;
				}

				// 获取内部所有资源的名称 (Unity默认会返回小写全路径, 如 assets/prefabs/capsuleplayerprefab.prefab) 
				string[] assetNames = bundle.GetAllAssetNames();
				MPMain.LogWarning($"[成功] 载入成功. 该 AB 包内包含 {assetNames.Length} 个资源：");

				for (int i = 0; i < assetNames.Length; i++) {
					MPMain.LogWarning($"   -> 索引 [{i}]: {assetNames[i]}");
				}

			} catch (Exception ex) {
				MPMain.LogError($"[异常] 读取 AB 包内容时崩溃: {ex.Message}");
			} finally {
				if (bundle != null) {
					// 仅卸载包本身, 不销毁内存中可能的已有资源
					bundle.Unload(false);
				}
			}
		}
		MPMain.LogWarning("=== [RPFactoryManager Debug] AB 包内容扫描结束 ===\n");
	}

	/// <summary>
	/// 调试API：以一定的时间间隔, 依次创建所有注册的模型预制体实例
	/// </summary>
	/// <param name="intervalSeconds">创建间隔时间 (秒) </param>
	public void DebugSpawnAllPrefabsSequentially(float intervalSeconds) {
		// 启动协程流程
		MPCore.Instance.StartCoroutine(SpawnFlow(ModelIDs, intervalSeconds));

		IEnumerator SpawnFlow(List<string> modelIds, float interval) {
			MPMain.LogWarning($"\n=== [RPFactoryManager Debug] 开始定时生成测试 ===");
			MPMain.LogWarning($"总计模型数: {modelIds.Count} 个, 生成间隔: {interval} 秒");

			int index = 0;
			foreach (var modelId in modelIds) {
				MPMain.LogWarning($"[测试 {index + 1}/{modelIds.Count}] 正在尝试创建模型ID: '{modelId}'");

				try {
					GameObject instance = RPFactoryManager.Instance.Create(modelId);
					if (instance != null) {
						// 为了防止模型全部叠在 0,0,0 点, 让它们在 X 轴上排开
						instance.transform.position = new Vector3(index * 3.0f, 0f, 0f);
						instance.name = $"Debug_PlayerInstance_{modelId}";

						MPMain.LogWarning($"[成功] 模型 '{modelId}' 实例化成功！世界坐标: {instance.transform.position}");
					} else {
						MPMain.LogError($"[失败] 模型 '{modelId}' 创建返回了 null 实例");
					}
				} catch (Exception ex) {
					MPMain.LogError($"[崩溃] 创建模型 '{modelId}' 时触发代码异常: {ex.Message}\n{ex.StackTrace}");
				}

				index++;
				// 等待指定间隔
				yield return new WaitForSeconds(interval);
			}
			MPMain.LogWarning($"=== [RPFactoryManager Debug] 定时生成测试完毕, 调试组件已自动销毁 ===\n");
		}
	}


	#endregion

	private class AssetHelper : IAssetHelper {
		private readonly AssetBundle _bundle;
		public AssetHelper(AssetBundle bundle) { _bundle = bundle; }

		public T GetCustomAsset<T>(string assetName) where T : Object {
			if (_bundle == null) return null;
			return _bundle.LoadAsset<T>(assetName);
		}
	}

	private class ModelRegistration {
		public ICustomModelExtension Extension { get; set; }
		public string BundlePath { get; set; }
	}
}