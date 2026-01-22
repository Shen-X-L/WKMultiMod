using System;
using System.Collections.Generic;
using System.Text;

namespace WKMPMod.Util;

public static class DictionaryExtensions {
	/// <summary>
	/// 查找键以指定数字结尾的项
	/// </summary>
	/// <returns>
	/// (result, matchingKeys) - 返回值 和 匹配的键列表
	/// </returns>
	public static List<ulong> FindByKeySuffix<T>(
		this Dictionary<ulong, T> dictionary, ulong suffix) {

		var matchingKeys = new List<ulong>();

		if (dictionary == null || dictionary.Count == 0)
			return matchingKeys;

		ulong divisor = CalculateDivisor(suffix);

		foreach (var kvp in dictionary) {
			if (kvp.Key % divisor == suffix) {
				matchingKeys.Add(kvp.Key);
			}
		}

		return matchingKeys;
	}

	// 返回对比用的10进制模
	private static ulong CalculateDivisor(ulong suffix) {
		if (suffix == 0) return 10;

		ulong divisor = 1;
		while (divisor <= suffix) {
			divisor *= 10;
		}
		return divisor;
	}
}