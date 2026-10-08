using UnityEngine;

namespace WKMPMod.Util;

public abstract class MonoSingleton<T> : MonoBehaviour where T : MonoSingleton<T> {
	private static T _instance;
	private static readonly object _lock = new object();
	private static bool _applicationIsQuitting = false;

	public static T Instance {
		get {
			// 如果程序正在退出,不再创建新实例,防止残留
			if (_applicationIsQuitting) return null;

			lock (_lock) {
				if (_instance == null) {
					// 在场景中查找 挂载组件的对象
					_instance = FindAnyObjectByType<T>();

					// 找不到则自动创建一个 根对象并挂载组件
					if (_instance == null) {
						GameObject singleton = new GameObject(typeof(T).Name);
						_instance = singleton.AddComponent<T>();

						// 确保在场景切换时不被销毁
						DontDestroyOnLoad(singleton);
					}
				}
				return _instance;
			}
		}
	}

	/// <summary>
	/// 指定特定的 GameObject 挂载该单例组件,并让 _instance 指向它
	/// </summary>
	/// <param name="target">指定的挂载目标</param>
	/// <param name="dontDestroyOnLoad">是否在跨场景时保持不销毁（默认 true）</param>
	/// <returns>返回挂载的单例组件实例</returns>
	public static T InitializeOn(GameObject target, bool dontDestroyOnLoad = true) {
		if (_applicationIsQuitting) return null;

		if (target == null) {
			Debug.LogError($"MonoSingleton<{typeof(T).Name}>: Target GameObject cannot be null!");
			return null;
		}

		lock (_lock) {
			// 如果当前已有单例实例
			if (_instance != null) {
				// 如果已经挂在目标对象上了,直接返回
				if (_instance.gameObject == target) return _instance;

				// 如果挂在其他对象上,提示警告并将旧对象组件移除/销毁
				Debug.LogWarning($"MonoSingleton<{typeof(T).Name}>: Instance already exists on '{_instance.gameObject.name}', re-attaching to '{target.name}'.");
				Destroy(_instance);
			}

			// 检查目标对象上是否已有该组件,有则复用,无则添加
			_instance = target.GetComponent<T>();
			if (_instance == null) _instance = target.AddComponent<T>();

			// 保证根节点或指定节点跨场景持久化
			if (dontDestroyOnLoad && target.transform.parent == null) 
				DontDestroyOnLoad(target);
			
			return _instance;
		}
	}

	protected virtual void Awake() {
		if (_instance == null) {
			_instance = (T)this;
			// 如果是手动拖入场景的,也确保跨场景持久化
			if (transform.parent == null) DontDestroyOnLoad(gameObject);
		} else if (_instance != this) {
			// 发现重复,立刻自毁
			Debug.LogWarning($"MonoSingleton<{typeof(T).Name}>: Duplicate components were found in the scene and have been automatically destroyed");
			Destroy(this);
		}
	}

	protected virtual void OnApplicationQuit() {
		_applicationIsQuitting = true;
	}

	protected virtual void OnDestroy() {
		// 如果是当前实例被销毁,清空引用
		if (_instance == this) _instance = null;
	}

}