using BepInEx.Configuration;
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using WKMPMod.Component;

namespace WKMPMod.Core;

public class MPConfig {
	// 配置版本号
	private const string CURRENT_CONFIG_VERSION = "2.0";

	// 配置版本号 (在大更新时对其配置用)
	private static ConfigEntry<string> _configVersion;

	// 数据发送频率 (每秒发送次数)
	private static ConfigEntry<int> _dataSendFrequency;
	public static int DataSendFrequency { get { return _dataSendFrequency.Value; } }

	private static ConfigEntry<string> _toggleKey;
	public static string ToggleKey => _toggleKey.Value;

	#region[自定义相关]

	// 头顶名称标签字体最大值
	private static ConfigEntry<float> _nameTagScale;
	public static float NameTagScale { get { return _nameTagScale.Value; } }

	// 远程玩家模型 (默认值为 "default", 可以设置为 "slugcat" 来使用蛞蝓猫模型)
	private static ConfigEntry<string> _remotePlayerModel;
	public static string RemotePlayerModel {
		get { return _remotePlayerModel.Value; }
		set { _remotePlayerModel.Value = value; }
	}

	// 远程玩家替代名称
	private static ConfigEntry<string> _remotePlayerName;
	public static string RemotePlayerName {
		get { return _remotePlayerName.Value; }
		set { _remotePlayerName.Value = value; }
	}

	// 远程玩家模型颜色 (RGB 0-255)
	private static ConfigEntry<int> _remotePlayerColorR;
	private static ConfigEntry<int> _remotePlayerColorG;
	private static ConfigEntry<int> _remotePlayerColorB;
	public static Color32 RemotePlayerColor {
		get {
			return new(
			(byte)Mathf.Clamp(_remotePlayerColorR.Value, 0, 255),
			(byte)Mathf.Clamp(_remotePlayerColorG.Value, 0, 255),
			(byte)Mathf.Clamp(_remotePlayerColorB.Value, 0, 255),
			255);
		}
		set {
			_remotePlayerColorR.Value = value.r;
			_remotePlayerColorG.Value = value.g;
			_remotePlayerColorB.Value = value.b;
		}
	}


	#endregion
	#region[PVP相关]

	// All (所有伤害)
	private static ConfigEntry<float> _allActive;
	public static float AllActive { get { return _allActive.Value; } }

	// Hammer (锤子)
	private static ConfigEntry<float> _hammerActive;
	public static float HammerActive { get { return _hammerActive.Value; } }

	// Melee (近战)
	private static ConfigEntry<float> _meleeActive;
	public static float MeleeActive { get { return _meleeActive.Value; } }

	// rebar (钢筋/骨矛)
	private static ConfigEntry<float> _rebarActive;
	public static float RebarActive { get { return _rebarActive.Value; } }

	// piton (自动钻头)
	private static ConfigEntry<float> _pitonActive;
	public static float PitonActive { get { return _pitonActive.Value; } }

	// flare (信号枪)
	private static ConfigEntry<float> _flareActive;
	public static float FlareActive { get { return _flareActive.Value; } }

	// returnrebar (神器长矛)
	private static ConfigEntry<float> _returnRebarActive;
	public static float ReturnRebarActive { get { return _returnRebarActive.Value; } }

	// rebarexplosion (爆炸钢筋)
	private static ConfigEntry<float> _rebarExplosionActive;
	public static float RebarExplosionActive { get { return _rebarExplosionActive.Value; } }

	// rebarexplosion (爆炸)
	private static ConfigEntry<float> _explosionActive;
	public static float ExplosionActive { get { return _explosionActive.Value; } }

	// ice (造冰枪-冰锥)
	private static ConfigEntry<float> _iceActive;
	public static float IceActive { get { return _iceActive.Value; } }

	// other (其他伤害类型 信号枪灼烧除外)
	private static ConfigEntry<float> _otherActive;
	public static float OtherActive { get { return _otherActive.Value; } }
	// 信号枪灼烧持续时间倍率
	private static ConfigEntry<float> _fireTimeMult;
	public static float FireTimeMult { get { return _fireTimeMult.Value; } }
	// 信号枪灼烧伤害倍率
	private static ConfigEntry<float> _fireDamageMult;
	public static float FireDamageMult { get { return _fireDamageMult.Value; } }

