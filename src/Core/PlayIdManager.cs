using Steamworks;
using System.Collections.Generic;
using UnityEngine;
using WKMultiMod.src.NetWork;

namespace WKMultiMod.src.Core;

public class PlayerIdManager : MonoBehaviour {
	private Dictionary<SteamId, int> _steamIdToPlayerId = new Dictionary<SteamId, int>();
	private Dictionary<int, SteamId> _playerIdToSteamId = new Dictionary<int, SteamId>();
	private int _nextPlayerId = 1; // 0保留给本地玩家或无效ID

	void Awake() {
		// 订阅Steam连接事件
		SteamNetworkEvents.OnPlayerConnected += ProcessSteamPlayerConnected;
		SteamNetworkEvents.OnPlayerDisconnected += ProcessSteamPlayerDisconnected;
	}

	void OnDestroy() {
		SteamNetworkEvents.OnPlayerConnected -= ProcessSteamPlayerConnected;
		SteamNetworkEvents.OnPlayerDisconnected -= ProcessSteamPlayerDisconnected;
	}

	public void ResetAll() {
		_steamIdToPlayerId.Clear();
		_playerIdToSteamId.Clear();
		_nextPlayerId = 1;
	}

	/// <summary>
	/// 分配新玩家ID映射
	/// </summary>
	/// <param name="steamId"></param>
	private void ProcessSteamPlayerConnected(SteamId steamId) {
		if (!_steamIdToPlayerId.ContainsKey(steamId)) {
			int playerId = _nextPlayerId++;
			_steamIdToPlayerId[steamId] = playerId;
			_playerIdToSteamId[playerId] = steamId;

			MPMain.Logger.LogInfo($"[MP Mod] 分配玩家ID: SteamId={steamId} -> PlayerId={playerId}");
		}
	}

	/// <summary>
	/// 断连时删除玩家ID映射
	/// </summary>
	/// <param name="steamId"></param>
	private void ProcessSteamPlayerDisconnected(SteamId steamId) {
		if (_steamIdToPlayerId.TryGetValue(steamId, out int playerId)) {
			_steamIdToPlayerId.Remove(steamId);
			_playerIdToSteamId.Remove(playerId);

			MPMain.Logger.LogInfo($"[MP Mod] 移除玩家映射: SteamId={steamId}, PlayerId={playerId}");
		}
	}

	/// <summary>
	/// 分配玩家ID（如果已存在则返回现有的）
	/// </summary>
	public int AssignPlayerId(SteamId steamId) {
		if (!_steamIdToPlayerId.TryGetValue(steamId, out int playerId)) {
			playerId = _nextPlayerId++;
			_steamIdToPlayerId[steamId] = playerId;
			_playerIdToSteamId[playerId] = steamId;
			MPMain.Logger.LogInfo($"[MP Mod] 分配玩家ID: {steamId} -> {playerId}");
		}
		return playerId;
	}

	/// <summary>
	/// 获取所有现有玩家ID（排除指定玩家）
	/// </summary>
	public List<int> GetAllExistingPlayerIds(SteamId excludeSteamId) {
		var result = new List<int>();

		foreach (var kvp in _steamIdToPlayerId) {
			if (!kvp.Key.Equals(excludeSteamId)) {
				result.Add(kvp.Value);
			}
		}

		return result;
	}

	public int GetPlayerId(SteamId steamId) {
		return _steamIdToPlayerId.TryGetValue(steamId, out int playerId) ? playerId : -1;
	}

	public SteamId GetSteamId(int playerId) {
		return _playerIdToSteamId.TryGetValue(playerId, out var steamId) ? steamId : default;
	}

	public int GetNextPlayerId() => _nextPlayerId;
}