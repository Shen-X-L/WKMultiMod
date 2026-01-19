using BepInEx.Configuration;
using System;
using System.Collections.Generic;
using System.Text;

namespace WKMPMod.Core;
public class MPConfig {
	// 数据发送频率 (每秒发送次数)
	private static ConfigEntry<int> _dataSendFrequency;
	public static int DataSendFrequency { get { return _dataSendFrequency.Value; } }

	// Debug日志语言类型
	private static ConfigEntry<int> _debugLogLanguage;
	public static int DebugLogLanguage { get { return _debugLogLanguage.Value; } }

	// 头顶名称标签字体最大值
	private static ConfigEntry<float> _nameTagSizeMax;
	// 头顶名称标签字体最小值
	private static ConfigEntry<float> _nameTagSizeMin;

	public static float NameTagSizeMax { get { return _nameTagSizeMax.Value; } }
	public static float NameTagSizeMin { get { return _nameTagSizeMin.Value; } }

	// All (所有伤害)
	private static ConfigEntry<float> _allActive;
	private static ConfigEntry<float> _allPassive;
	public static float AllActive { get { return _allActive.Value; } }
	public static float AllPassive { get { return _allPassive.Value; } }

	// Hammer (锤子)
	private static ConfigEntry<float> _hammerActive;
	private static ConfigEntry<float> _hammerPassive;
	public static float HammerActive {get { return _hammerActive.Value; }}
	public static float HammerPassive {get { return _hammerPassive.Value; }}

	// rebar (钢筋/骨矛)
	private static ConfigEntry<float> _rebarActive;
	private static ConfigEntry<float> _rebarPassive;
	public static float RebarActive { get { return _rebarActive.Value; } }
	public static float RebarPassive { get { return _rebarPassive.Value; } }

	// piton (自动钻头)
	private static ConfigEntry<float> _pitonActive;
	private static ConfigEntry<float> _pitonPassive;
	public static float PitonActive { get { return _pitonActive.Value; } }
	public static float PitonPassive { get { return _pitonPassive.Value; } }

	// flare (信号枪)
	private static ConfigEntry<float> _flareActive;
	private static ConfigEntry<float> _flarePassive;
	public static float FlareActive { get { return _flareActive.Value; } }
	public static float FlarePassive { get { return _flarePassive.Value; } }

	// returnrebar (神器长矛)
	private static ConfigEntry<float> _returnRebarActive;
	private static ConfigEntry<float> _returnRebarPassive;
	public static float ReturnRebarActive { get { return _returnRebarActive.Value; } }
	public static float ReturnRebarPassive { get { return _returnRebarPassive.Value; } }

	// rebarexplosion (爆炸钢筋)
	private static ConfigEntry<float> _rebarExplosionActive;
	private static ConfigEntry<float> _rebarExplosionPassive;
	public static float RebarExplosionActive { get { return _rebarExplosionActive.Value; } }
	public static float RebarExplosionPassive { get { return _rebarExplosionPassive.Value; } }

	// rebarexplosion (爆炸)
	private static ConfigEntry<float> _explosionActive;
	private static ConfigEntry<float> _explosionPassive;
	public static float ExplosionActive { get { return _explosionActive.Value; } }
	public static float ExplosionPassive { get { return _explosionPassive.Value; } }

	// ice (造冰枪-冰锥)
	private static ConfigEntry<float> _iceActive;
	private static ConfigEntry<float> _icePassive;
	public static float IceActive { get { return _iceActive.Value; } }
	public static float IcePassive { get { return _icePassive.Value; } }

	// other (其他伤害类型)
	private static ConfigEntry<float> _otherActive;
	public static ConfigEntry<float> _otherPassive;
	public static float OtherActive { get { return _otherActive.Value; } }
	public static float OtherPassive { get { return _otherPassive.Value; } }

