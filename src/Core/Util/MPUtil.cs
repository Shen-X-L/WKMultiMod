using System;
using System.Collections.Generic;
using System.Text;

namespace WKMPMod.Util;

public static class MPUtil {
	/// <summary>
	/// 标准化PrefabKey
	/// 去除空格和Unity实例化后附加的(Clone)后缀<br/>
	///
	/// Normalizes a prefab key
	/// Removes whitespace and Unity's instantiated "(Clone)" suffix
	/// </summary>

	public static string CleanCloneName(string prefabKey) {
		if (string.IsNullOrEmpty(prefabKey)) return string.Empty;

		return prefabKey.Replace("(Clone)", string.Empty).Trim();
	}
}
