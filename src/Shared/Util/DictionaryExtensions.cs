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
	public static List<ulong> FindByKeySuffix(IEnumerable<ulong> dictionary, ulong suffix) {
		var matchingKeys = new List<ulong>();

		if (dictionary == null) return matchingKeys;

		ulong divisor = CalculateDivisor(suffix);

		foreach (var value in dictionary) 
			if (value % divisor == suffix) matchingKeys.Add(value);
		
		return matchingKeys;
	}

	/// <summary>
	/// 返回对比用的10进制模
	/// </summary>
	/// <param name="suffix"></param>
	/// <returns>大于入参的最小10次幂</returns>
	private static ulong CalculateDivisor(ulong suffix) {
		if (suffix == 0) return 10;

		ulong divisor = 1;
		while (divisor <= suffix) divisor *= 10;
		
		return divisor;
	}

	// 返回 minuend - subtrahend 的结果(仅保留差值大于0的项)
	public static Dictionary<K, byte> SetDifference<K> (
		Dictionary<K, byte> minuend,   
		Dictionary<K, byte> subtrahend) {

		var result = new Dictionary<K, byte>();
		foreach (var (k, vM) in minuend) {
			if (subtrahend.TryGetValue(k, out var vS)) {
				if (vM > vS) {
					// 在S集存在 且 vM > vS
					result[k] = (byte)(vM - vS);
				}
			}else {
				// 不在S集存在
				result[k] = vM;
			}

		}
		return result;
	}
}