	// 爆发伤害窗口期
	private static ConfigEntry<float> _burstWindow;
	public static float BurstWindow { get { return _burstWindow.Value; } }

	// 无敌时间
	private static ConfigEntry<float> _invincibilityTime;
	public static float InvincibilityTime { get { return _invincibilityTime.Value; } }

	#endregion
	#region[房间规则控制]

	private static ConfigEntry<bool> _allowCheats;
	private static ConfigEntry<bool> _allowPVP;

	public static bool AllowCheats {
		get { return _allowCheats.Value; }
		set { _allowCheats.Value = value; }
	}
	public static bool AllowPVP {
		get { return _allowPVP.Value; }
		set { _allowPVP.Value = value; }
	}

	private static ConfigEntry<bool> _bindsync;
	public static bool BindSync {
		get { return _bindsync.Value; }
		set { _bindsync.Value = value; }
	}

	#endregion

	/// <summary>
	/// 初始化配置文件
	/// </summary>
	public static void Initialize(ConfigFile config) {

		// 判断是否是旧版本
		bool isConfigOutdated = !config.ContainsKey(new ConfigDefinition("Internal", "Version"));

		_configVersion = config.Bind("Internal", "Version", CURRENT_CONFIG_VERSION, "Internal version tracking. Do not modify.内部版本跟踪 别改哦");

		isConfigOutdated |= new Version(_configVersion.Value) < new Version((string)_configVersion.DefaultValue);

		_dataSendFrequency = config.Bind<int>(
			"Network", "DataSendFrequency", 20,
			"Sets how many times per second data is sent to other players.\n" +
			"设置每秒向其他玩家发送数据的次数.");

		#region[自定义相关]

		_nameTagScale = config.Bind<float>(
			"RemotePlayer", "NameTagScale", 1f,
			"This value sets the scale size for player name tags above their heads.\n" +
			"这个值设置玩家头部名称的缩放倍率");

		_remotePlayerModel = config.Bind<string>(
			"RemotePlayer", "Model", "default",
			"Sets the model used for remote players. Default is 'default', you can set it to 'slugcat' to use the slugcat model.\n" +
			"设置远程玩家使用的模型,默认值为'default',你可以设置为'slugcat'来使用蛞蝓猫模型.");

		_remotePlayerName = config.Bind<string>(
			"RemotePlayer", "Name", "",
			"Sets a custom name to display for remote players. Leave empty to use their Steam name.\n" +
			"设置一个自定义名称来显示远程玩家,留空则使用他们的Steam名称");

		_remotePlayerColorR = config.Bind<int>(
			"RemotePlayer", "ColorR", 255,
			"The red channel (0-255) used for your remote player model color.\n" +
			"玩家模型颜色红色通道(0-255).");

		_remotePlayerColorG = config.Bind<int>(
			"RemotePlayer", "ColorG", 220,
			"The green channel (0-255) used for your remote player model color.\n" +
			"玩家模型颜色绿色通道(0-255).");

		_remotePlayerColorB = config.Bind<int>(
			"RemotePlayer", "ColorB", 64,
			"The blue channel (0-255) used for your remote player model color.\n" +
			"玩家模型颜色蓝色通道(0-255).");

		#endregion
		#region[PVP相关]

		config.Bind(
			"RemotePlayerPvP",
			"Damage Types Guide",
			"",
@"
DAMAGE TYPE REFERENCE:
---------------------
Note: ×N means deals N instances of this damage type.

* Hammer - Type:Melee Tags:Melee blunt hammer Damage:1-3
* Auto Piton - Type:piton Damage:3
* Brick - Damage:3
* Flare Gun - Type:flare Tags:flare incendiary-long Damage:4
* Flare Gun Burning - Type:fire Tags:fire Damage:0.5 per 0.5s for 5s (reduced by modifying fireTimeMult and fireDamageMult)
* Rebar/Bone Spears - Type:rebar Damage:10
* Rope Rebar - Damage:10
* Artifact Spear (throw/return) - Type:returnrebar Tags:returnrebar Damage:10
* Explosive Rebar - Type:explosion Tags:explosion Damage:10 - Type:rebarexplosion Tags:rebarexplosion explosion explosive Damage:10 × 3
* Cryo-Gun (uncharged/charged) - Type:ice Tags:ice Damage:10 - Tags:explosion explosive Damage: 0 × 3

The Active configuration item controls the damage multiplier dealt by players.

Formula (Excluding Flare Gun Burning): 
Final Damage = Base Damage × AllActive Multiplier × Corresponding Type Active Multiplier

* 锤子		类型:Melee	标签:Melee blunt	hammer	伤害1-3
* 自动钻头	类型:piton	伤害3
* 砖头		类型			伤害3
* 信号枪	类型:flare	标签:flare incendiary-long	伤害4	
* 信号枪灼烧	类型:fire	标签:fire	伤害0.5 每0.5秒造成一次 共持续5秒	(通过修改fireTimeMult和fireDamageMult降低)
* 钢筋/骨矛		类型:rebar	伤害10
* 带绳钢筋		类型			伤害10
* 神器长矛(投出/返回)	类型:returnrebar		标签:returnrebar		伤害10
* 爆炸钢筋	类型:explosion		标签:explosion	伤害10
			类型:rebarexplosion	标签:rebarexplosion explosion explosive	伤害10 × 3
* 造冰枪(不蓄力/蓄力)	类型:ice		标签:ice			伤害10
					类型			标签:explosion explosive	伤害 0 × 3

Active配置项控制玩家造成的伤害倍率 (信号枪灼烧除外)
公式 : 最终伤害 = 基础伤害 × AllActive倍率 × 对应类型Active倍率
"
			);

		// All (所有伤害)
		_allActive = config.Bind<float>(
			"RemotePlayerPvP", "AllActive", 0.1f,
			"Multiplier for all damage dealt by the player.\n" +
			"玩家造成所有伤害类型的伤害倍率");

		// Hammer (锤子)
		_hammerActive = config.Bind<float>(
			"RemotePlayerPvP", "HammerActive", 5.0f,
			"Multiplier for hammer damage dealt by the player.\n" +
			"玩家可以使用锤子造成伤害的伤害倍率");

		// Melee (近战)
		_meleeActive = config.Bind<float>(
			"RemotePlayerPvP", "MeleeActive", 5.0f,
			"Multiplier for melee damage dealt by the player.\n" +
			"玩家可以使用近战造成伤害的伤害倍率");

		// rebar (钢筋/骨矛)
		_rebarActive = config.Bind<float>(
			"RemotePlayerPvP", "RebarActive", 1.0f,
			"Multiplier for rebar damage dealt by the player.\n" +
			"玩家可以使用长矛类造成伤害的伤害倍率");

		// piton (自动钻头)
		_pitonActive = config.Bind<float>(
			"RemotePlayerPvP", "PitonActive", 7.0f,
			"Multiplier for auto-piton damage dealt by the player.\n" +
			"玩家使用自动钻头造成伤害的伤害倍率");

		// flare (信号枪)
		_flareActive = config.Bind<float>(
			"RemotePlayerPvP", "FlareActive", 0f,
			"Multiplier for flare gun damage dealt by the player.\n" +
			"玩家使用信号枪造成伤害的伤害倍率");

		// returnrebar (神器长矛)
		_returnRebarActive = config.Bind<float>(
			"RemotePlayerPvP", "ReturnRebarActive", 1.0f,
			"Multiplier for artifact spear (returnrebar) damage dealt by the player.\n" +
			"玩家使用神器长矛造成伤害的伤害倍率");

		// rebarexplosion (爆炸钢筋)
		_rebarExplosionActive = config.Bind<float>(
			"RemotePlayerPvP", "RebarExplosionActive", 1.0f,
			"Multiplier for explosion shrapnel (rebarexplosion) damage dealt by the player.\n" +
			"玩家造成爆炸钢筋伤害的伤害倍率");

		// explosion (爆炸)
		_explosionActive = config.Bind<float>(
			"RemotePlayerPvP", "ExplosionActive", 1.0f,
			"Multiplier for explosion shrapnel (explosion) damage dealt by the player.\n" +
			"玩家造成爆炸溅射伤害的伤害倍率");

		// ice (造冰枪-冰锥)
		_iceActive = config.Bind<float>(
			"RemotePlayerPvP", "IceActive", 1.0f,
			"Multiplier for cryo-gun ice spike damage dealt by the player.\n" +
			"玩家使用造冰枪冰锥造成伤害的伤害倍率");

		// other (其他伤害类型)
		_otherActive = config.Bind<float>(
			"RemotePlayerPvP", "OtherActive", 1.0f,
			"Multiplier for other damage dealt by the player.\n" +
			"玩家造成其他伤害类型的伤害倍率");

		// flare burning time (信号枪灼烧时间倍率)
		_fireTimeMult = config.Bind<float>(
			"RemotePlayerPvP", "FireTimeMult", 1.0f,
			"Multiplier for the duration of flare gun burning effect.\n" +
			"信号枪灼烧效果持续时间的倍率");

		// flare burning damage (信号枪灼烧伤害倍率)
		_fireDamageMult = config.Bind<float>(
			"RemotePlayerPvP", "FireDamageMult", 0.6f,
			"Multiplier for the damage of flare gun burning effect.\n" +
			"信号枪灼烧效果伤害的倍率");

		_burstWindow = config.Bind<float>(
			"RemotePlayerPvP", "BurstWindow", 0.05f, // 默认 50毫秒
			"Damage window (in seconds) to allow multiple hits like shotgun pellets before invincibility starts.\n" +
			"允许霰弹枪等多段伤害同时生效的并发窗口期(秒)");

		_invincibilityTime = config.Bind<float>(
			"RemotePlayerPvP", "InvincibilityTime", 0.3f, // 默认 0.3秒
			"Duration (in seconds) of invincibility after the burst window ends.\n" +
			"玩家受到伤害后的无敌时间(秒)");

		#endregion
		#region[房间规则控制]

		_allowCheats = config.Bind<bool>(
			"LobbyRule", "cheats", false,
			"Controls whether cheats are allowed by LobbyRule in lobby you host.\n" +
			"控制由你开启的房间是否默认可以使用cheats"
			);
		_allowPVP = config.Bind<bool>(
			"LobbyRule", "PVP", false,
			"Controls whether PvP is enabled by LobbyRule in lobby you host.\n" +
			"控制由你开启的房间是否默认可以PVP"
			);
		_bindsync = config.Bind<bool>(
			"LobbyRule", "bindsync", false,
			"Controls whether bindings or trinkets synchronization is enabled by LobbyRule in lobby you host.\n" +
			"控制由你开启的房间是否默认可以绑定或天赋同步");
		#endregion

		_toggleKey = config.Bind("Controls", "ToggleKey", "g", "按下此键切换抓取/拖拽状态");

		if (isConfigOutdated)
			VersionDetection(config);
	}

