using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine.UIElements;

namespace WKMPMod.Data;

public static partial class MPKeys {
	// 游戏标识, 用于搜索游戏大厅
	public const string GAME_KEY = "game";
	public const string GAME_VALUE = "White Knuckle";
	// 名称描述用途
	public const string VERSION = "version";
	public const string MOD_VERSION = "mod version";

	public const string OWNER_NAME = "owner";

	public const string LOBBY_VISIBILITY = "visibility";
	public const string LOBBY_NAME = "name";

	public const string GAMEMODE_JSON = "gamemode";
	public const string BIND_SYNC = "bind sync";

	public const string ALLOW_CHEATS = "allowCheats";
	public const string ALLOW_PVP = "allowPVP";
	public const string DAMAGE_CONFIG = "damage config";

	public const string ALL_KEYS_INDEX = "__all_keys__";
	public const string MODEL_ID = "model id";
}
