using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using WKMPMod.Asset;
using WKMPMod.Core;
using WKMPMod.Data;
using WKMPMod.Util;

namespace WKMPMod.Components;

public class RemotePlayer : GameEntity {
	// 玩家ID,用于识别玩家
	public IDType playerId;
	// 是否启用PVP伤害判定, 可通过玩家数据更新
	public bool pvpEnabled = false;

	#region [位移与旋转同步]

	public float teleportThreshold = 50f;   // 当前位置与目标位置超过此距离时直接瞬移
	public float maxSmoothDistance = 10f;   // 超过此距离使用更快的平滑

	private bool _isTeleporting = false;    // 是否进行了传送
	private Vector3 _targetPosition;        // 目标位置
	private Vector3 _velocity = Vector3.zero;   // 当前速度,用于平滑插值

	// 每帧更新位置
	protected void LateUpdate() {
		// 如果是传送状态,不进行平滑移动
		if (_isTeleporting) return;
		// 检查当前位置与目标位置的距离
		float distance = Vector3.Distance(transform.position, _targetPosition);
		// 如果距离超过阈值,直接瞬移
		if (distance > teleportThreshold) {
			Teleport(_targetPosition);
			return;
		}

		if (transform.position != _targetPosition) {
			// 计算平滑时间
			float smoothTime = CalculateSmoothTime(distance);

			transform.position = Vector3.SmoothDamp(
				transform.position, // 当前位置
				_targetPosition,    // 目标位置
				ref _velocity,      // 速度引用
				smoothTime,         // 平滑时间
				float.MaxValue,     // 最大速度
				Time.deltaTime      // 时间增量
			);

			// 速度 < 0.5 && 距离 > 0.05时 强制最低速度 0.5格/秒
			if (_velocity.magnitude < 0.5f && distance > 0.05f) {
				Vector3 direction = (_targetPosition - transform.position).normalized;
				_velocity = direction * 0.5f;
			}
		}
	}

	// 根据距离计算平滑时间
	private float CalculateSmoothTime(float distance) {
		// 如果距离很远,使用更快的平滑
		if (distance > maxSmoothDistance) {
			// 使用对数曲线
			return Mathf.Clamp(Mathf.Log(distance) * 0.1f, 0.1f, 0.3f);
		}
		return Mathf.Clamp(distance / maxSmoothDistance, 0.05f, 0.1f);
	}

	// 从PlayerData更新手位置(Container调用这个方法)
	public void UpdateFromPlayerData(Vector3 position, Quaternion rotation) {
		_isTeleporting = false;
		_targetPosition = position;
		transform.rotation = rotation;
	}

	public void UpdateFromPlayerData(ref PlayerData playerData) {
		_isTeleporting = false;
		_targetPosition = playerData.Position;
		transform.rotation = playerData.Rotation;
	}

	// 统一重写 GameEntity 的 Teleport，彻底避免位移与平滑算法冲突
	public override void Teleport(Vector3 pos) {
		TeleportInternal(pos, null);
	}

	public override void Teleport(Vector3 position, Quaternion rotation, bool keepVelocity = false) {
		TeleportInternal(position, rotation);
	}

	// 立即传送
	private void TeleportInternal(Vector3 position, Quaternion? rotation, bool keepVelocity = false) {
		// 标记为传送状态,避免平滑插值
		_isTeleporting = true;
		transform.position = position;
		_targetPosition = position;
		// 重置速度
		if (!keepVelocity) _velocity = Vector3.zero;
		// 如果提供了旋转,则设置旋转
		if (rotation.HasValue) transform.rotation = rotation.Value;
		// 传送完成后重置状态(延迟一帧确保不会立即开始平滑)
		StartCoroutine(ResetTeleportFlag());

		IEnumerator ResetTeleportFlag() {
			yield return null;
			_isTeleporting = false;
		}
	}

	#endregion

	#region [战斗与伤害判定 (GameEntity 重写)]

	// 负责处理 无敌帧 的重置计时器
	private TickTimer _invincibilityTimer = new TickTimer(0.5f);
	// 负责记录每次 无敌帧并发窗口 的起始物理时间
	private float _burstStartTime = -999f;

	// 对方受到伤害时调用
	public override bool Damage(Damageable.DamageInfo info) {
		// 关闭pvp || 伤害来源非同步因素伤害
		if (!pvpEnabled) return false;

		MPMain.LogTest($"DamageInfo: {info.amount} type: {info.type} source: {info.sourceEntity?.name?? "Unknown"}");

		if (!DamageRules.whitelistDamage.Contains(info.type)) return false;

		// 如果对方正在抓着我, 强制对方放手
		if (LocalPlayer.IsHoldingMe(playerId))
			MPEventBusGame.NotifyPlayerStopInteraction(playerId);

		if (info.amount <= 0) return false;

		_invincibilityTimer.SetInterval(MPCore.damageRules.InvincibilityTime);

		// 如果无敌时间已到 (大于 b), 开启新一轮的伤害判定窗口
		if (_invincibilityTimer.IsTickReached) {
			_invincibilityTimer.Reset();   // 重置 TickTimer
			_burstStartTime = Time.time;   // 记录本轮第一发子弹打中的时间
		} else if (Time.time - _burstStartTime <= MPCore.damageRules.BurstWindow) {
			// 如果还在无敌倒计时内, 但时间处于并发窗口期 (小于 a) 允许伤害通过
		} else {
			return false; // 处于 (a, b) 之间, 属于无敌帧 免疫伤害
		}

		// 添加屏幕震动
		CL_CameraControl.Shake(0.01f);
		// 计算伤害倍率
		CalculatedDamage(info);
		// 发送伤害通知事件
		MPEventBusGame.NotifyPlayerDamage(playerId, info);

		// 会不会死由对方决定
		return false;
	}

