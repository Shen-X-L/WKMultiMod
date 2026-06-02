using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine.UIElements;

namespace WKMPMod.Data;

public static partial class MPKeys {
	// 游戏标识, 用于搜索游戏大厅
	public const string GAME_KEY = "game";
	public const string GAME_VALUE = "White Knuckle";
	// 游戏本体常量
	public const string GRAB_TAGGER = "Pickupable";
	public const string HANGING_TAGGER = "Handhold";
	// 名称描述用途
	// 大厅数据键
	public const string VERSION = "version";
	public const string MOD_VERSION = "mod version";

	public const string OWNER_NAME = "owner";

	public const string LOBBY_VISIBILITY = "visibility";
	public const string LOBBY_NAME = "name";

	public const string GAMEMODE_JSON = "gamemode";
	public const string BIND_SYNC = "bind sync";

	public const string ALLOW_CHEATS = "allowCheats";
	public const string DAMAGE_CONFIG = "damage config";

	public const string ACTIVE_TEAMS = "active teams";

	// 玩家个人数据键
	public const string ALL_KEYS_INDEX = "__all_keys__";
	public const string PREFAB_ID = "prefab id";
	public const string PLAYER_NAME = "player name";
	public const string JOIN_MESSAGE = "join message";
	public const string LEAVE_MESSAGE = "leave message";
	public const string TEAM = "team";
	public const string DEFAULT_TEAM = "default";

	// 文件名
	public const string TEAM_RULES_FILE = "WKMP_TeamRules.json";
}