	public static void Initialize(ConfigFile config) {
		config.Bind(
	"RemotePlayerPvP",
	"Damage Types Guide",
	"",
	@"
DAMAGE TYPE REFERENCE:
---------------------
Note: ×N means deals N instances of this damage type.

* Hammer - Type: Hammer, Damage: 1 (Hammer)
* Auto Piton - Type: piton, Damage: 3 (Auto Piton)
* Brick - Type: , Damage: 3 (Brick)
* Flare Gun - Type: flare, Damage: 6 (Flare Gun)
* Rebar/Bone Spears - Type: rebar, Damage: 10 (Rebar/Bone spears)
* Rope Rebar - Type: , Damage: 10 (Rope Rebar)
* Artifact Spear (throw/return) - Type: returnrebar, Damage: 10 (Artifact Spear)
* Explosive Rebar - Type: explosion, Damage: 10 - Type: rebarexplosion, Damage: 10 (Explosive Rebar)
* Cryo-Gun (uncharged/charged) - Type: ice, Damage: 10 - Type: , Damage: 0 × 2 (Cryo-Gun)

The Active configuration item controls the damage multiplier dealt by players.
The Passive configuration item controls the damage multiplier received by players.

Formula:
Final Damage = Base Damage × AllActive Multiplier × AllPassive Multiplier × Corresponding Type Active Multiplier × Corresponding Type Passive Multiplier

* 锤子 - 类型Hammer 伤害1
* 自动钻头 - 类型piton 伤害3
* 砖头 - 类型 伤害3
* 信号枪 - 类型flare 伤害6
* 钢筋/骨矛 - 类型rebar 伤害10
* 带绳钢筋 - 类型 伤害10
* 神器长矛(投出/返回) - 类型returnrebar 伤害10
* 爆炸钢筋 - 类型explosion 伤害10 - 类型rebarexplosion 伤害10 × 2
* 造冰枪(不蓄力/蓄力) - 类型ice 伤害10 - 类型 伤害 0 × 2

Active配置项控制玩家造成的伤害倍率
Passive配置项控制玩家受到的伤害倍率
公式 : 最终伤害 = 基础伤害 × AllActive倍率 × AllPassive倍率 × 对应类型Active倍率 × 对应类型Passive倍率
"
		);

		_dataSendFrequency = config.Bind<int>(
			"Network", "DataSendFrequency", 20,
			"Sets how many times per second data is sent to other players.\n" +
			"设置每秒向其他玩家发送数据的次数.");

		_debugLogLanguage = config.Bind<int>(
			"Debug", "LogLanguage", 1,
			"值为0时使用中文输出日志, Use English logs when the value is 1.");

		_nameTagSizeMax = config.Bind<float>(
			"RemotePlayer", "NameTagSizeMax", 0.5f,
			"This value sets the maximum size for player name tags above their heads.\n" +
			"这个值设置玩家头部名称最大的大小");

		_nameTagSizeMin = config.Bind<float>(
			"RemotePlayer", "NameTagSizeMin", 0.3f,
			"This value sets the minimum size for player name tags above their heads.\n" +
			"这个值设置玩家头部名称最小的大小");

		// All (所有伤害)
		_allActive = config.Bind<float>(
			"RemotePlayerPvP", "AllActive", 0.2f,
			"Multiplier for all damage dealt by the player.\n" +
			"玩家造成所有伤害类型的伤害倍率");
		_allPassive = config.Bind<float>(
			"RemotePlayerPvP", "AllPassive", 1.0f,
			"Multiplier for all damage received by the player.\n" +
			"玩家受到所有伤害类型的伤害倍率");

		// Hammer (锤子)
		_hammerActive = config.Bind<float>(
			"RemotePlayerPvP", "HammerActive", 5f,
			"Multiplier for hammer damage dealt by the player.\n" +
			"玩家可以使用锤子造成伤害的伤害倍率");
		_hammerPassive = config.Bind<float>(
			"RemotePlayerPvP", "HammerPassive", 1.0f,
			"Multiplier for hammer damage received by the player.\n" +
			"玩家受到锤子伤害的伤害倍率");

		// rebar (钢筋/骨矛)
		_rebarActive = config.Bind<float>(
			"RemotePlayerPvP", "RebarActive", 1.0f,
			"Multiplier for rebar damage dealt by the player.\n" +
			"玩家可以使用长矛类造成伤害的伤害倍率");
		_rebarPassive = config.Bind<float>(
			"RemotePlayerPvP", "RebarPassive", 1.0f,
			"Multiplier for rebar damage received by the player.\n" +
			"玩家受到长矛类伤害的伤害倍率");

		// piton (自动钻头)
		_pitonActive = config.Bind<float>(
			"RemotePlayerPvP", "PitonActive", 1.0f,
			"Multiplier for auto-piton damage dealt by the player.\n" +
			"玩家使用自动钻头造成伤害的伤害倍率");
		_pitonPassive = config.Bind<float>(
			"RemotePlayerPvP", "PitonPassive", 1.0f,
			"Multiplier for auto-piton damage received by the player.\n" +
			"玩家受到自动钻头伤害的伤害倍率");

		// flare (信号枪)
		_flareActive = config.Bind<float>(
			"RemotePlayerPvP", "FlareActive", 1.0f,
			"Multiplier for flare gun damage dealt by the player.\n" +
			"玩家使用信号枪造成伤害的伤害倍率");
		_flarePassive = config.Bind<float>(
			"RemotePlayerPvP", "FlarePassive", 1.0f,
			"Multiplier for flare gun damage received by the player.\n" +
			"玩家受到信号枪伤害的伤害倍率");

		// returnrebar (神器长矛)
		_returnRebarActive = config.Bind<float>(
			"RemotePlayerPvP", "ReturnRebarActive", 1.0f,
			"Multiplier for artifact spear (returnrebar) damage dealt by the player.\n" +
			"玩家使用神器长矛造成伤害的伤害倍率");
		_returnRebarPassive = config.Bind<float>(
			"RemotePlayerPvP", "ReturnRebarPassive", 1.0f,
			"Multiplier for artifact spear (returnrebar) damage received by the player.\n" +
			"玩家受到神器长矛伤害的伤害倍率");

		// rebarexplosion (爆炸钢筋)
		_rebarExplosionActive = config.Bind<float>(
			"RemotePlayerPvP", "RebarExplosionActive", 1.0f,
			"Multiplier for explosion shrapnel (rebarexplosion) damage dealt by the player.\n" +
			"玩家造成爆炸钢筋伤害的伤害倍率");
		_rebarExplosionPassive = config.Bind<float>(
			"RemotePlayerPvP", "RebarExplosionPassive", 1.0f,
			"Multiplier for explosion shrapnel (rebarexplosion) damage received by the player.\n" +
			"玩家受到爆炸钢筋伤害的伤害倍率");

		// explosion (爆炸)
		_explosionActive = config.Bind<float>(
			"RemotePlayerPvP", "ExplosionActive", 1.0f,
			"Multiplier for explosion shrapnel (explosion) damage dealt by the player.\n" +
			"玩家造成爆炸溅射伤害的伤害倍率");
		_explosionPassive = config.Bind<float>(
			"RemotePlayerPvP", "ExplosionPassive", 1.0f,
			"Multiplier for explosion shrapnel (explosion) damage received by the player.\n" +
			"玩家受到爆炸溅射伤害的伤害倍率");

		// ice (造冰枪-冰锥)
		_iceActive = config.Bind<float>(
			"RemotePlayerPvP", "IceActive", 1.0f,
			"Multiplier for cryo-gun ice spike damage dealt by the player.\n" +
			"玩家使用造冰枪冰锥造成伤害的伤害倍率");
		_icePassive = config.Bind<float>(
			"RemotePlayerPvP", "IcePassive", 1.0f,
			"Multiplier for cryo-gun ice spike damage received by the player.\n" +
			"玩家受到造冰枪冰锥伤害的伤害倍率");

		// other (其他伤害类型)
		_otherActive = config.Bind<float>(
			"RemotePlayerPvP", "OtherActive", 1.0f,
			"Multiplier for other damage dealt by the player.\n" +
			"玩家造成其他伤害类型的伤害倍率");
		_otherPassive = config.Bind<float>(
			"RemotePlayerPvP", "OtherPassive", 1.0f,
			"Multiplier for other damage received by the player.\n" +
			"玩家受到其他伤害类型的伤害倍率");
	}
}