	public override void Kill(string type = "", Damageable.DamageInfo damageInfo = null) { }

	// 添加力(基础实现)
	public override void AddForce(Vector3 v, string source = "") {
		// 关闭pvp || 不是玩家伤害帧生成的力
		if (!pvpEnabled || Time.time - _burstStartTime > 0.1f) return;
		// 发送冲击力通知事件
		MPEventBusGame.NotifyPlayerAddForce(playerId, v / 10, source);
	}

	// 在指定位置添加力
	public override void AddForceAtPosition(Vector3 v, Vector3 p, string source = "") {
		AddForce(v, source);
	}

	public override void TonguePull(Vector3 v) { }

	#endregion

	#region[Unity生命周期函数]

	public override void Start() {
		base.Start();
	}

	#endregion

	#region[工具函数]

	/// <summary>
	/// 应用伤害倍率
	/// </summary>
	public static void CalculatedDamage(Damageable.DamageInfo info) {
		var baseDamage = info.amount * MPCore.damageRules.All;
		info.amount = info.type switch {
			"Melee" => baseDamage * MPCore.damageRules.Melee,
			"rebar" => baseDamage * MPCore.damageRules.Rebar,
			"returnrebar" => baseDamage * MPCore.damageRules.ReturnRebar,
			"rebarexplosion" => baseDamage * MPCore.damageRules.RebarExplosion,
			"explosion" => baseDamage * MPCore.damageRules.Explosion,
			"piton" => baseDamage * MPCore.damageRules.Piton,
			"flare" => baseDamage * MPCore.damageRules.Flare,
			"ice" => baseDamage * MPCore.damageRules.Ice,
			"bullet" => baseDamage * MPCore.damageRules.Bullet,
			_ => baseDamage * MPCore.damageRules.Other
		};
		return;
	}

	#endregion
}

/*
锤子		类型:Melee	标签:Melee blunt	 hammer	伤害1-3
自动钻头	类型:piton		伤害3
砖头		类型:			伤害3
信号枪	类型:flare	标签:flare incendiary-long	伤害4
钢筋/骨矛		类型:rebar	伤害10
带绳钢筋		类型:		伤害10
神器长矛(投出/返回)	类型:returnrebar		标签:returnrebar		伤害10
爆炸钢筋		类型:explosion		标签:explosion	伤害10
			类型:rebarexplosion	标签:rebarexplosion explosion explosive	伤害10 × 3
爆炸钢筋(自伤)	类型:rebarexplosion	标签:rebarexplosion explosion explosive	伤害1
造冰枪(不蓄力/蓄力)	类型:ice		标签:ice			伤害10
					类型:		标签:explosion explosive	伤害 0 × 3
造冰枪(自伤)			类型:		标签:explosion explosive	伤害 0
 */
[Serializable]
public class DamageRules {
	public float All;
	public float Melee;
	public float Rebar;
	public float ReturnRebar;
	public float RebarExplosion;
	public float Explosion;
	public float Piton;
	public float Flare;
	public float Ice;
	public float Bullet;
	public float Other;
	public float FireTime;
	public float FireDamage;
	public float BurstWindow;// 并发伤害允许的窗口期 (秒)
	public float InvincibilityTime;// 受伤后的完整无敌时间 (秒)

	// 玩家可以传递的伤害类型
	public static readonly HashSet<string> whitelistDamage = new HashSet<string>{
		"Melee","piton","flare","rebar","returnrebar","explosion","rebarexplosion","ice","bullet",
		"denizen","bloodbug","bloodbug-swarmer","bloodbug-spitter","barnacle",
		"sprider","aunt-spike","aunt","ravelin","gasbag"
	};

	// 字段名集合
	public static HashSet<string> FloatFieldNames { get; }

	// 字段缓存
	private static readonly Dictionary<string, FieldInfo> _floatFields;

	// 属性: 当前值字典 (每次调用创建新字典, 但遍历开销最小)
	[Newtonsoft.Json.JsonIgnore]
	public Dictionary<string, float> FloatFieldValues {
		get {
			var result = new Dictionary<string, float>(_floatFields.Count,
				StringComparer.OrdinalIgnoreCase);
			foreach (var (fieldName, fieldInfo) in _floatFields) {
				result[fieldName] = (float)fieldInfo.GetValue(this);
			}
			return result;
		}
	}

	// 静态构造函数 反射public float类型字段名
	static DamageRules() {
		// 一次性获取所有 public float 实例字段
		var fields = typeof(DamageRules)
			.GetFields(BindingFlags.Public | BindingFlags.Instance)
			.Where(f => f.FieldType == typeof(float))
			.ToArray();

		// 缓存为字典 (用于 SetField)
		_floatFields = new Dictionary<string, FieldInfo>(fields.Length, StringComparer.OrdinalIgnoreCase);
		foreach (var f in fields) _floatFields[f.Name] = f;

		// 字段名集合 (用于外部查询)
		FloatFieldNames = new HashSet<string>(
			fields.Select(f => f.Name.ToLower()), StringComparer.OrdinalIgnoreCase);
	}

	/// <summary>
	/// 根据字段名设置 float 值(忽略大小写)
	/// </summary>
	public bool SetField(string fieldName, float value) {
		if (string.IsNullOrEmpty(fieldName))
			return false;

		if (_floatFields.TryGetValue(fieldName, out var field)) {
			field.SetValue(this, value);
			return true;
		}
		return false;
	}
}