	/// <summary>
	/// 进行配置文件版本检查
	/// </summary>
	public static void VersionDetection(ConfigFile config) {
		// 执行重置逻辑
		_allActive.Value = (float)_allActive.DefaultValue;
		_hammerActive.Value = (float)_hammerActive.DefaultValue;
		_meleeActive.Value = (float)_meleeActive.DefaultValue;
		_rebarActive.Value = (float)_rebarActive.DefaultValue;
		_returnRebarActive.Value = (float)_returnRebarActive.DefaultValue;
		_rebarExplosionActive.Value = (float)_rebarExplosionActive.DefaultValue;
		_explosionActive.Value = (float)_explosionActive.DefaultValue;
		_pitonActive.Value = (float)_pitonActive.DefaultValue;
		_flareActive.Value = (float)_flareActive.DefaultValue;
		_iceActive.Value = (float)_iceActive.DefaultValue;
		_otherActive.Value = (float)_otherActive.DefaultValue;
		_fireTimeMult.Value = (float)_fireTimeMult.DefaultValue;
		_fireDamageMult.Value = (float)_fireDamageMult.DefaultValue;
		// 将磁盘上的版本号更新到最新
		_configVersion.Value = (string)_configVersion.DefaultValue;
		_burstWindow.Value = (float)_burstWindow.DefaultValue;
		_invincibilityTime.Value = (float)_invincibilityTime.DefaultValue;

		config.Save();
	}

	public static DamageRules DamageRules {
		get {
			return new DamageRules {
				All = AllActive,
				Hammer = HammerActive,
				Melee = MeleeActive,
				Rebar = RebarActive,
				ReturnRebar = ReturnRebarActive,
				RebarExplosion = RebarExplosionActive,
				Explosion = ExplosionActive,
				Piton = PitonActive,
				Flare = FlareActive,
				Ice = IceActive,
				Other = OtherActive,
				FireTime = FireTimeMult,
				FireDamage = FireDamageMult,
				BurstWindow = BurstWindow,
				InvincibilityTime = InvincibilityTime
			};
		}
	}

}

