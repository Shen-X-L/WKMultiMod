using HarmonyLib;
using System;
using System.Collections;
using UnityEngine;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.Patch;
using WKMPMod.World;

namespace WKMPMod.Components;

public class PhageSyncFeature : IStateSyncFeature, ICustomSyncFeature {
	private static readonly Action<DEN_Phage> _onKillPlayer =
		AccessTools.MethodDelegate<Action<DEN_Phage>>(AccessTools.Method(typeof(DEN_Phage), "OnKillPlayer"));
	// 动画重置hashID
	private const int RESET_ANIMA_HASH = -554291977;

	private readonly NetworkedGameEntity _netEntity;
	private readonly DEN_Phage _phage;
	private bool _killAnimationStarted;

	#region[注视状态]

	private bool _canSeeTarget;
	private bool _canRememberTarget;

	#endregion

	public PhageSyncFeature(DEN_Phage phage, NetworkedGameEntity netEntity) {
		_phage = phage;
		_netEntity = netEntity;
	}

	#region[ISyncFeature 基础实现]

	public string FeatureId => _featureId;
	public const string _featureId = "Phage";

	public void OnReset() { }

	public void OnDestroy() {

	}

	#endregion

	#region[IStateSyncFeature 状态同步实现]

	public byte StateIndex { get; set; }
	public bool IsDirty => 
		_canSeeTarget != Patch_DEN_Phage.canSeeTargetField(_phage)
		|| _canRememberTarget != Patch_DEN_Phage.canRememberTargetField(_phage);
	public void WriteState(DataWriter writer) {
		_canSeeTarget = Patch_DEN_Phage.canSeeTargetField(_phage);
		_canRememberTarget = Patch_DEN_Phage.canRememberTargetField(_phage);
		writer.Put((byte)((_canSeeTarget ? 1 : 0) | ((_canRememberTarget ? 1 : 0) << 1)));
	}
	public void ReadState(DataReader reader) { 
		if (_phage == null) return;
		byte state = reader.GetByte();
		_canSeeTarget = (state & 1) != 0;
		_canRememberTarget = (state & 2) != 0;
		Patch_DEN_Phage.canSeeTargetField(_phage)= _canSeeTarget;
		Patch_DEN_Phage.canRememberTargetField(_phage) = _canRememberTarget;
	}

	#endregion

	#region[ICustomSyncFeature 信号式交互实现]

	public byte CustomIndex { get; set; }
	public void HandleCustomMessage(IDType senderId,DataReader reader,bool needReply) {
		byte option = reader.GetByte();
		switch (option) {
			case 0:
				HandleAttackHit(senderId);
				break;
			case 1:
				HandleVictimKillTriggered(senderId);
				break;
			default:
				MPMain.LogWarning($"[MP PhageSF] Unknown PhageSync message: {option}");
				break;
		}
	}

	public void HandleCustomReply(IDType senderId) {

	}

	#endregion

	#region[网络消息]

	private void SendAttackHit(IDType targetId) {
		if (_netEntity == null || _phage == null || !_netEntity.IsOwnedByMe) return;
		// 发送攻击实施包 0
		EnemySyncModule.Instance.SendCustomFeature(
			targetId, _netEntity.networkId, CustomIndex,
			// 攻击实施包 0, 模块缺失时不进行回复
			writer => { writer.Put((byte)0); },needReply: false);
	}

	/// <summary>
	/// 玩家收到所有者的 Phage攻击当前玩家
	/// </summary>
	private void HandleAttackHit(IDType senderId) {
		// 只有 Phage 当前所有者可以发起攻击命中事件
		if (_netEntity == null || _netEntity.CurrentOwnerId != senderId) return;
		ENT_Player player = ENT_Player.GetPlayer();
		if (player == null || player.IsDead()) return;
		// 保持原版 OnAttackHit 的检查顺序
		// 进入击杀判定
		if (player.HasPerk(_phage.perkAsset.id) && player.GetPerk(_phage.perkAsset.id).stackAmount >= 3) {
			// 通知 Phage 所有者, 玩家已经进入 Phage 击杀流程	
			EnemySyncModule.Instance.SendCustomFeature(
				senderId, _netEntity.networkId, CustomIndex,
				writer => { writer.Put((byte)1); }, needReply: false);
			// 本地玩家进入 Phage 击杀流程, 直接调用原版 OnKillPlayer 方法
			_onKillPlayer(_phage);
			return;
		}
		player.AddPerk(_phage.perkAsset, _phage.infectionAmount);
	}

	/// <summary>
	/// 所有者收到 Phage击杀玩家
	/// </summary>
	private void HandleVictimKillTriggered(IDType senderId) {
		// 只有 Phage 的所有者负责执行权威动画和定身行为
		if (_netEntity == null || _phage == null || !_netEntity.IsOwnedByMe) return;
		// 避免重复消息导致重复启动击杀协程
		if (_killAnimationStarted) return;
		_killAnimationStarted = true;
		// 保存原始状态
		float tempSpeedMult = _phage.speedMult;
		bool tempLocked = _phage.locked;
		// 击杀其他玩家协程
		_phage.StartCoroutine(KillOtherPlayerAnimation());
		IEnumerator KillOtherPlayerAnimation() {
			_phage.speedMult = 0f;
			_phage.locked = true;
			yield return new WaitForSeconds(1f);
			AnimatorInterceptor.OnSetTriggerStr(_phage.animator, "killplayer");
			yield return new WaitForSeconds(1.8f);
			_phage.speedMult = tempSpeedMult;
			_phage.locked = tempLocked;
			// 重置状态
			AnimatorInterceptor.OnPlayHash(_phage.animator, RESET_ANIMA_HASH, 0, 0f);
		}
	}

	#endregion

	/// <summary>
	/// 噬菌体命中玩家 进行 类RPC调用
	/// </summary>
	public static void PhageOnAttackHit(DEN_Phage instance, GameEntity hitEntity) {
		if (!EnemySyncModule.Instance.IsEnabled
			|| !instance.TryGetComponent<NetworkedGameEntity>(out var identity)
			|| !identity.IsOwnedByMe) return;
		IDType targetId;
		if (hitEntity is RemotePlayer remotePlayer) {
			targetId = remotePlayer.playerId;
		} else if (hitEntity is RemoteProp remoteProp) {
			targetId = remoteProp.remoteEntity.playerId;
		} else return;
		// 查找噬菌体模块, 发送攻击命中消息
		foreach (var feature in identity.CustomSyncFeatures) {
			if (feature is PhageSyncFeature phageFeature) {
				phageFeature.SendAttackHit(targetId);
				break;
			}
		}
	}
}
