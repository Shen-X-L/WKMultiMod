using HarmonyLib;
using UnityEngine;
using WKMPMod.Components;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.NetWork;
using WKMPMod.RemotePlayers;
using WKMPMod.World;

namespace WKMPMod.EntitySync;

/// <summary>
/// 只追本机玩家的怪 (鼠王 DEN_RatKing / 遗骸 DEN_Remains_Active, entitymode full):
/// 原版 AI 写死目标为 ENT_Player.GetPlayer(), 联机时只有所有者那边真正跑 AI, 所以怪永远只追所有者.
/// - 所有者: 每 0.5 秒比较各玩家距离, 有别人比自己近 5 m 以上并持续 1 秒 (或自己已死) 时, 把所有权转交给最近的人 →
///   由被追的人本地运行原版 AI (追他、对他判定击杀).
/// - 非所有者: 网络接管期间跳过原版 Update / TickUpdate (见 Patch_ChaseOwner), 位置完全由所有者驱动.
/// - 遗骸 "被看见就不动": 非所有者看见它时每 0.25 秒告诉所有者, 所有者在 0.5 秒内取消这一 tick 的移动.
/// </summary>
public class ChaseOwnerFeature : ICustomSyncFeature, IFrameUpdateFeature {
	private const float CHECK_INTERVAL = 0.5f;
	private const float HANDOVER_MARGIN = 5f;
	private const float HANDOVER_DELAY = 1f;
	private const float SEEN_SEND_INTERVAL = 0.25f;
	private const float SEEN_HOLD = 0.5f;

	private enum Message : byte { Seen = 1 }

	private static readonly AccessTools.FieldRef<DEN_Remains_Active, bool> KillingField =
		AccessTools.FieldRefAccess<DEN_Remains_Active, bool>("isKillingPlayer");

	private readonly NetworkedGameEntity _net;
	private readonly DEN_Remains_Active? _remains;
	private float _checkTimer;
	private IDType _candidate;
	private float _candidateSince;
	private float _seenSendTimer;
	private float _remoteSeenTime = -10f;

	public ChaseOwnerFeature(NetworkedGameEntity net) {
		_net = net;
		_remains = net.Entity as DEN_Remains_Active;
	}

	public string FeatureId => "ChaseOwner";
	public byte CustomIndex { get; set; }

	public void OnReset() {
		_candidate = 0;
		_remoteSeenTime = -10f;
	}

	public void OnDestroy() { }

	/// <summary>
	/// 所有者: 别人最近看见了这只遗骸 (原版只在本机看见时才停)
	/// </summary>
	public bool RemoteSeen => Time.time - _remoteSeenTime < SEEN_HOLD;

	public void OnUpdate(float deltaTime) {
		if (!MPCore.CanSync || !_net.HasOwner || _net.Entity == null || _net.Entity.dead) return;
		if (_net.IsOwnedByMe) {
			_checkTimer += deltaTime;
			if (_checkTimer < CHECK_INTERVAL) return;
			_checkTimer = 0f;
			CheckHandover();
		} else if (_remains != null && _net.IsNetworkControlled && _remains.meshRenderer != null && _remains.meshRenderer.isVisible) {
			_seenSendTimer += deltaTime;
			if (_seenSendTimer < SEEN_SEND_INTERVAL) return;
			_seenSendTimer = 0f;
			EnemySyncModule.Instance.SendCustomFeature(_net.CurrentOwnerId, _net.networkId, CustomIndex,
				w => w.Put((byte)Message.Seen));
		}
	}

	private void CheckHandover() {
		if (_remains != null && KillingField(_remains)) return;   // 正在扭脖子: 不换人
		Vector3 pos = _net.transform.position;
		var local = ENT_Player.GetPlayer();
		bool localAlive = local != null && !local.IsDead();
		float localDist = localAlive ? Vector3.Distance(local.transform.position, pos) : float.MaxValue;

		IDType nearest = 0;
		float nearestDist = float.MaxValue;
		foreach (var pair in RPManager.Instance.Players) {
			var obj = pair.Value?.PlayerObject;
			if (obj == null || !obj.activeInHierarchy || !MPSteamworks.ContainsMemberID(pair.Key)) continue;
			float d = Vector3.Distance(obj.transform.position, pos);
			if (d < nearestDist) {
				nearestDist = d;
				nearest = pair.Key;
			}
		}
		if (nearest == 0 || nearestDist + HANDOVER_MARGIN >= localDist) {
			_candidate = 0;
			return;
		}
		if (_candidate != nearest) {
			_candidate = nearest;
			_candidateSince = Time.time;
		}
		if (localAlive && Time.time - _candidateSince < HANDOVER_DELAY) return;
		_candidate = 0;
		MPMain.LogInfo($"[MP ChaseOwner] {_net.Entity.name} {_net.networkId}: {nearest} is nearer ({nearestDist:0.#} m vs {(localAlive ? localDist.ToString("0.#") + " m" : "dead")}), handing over");
		EnemySyncModule.Instance.RequestOwnerTransfer(_net.networkId, nearest);
	}

	public void HandleCustomMessage(IDType senderId, DataReader reader, bool needReply) {
		if ((Message)reader.GetByte() == Message.Seen && _net.IsOwnedByMe) _remoteSeenTime = Time.time;
	}

	public void HandleCustomReply(IDType senderId) { }

	/// <summary>
	/// Harmony 用: 该实体在本机是否由网络驱动 (此时跳过原版 AI)
	/// </summary>
	public static bool IsDrivenRemotely(Component entity) =>
		EntityMode.Current == EntityMode.Mode.Full && entity.TryGetComponent<NetworkedGameEntity>(out var net) && net.IsNetworkControlled;

	public static ChaseOwnerFeature? Find(Component entity) {
		if (!entity.TryGetComponent<NetworkedGameEntity>(out var net)) return null;
		foreach (var feature in net.Features) if (feature is ChaseOwnerFeature chase) return chase;
		return null;
	}
}
