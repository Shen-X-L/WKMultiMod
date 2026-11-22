using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Events;

namespace WKMPMod.MK_Component;

public class MK_ENT_Hitbox : MonoBehaviour {
	public MK_RemotePlayer entity;
	// 传递给RemoteEntity伤害信息的额外标签列表
	public List<string> passTags;       
	// 提前进行一次倍率计算
	public List<DamageEffectData> damageEffects;
}
[Serializable]
public class DamageEffectData {
	public string id;                       // 效果唯一标识
	public List<string> requiredTags;       // 触发此效果所需的伤害标签
	public float damageMultiplier = 1f;     // 伤害倍率(<1为抗性, >1为弱点)
	public UnityEvent damageEvent;			// 受到此类型伤害时触发的事件
}