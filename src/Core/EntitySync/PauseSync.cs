using Steamworks.Data;
using System.Collections.Generic;
using UnityEngine;
using WKMPMod.Components;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.NetWork;
using WKMPMod.Util;
using WKMPMod.World;
using static WKMPMod.Data.MPWriterPool;

namespace WKMPMod.EntitySync;

/// <summary>
/// 暂停处理 (entitymode full): 联机暂停会让暂停者的游戏时间完全停止, 他拥有的生物在所有人那边都会停住.
/// - 本机暂停满 2 秒 (按不受时间缩放影响的时间) 视为 "离开": 广播 Away, 放弃拥有的全部生物, 交出拥有的可拖动物体;
///   离开期间不再申请任何所有权 (只订阅), 画面上网络驱动的位置也不再更新 (取消暂停时从最新位置继续).
/// - 其他人: 选 "最近的玩家" (鼠王 / 遗骸转交) 与可拖动物体接手人时跳过离开中的玩家.
/// - 取消暂停: 广播 Back, 对已登记的生物重新申请所有权或订阅.
/// </summary>
public class PauseSync : Singleton<PauseSync>, ISyncModule {
	private const float AWAY_AFTER = 2f;

	private static readonly HashSet<IDType> _awayPlayers = new();
	private float _pausedSince = -1f;

	private PauseSync() { }

	/// <summary>
	/// 本机是否处于 "暂停离开" 状态
	/// </summary>
	public static bool LocalAway { get; private set; }

	/// <summary>
	/// 本机当前是否暂停 (游戏时间停止, 不再应用网络插值)
	/// </summary>
	public static bool LocalPaused =>
		EntityMode.Current == EntityMode.Mode.Full && CL_GameManager.gMan != null && CL_GameManager.gMan.isPaused;

	public static bool IsAway(IDType playerId) => playerId == MPSteamworks.UserSteamId ? LocalAway : _awayPlayers.Contains(playerId);

	#region[ISyncModule]

	public string ModuleName => "PauseSync";
	public bool IsEnabled { get; set; } = true;

	public void OnResetMap() { }
	public void OnLeave() => Clear();
	public void OnEnd() => Clear();

	private void Clear() {
		_awayPlayers.Clear();
		LocalAway = false;
		_pausedSince = -1f;
	}

	public void OnSyncUpdate(float deltaTime) {
		bool paused = LocalPaused;
		if (!paused) {
			_pausedSince = -1f;
			if (LocalAway) SetAway(false);
			return;
		}
		if (_pausedSince < 0f) _pausedSince = Time.unscaledTime;
		if (!LocalAway && Time.unscaledTime - _pausedSince >= AWAY_AFTER) SetAway(true);
	}

	#endregion

	private void SetAway(bool away) {
		LocalAway = away;
		var writer = GetWriter(MPSteamworks.UserSteamId, MPProtocol.BroadcastId, PacketType.PauseSync);
		writer.Put(away);
		MPSteamworks.Instance.Broadcast(writer, SendType.Reliable);

		var enemySync = EnemySyncModule.Instance;
		var identities = new List<NetworkedGameEntity>();
		foreach (var identity in enemySync.enemies.Values) if (identity != null) identities.Add(identity);
		if (away) {
			var owned = identities.FindAll(x => x.IsOwnedByMe);
			if (owned.Count > 0) enemySync.AbandonOwnership(owned);
			int props = PropSyncModule.Instance.ReleaseForPause();
			MPMain.LogInfo($"[MP Pause] Paused for {AWAY_AFTER:0} s: away, abandoned {owned.Count} entities and {props} props");
		} else {
			if (identities.Count > 0) enemySync.RequestOwnerOrSubscription(identities);
			PropSyncModule.Instance.ResumeAfterPause();
			MPMain.LogInfo($"[MP Pause] Resumed: back, re-requesting {identities.Count} entities");
		}
	}

	[MPPacketHandler(PacketType.PauseSync)]
	public static void HandlePause(IDType senderId, DataReader reader) {
		bool away = reader.GetBool();
		if (away) _awayPlayers.Add(senderId);
		else _awayPlayers.Remove(senderId);
		MPMain.LogInfo($"[MP Pause] {senderId} is {(away ? "away (paused)" : "back")}");
	}
}
