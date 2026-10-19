using System;
using System.Collections.Generic;
using System.Text;

namespace WKMPMod.Util;

/// <summary>
/// 复用临时 List / Dictionary / HashSet.
/// 适用于 Unity 主线程, 不是线程安全容器池.
/// </summary>
public static class ReusableCollectionPool {
	private const int MaxPoolSize = 32;

	private static class ListPool<T> {
		private static readonly Stack<List<T>> Pool = new();

		public static List<T> Rent(int capacity = 0) {
			var list = Pool.Count > 0 ? Pool.Pop() : new List<T>(capacity);
			if (list.Capacity < capacity) list.Capacity = capacity;
			return list;
		}

		public static void Return(List<T> list) {
			if (list == null) return;
			list.Clear();
			if (Pool.Count < MaxPoolSize) Pool.Push(list);
		}
	}

	private static class DictionaryPool<TKey, TValue> {
		private static readonly Stack<Dictionary<TKey, TValue>> Pool = new();

		public static Dictionary<TKey, TValue> Rent(int capacity = 0) {
			var dictionary = Pool.Count > 0
				? Pool.Pop() : new Dictionary<TKey, TValue>(capacity);
			return dictionary;
		}

		public static void Return(Dictionary<TKey, TValue> dictionary) {
			if (dictionary == null) return;
			dictionary.Clear();
			if (Pool.Count < MaxPoolSize) Pool.Push(dictionary);
		}
	}

	private static class HashSetPool<T> {
		private static readonly Stack<HashSet<T>> Pool = new();

		public static HashSet<T> Rent() {
			return Pool.Count > 0 ? Pool.Pop() : new HashSet<T>();
		}

		public static void Return(HashSet<T> set) {
			if (set == null) return;
			set.Clear();
			if (Pool.Count < MaxPoolSize) Pool.Push(set);
		}
	}

	public static List<T> RentList<T>(int capacity = 0)
		=> ListPool<T>.Rent(capacity);

	public static void ReturnList<T>(List<T> list)
		=> ListPool<T>.Return(list);

	public static Dictionary<TKey, TValue> RentDictionary<TKey, TValue>(int capacity = 0)
		=> DictionaryPool<TKey, TValue>.Rent(capacity);

	public static void ReturnDictionary<TKey, TValue>(Dictionary<TKey, TValue> dictionary)
		=> DictionaryPool<TKey, TValue>.Return(dictionary);

	public static HashSet<T> RentHashSet<T>()
		=> HashSetPool<T>.Rent();

	public static void ReturnHashSet<T>(HashSet<T> set)
		=> HashSetPool<T>.Return(set);
}
