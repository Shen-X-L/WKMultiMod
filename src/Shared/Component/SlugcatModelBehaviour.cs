using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using WKMultiPlayerMod.Shared.Data;

namespace WKMultiPlayerMod.Shared.Component;

public class SlugcatModelBehaviour : CustomModelBehaviour {

	#region[物品坐标常量]

	public override Dictionary<string, ItemPoseData> HandItemTransform => _handItemTransform;

	private static Dictionary<string, ItemPoseData> _handItemTransform;

	static SlugcatModelBehaviour() {
		_handItemTransform = new(){
		{ "None",new ItemPoseData(Vector3.zero,Quaternion.identity,Vector3.one,HAND_ROT)},
		{ "Item_Hammer",HAMMER_TRANSFROM},
		{ "Item_BanHammer",HAMMER_TRANSFROM},
		{ "Item_Pipewrench", HAMMER_TRANSFROM},
		{ "Item_Hammer_Cosmetic_Wrench", HAMMER_TRANSFROM},

		{ "Item_BarnacleHook",BARNACLE_HOOK_TRANSFROM},
		{ "Item_BarnacleHook_Infinite",BARNACLE_HOOK_TRANSFROM},
		{ "Item_Handgun", HANDGUN_TRANSFROM },
		{ "Item_Handgun_Debug", HANDGUN_TRANSFROM },
		{ "Item_10mm_Ammo", new ItemPoseData(new Vector3(0,0.3f,0), Quaternion.Euler(270, 270, 0), Vector3.one, HAND_ROT) },

		{ "Item_Injector", new ItemPoseData(new Vector3(0, -0.1f, 0), Quaternion.Euler(270, 90, 0), Vector3.one * 1.3f, HAND_ROT) },
		{ "Item_Pillbottle", new ItemPoseData(new Vector3(0, 0.2f, 0), Quaternion.Euler(270, 0, 0), Vector3.one, HAND_ROT) },

		{ "Item_Food_Fruit", new ItemPoseData(new Vector3(0.2f, 0f, 0f), Quaternion.Euler(0, 90, 270), Vector3.one, HAND_ROT) },
		{ "Item_Food_Meat", new ItemPoseData(Vector3.zero, Quaternion.Euler(0, 90, 270), Vector3.one, HAND_ROT) },
		{ "Item_Wine",BOXED_FOOD_TRANSFROM},
		{ "Item_Wine_Empty",BOXED_FOOD_TRANSFROM},
		{ "Item_Milk",BOXED_FOOD_TRANSFROM},
		{ "Item_Milk_Empty",BOXED_FOOD_TRANSFROM},
		{ "Item_Milk_Rho",BOXED_FOOD_TRANSFROM},

		{ "Item_Beans",BEANS_TRANSFROM},
		{ "Item_Beans_Eaten",BEANS_TRANSFROM},
		{ "Item_Beans_Periphery",BEANS_TRANSFROM },

		{ "Item_Food_Bar",FOOD_TRANSFROM},
		{ "Item_Food_Cookie",FOOD_TRANSFROM},

		{ "Item_Cocoa_Full",COCOA_TRANSFROM},
		{ "Item_Cocoa_Empty",COCOA_TRANSFROM},

		{ "Item_Rebar_Explosive",REBAR_TRANSFROM},
		{ "Item_RebarRope",REBAR_TRANSFROM},
		{ "Item_RebarRope_Holiday",REBAR_TRANSFROM},
		{ "Item_Rebar",REBAR_TRANSFROM},
		{ "Item_Rebar_Holiday",REBAR_TRANSFROM},
		{ "Item_Rebar_Bone",REBAR_TRANSFROM},

		{ "Item_AutoPiton", new ItemPoseData(new Vector3(0f, 0f, 0.2f), Quaternion.Euler(0, 180, 0), Vector3.one, HAND_ROT) },
		{ "Item_Piton", PITON_TRANSFROM  },
		{ "Item_Piton_Holiday",PITON_TRANSFROM  },

		{ "Item_Rubble", new ItemPoseData(new Vector3(0, 0.15f, 0), Quaternion.Euler(90, 0, 0), Vector3.one, HAND_ROT) },

		{ "Item_Flaregun", new ItemPoseData(new Vector3(0f, 0.1f, 0.1f), Quaternion.Euler(90, 0, 0), Vector3.one, HAND_ROT) },
		{ "Item_Flaregun_Ammo", new ItemPoseData(new Vector3(0, 0.2f, 0), Quaternion.Euler(90, 90, 0), Vector3.one * 1.2f, HAND_ROT) },
		{ "Item_Flashlight", new ItemPoseData(new Vector3(0, 0.2f, 0), Quaternion.Euler(90, 0, 0), Vector3.one, HAND_ROT) },

		{ "Item_RhoStone", new ItemPoseData(new Vector3(0, 0.2f, 0), Quaternion.Euler(270, 0, 0), Vector3.one * 1.2f, HAND_ROT) },
		{ "Item_Cleaver", new ItemPoseData(new Vector3(0f, 0.4f, 0.1f), Quaternion.Euler(270, 270, 0), Vector3.one, HAND_ROT) },
		{ "Item_BlinkEye", BLINK_EYE_TRANSFROM },
		{ "Item_BlinkEye_Marionette", BLINK_EYE_TRANSFROM },

		{ "Item_Artifact_Translocator", new ItemPoseData(new Vector3(0f, 0.1f, 0.3f), Quaternion.Euler(270, 0, 0), Vector3.one, HAND_ROT) },
		{ "Item_Artifact_Rapier", new ItemPoseData(new Vector3(0, 0.1f, 0), Quaternion.Euler(270, 90, 0), Vector3.one, HAND_ROT) },
		{ "Item_Artifact_Rebar_Return", new ItemPoseData(new Vector3(0, 0.3f,0), Quaternion.Euler(270, 180, 0), Vector3.one, HAND_ROT) },
		{ "Item_Artifact_Timepiece", new ItemPoseData(new Vector3(0, 0.2f, 0), Quaternion.Euler(270, 0, 0), Vector3.one, HAND_ROT)},
		{ "Item_Artifact_Remote", new ItemPoseData(new Vector3(0f, 0.15f, 0.15f), Quaternion.Euler(315, 0, 0), Vector3.one, HAND_ROT) },
		{ "Item_Artifact_EVAGlove", new ItemPoseData(new Vector3(0f, 0f, -0.2f), Quaternion.Euler(0, 0, 0), Vector3.one * 1.2f, Quaternion.identity) },

		{ "Denizen_Roach_Flying_Ruby", new ItemPoseData(new Vector3(0f, 0.1f, 0.1f), Quaternion.Euler(0, 180, 0), Vector3.one, HAND_ROT) },
		{ "Denizen_Roach_Platinum",ROACH_TRANSFROM},
		{ "Denizen_Roach_Platinum_Navmesh",ROACH_TRANSFROM},
		{ "Denizen_Roach_Gold",ROACH_TRANSFROM},
		{ "Denizen_Roach_Gold_Navmesh",ROACH_TRANSFROM},
		{ "Denizen_Roach_Lemon",ROACH_TRANSFROM},

		{ "Denizen_SlugGrub", GRUB_TRANSFROM},
		{ "Denizen_SlugGrub_Christmas", GRUB_TRANSFROM},
		{ "Denizen_SlugGrub_Sam", GRUB_TRANSFROM},

		{ "Item_Floppy_T1",FLOPPY_TRANSFROM},
		{ "Item_Floppy_T2",FLOPPY_TRANSFROM},
		{ "Item_Floppy_T3",FLOPPY_TRANSFROM},
		{ "Item_Floppy_Variant",FLOPPY_TRANSFROM},
		{ "Item_Floppy_Test",FLOPPY_TRANSFROM},
		{ "Item_Floppy_T1_Notes",FLOPPY_TRANSFROM},
		{ "Item_Floppy_Lost",FLOPPY_TRANSFROM},

		{ "Item_EntityScanner", new ItemPoseData(new Vector3(0f, 0f, 0.25f), Quaternion.Euler(90, 90, 0), Vector3.one, HAND_ROT) },
		{ "Item_CandyCauldron", BLINK_EYE_TRANSFROM },
		{ "Item_CandyCauldron_Empty", BLINK_EYE_TRANSFROM },
		{ "Item_Cryogun", new ItemPoseData(new Vector3(0f, 0.1f, 0.2f), Quaternion.Euler(270, 180, 0), Vector3.one, HAND_ROT) },
		{ "Item_Powercell", new ItemPoseData(new Vector3(0f, 0f, 0.1f), Quaternion.Euler(270, 90, 0), Vector3.one, HAND_ROT) },
		{ "Item_Inoculator", new ItemPoseData(Vector3.zero, Quaternion.Euler(270, 90, 0), Vector3.one * 1.3f, HAND_ROT) },

		{ "Item_Bandage", new ItemPoseData(new Vector3(0, 0.2f, 0), Quaternion.Euler(0, 90, 90), Vector3.one, HAND_ROT) },

		{ "Item_Note_01",NOTE_TRANSFROM},
		{ "Item_Note_03_Torn",NOTE_TRANSFROM},
		{ "Item_Note_02_Bloody",NOTE_TRANSFROM},

		{ "Item_Radio", new ItemPoseData(new Vector3(0, 0.3f, 0), Quaternion.Euler(270, 0, 0), new Vector3(-1, 1, 1), HAND_ROT) },
		{ "Item_Rope", new ItemPoseData(new Vector3(0, 0.1f, 0), Quaternion.Euler(0, 45, 0), Vector3.one, HAND_ROT) },
		{ "Item_Temp", new ItemPoseData(new Vector3(0, 0.4f, 0), Quaternion.Euler(45, 45, 45), Vector3.one, HAND_ROT) },

		{ "Item_Trinket_Base", new ItemPoseData(new Vector3(0, 0.4f, 0), Quaternion.Euler(45, 45, 45), Vector3.one, HAND_ROT) },
		{ "Item_Trinket_Helmet", new ItemPoseData(new Vector3(-0.2f, 0.1f, 0.3f), Quaternion.Euler(270, 180, 0), Vector3.one, HAND_ROT) },
		{ "Item_Trinket_CalmingBuddy", FLOPPY_TRANSFROM },
		{ "Item_Trinket_Nugget", new ItemPoseData(new Vector3(0, 0.2f, 0), Quaternion.Euler(0, 90, 0), Vector3.one, HAND_ROT) },
		{ "Item_Trinket_Carabiner", new ItemPoseData(new Vector3(0, 0.15f, 0), Quaternion.Euler(0, 0, 0), Vector3.one, HAND_ROT) },
		{ "Item_Trinket_PhotoOfHome", new ItemPoseData(new Vector3(0, 0.2f, 0), Quaternion.Euler(270, 180, 0), Vector3.one, HAND_ROT) },
		{ "Item_Trinket_EmployeeID", FLOPPY_TRANSFROM },
		{ "Item_Trinket_Chalk", new ItemPoseData(new Vector3(0, 0.2f, 0), Quaternion.Euler(315, 0, 0), new Vector3(-1, 1, 1), HAND_ROT) },
		{ "Item_Trinket_Headlamp", new ItemPoseData(new Vector3(0, 0.15f, 0), Quaternion.Euler(315, 180, 0), Vector3.one, HAND_ROT) },
		{ "Item_Trinket_MassDamper", new ItemPoseData(new Vector3(0f, 0.15f, -0.1f), Quaternion.Euler(0, 270, 0), Vector3.one, HAND_ROT) },
		{ "Item_Trinket_MoonRock", new ItemPoseData(new Vector3(0, 0.2f, 0), Quaternion.Euler(270, 270, 0), Vector3.one, HAND_ROT) },
		{ "Item_Trinket_Beta", new ItemPoseData(new Vector3(-0.1f, 0.2f, 0f), Quaternion.Euler(270, 0, 0), new Vector3(-1, 1, 1), HAND_ROT) },
		{ "Item_Trinket_ClimbingShoes", new ItemPoseData(new Vector3(0, 0.1f, 0), Quaternion.Euler(0, 0, 90), Vector3.one, HAND_ROT) },
	};
	}

	public static readonly Quaternion HAND_ROT = Quaternion.Euler(0, 330, 0);

	// 锤子
	public static readonly ItemPoseData HAMMER_TRANSFROM
		= new ItemPoseData(new Vector3(0, 0.2f, 0), Quaternion.Euler(270, 90, 0), Vector3.one, HAND_ROT);

	// 藤壶钩
	public static readonly ItemPoseData BARNACLE_HOOK_TRANSFROM
		= new ItemPoseData(new Vector3(0f, 0f, 0.1f), Quaternion.identity, Vector3.one, HAND_ROT);

	// 手枪
	public static readonly ItemPoseData HANDGUN_TRANSFROM
		= new ItemPoseData(new Vector3(0f, 0.2f, 0.2f), Quaternion.Euler(0, 180, 90), Vector3.one, HAND_ROT);

	// 袋装食物
	public static readonly ItemPoseData BOXED_FOOD_TRANSFROM
		= new ItemPoseData(new Vector3(0, 0.2f, 0), Quaternion.Euler(0, 45, 0), Vector3.one, HAND_ROT);

	// 罐头类
	public static readonly ItemPoseData BEANS_TRANSFROM
		= new ItemPoseData(new Vector3(0, 0.2f, 0), Quaternion.Euler(0, 225, 0), Vector3.one, HAND_ROT);

	// 一次性食物
	public static readonly ItemPoseData FOOD_TRANSFROM
		= new ItemPoseData(new Vector3(0, 0.2f, 0), Quaternion.Euler(270, 0, 0), Vector3.one, HAND_ROT);

	// 可可
	public static readonly ItemPoseData COCOA_TRANSFROM
		= new ItemPoseData(new Vector3(-0.2f, 0f, 0f), Quaternion.Euler(0, 180, 0), Vector3.one, HAND_ROT);

	// 钢筋类
	public static readonly ItemPoseData REBAR_TRANSFROM
		= new ItemPoseData(new Vector3(0, 0.3f, 0), Quaternion.Euler(90, 270, 0), Vector3.one, HAND_ROT);

	// 岩钉
	public static readonly ItemPoseData PITON_TRANSFROM
		= new ItemPoseData(Vector3.zero, Quaternion.Euler(270, 180, 0), Vector3.one * 1.2f, HAND_ROT);

	// 眼球
	public static readonly ItemPoseData BLINK_EYE_TRANSFROM
		= new ItemPoseData(new Vector3(0, 0.2f, 0), Quaternion.Euler(0, 180, 0), Vector3.one, HAND_ROT);

	// 蟑螂
	public static readonly ItemPoseData ROACH_TRANSFROM
		= new ItemPoseData(new Vector3(0f, 0.1f, 0.1f), Quaternion.Euler(315, 0, 0), Vector3.one, HAND_ROT);

	// GRUB
	public static readonly ItemPoseData GRUB_TRANSFROM
		= new ItemPoseData(new Vector3(-0.1f, 0.1f, 0f), Quaternion.Euler(0, 0, 270), Vector3.one, HAND_ROT);

	// 软盘
	public static readonly ItemPoseData FLOPPY_TRANSFROM
		= new ItemPoseData(new Vector3(0f, 0.2f, 0f), Quaternion.Euler(270, 0, 0), Vector3.one, HAND_ROT);

	// 笔记
	public static readonly ItemPoseData NOTE_TRANSFROM
		= new ItemPoseData(new Vector3(0, 0.3f, 0), Quaternion.Euler(270, 0, 0), Vector3.one, HAND_ROT);
	#endregion

	private static readonly string[] TintColorProperties = { "_BaseColor", "_Color", "_MainColor" };

	[SerializeField]
	internal GameObject _gasMask;
	[SerializeField]
	internal GameObject _gearedUp;
	internal Dictionary<string, List<GameObject>> _itemGroups = new();

	[SerializeField]
	internal Renderer[] _tintRenderers;// 可以进行换色的渲染器

	public override void OnPrefabLoaded() {
		// 预先隐藏面具
		Transform mask = gameObject.transform.Find("SlugcatModel/SlugcatGasMask");
		if (mask != null) {
			_gasMask = mask.gameObject;
			_gasMask.SetActive(false);
		}

		// 预先隐藏装备组
		Transform gearedUp = gameObject.transform.Find("SlugcatModel/SlugcatGearedUp");
		if (gearedUp != null) {
			_gearedUp = gearedUp.gameObject;
		}

		// 提前收集渲染器并存入 _tintRenderers 序列化字段
		List<Renderer> renderersList = new();
		foreach (var r in gameObject.GetComponentsInChildren<Renderer>(true)) {
			if (gearedUp && r.transform.IsChildOf(gearedUp)) continue;
			if (mask && r.transform.IsChildOf(mask)) continue;
			if (r.GetComponent<TMP_Text>()) continue;

			renderersList.Add(r);
		}

		// 赋值给预制体模板, 克隆时会自动映射到每个子渲染器
		_tintRenderers = renderersList.ToArray();
	}

	/// <summary>
	/// 初始化回调
	/// </summary>
	public void Start() {
		if (_gearedUp != null) {
			foreach (Transform child in _gearedUp.transform) {
				int dotIndex = child.name.IndexOf('.');
				string baseName = dotIndex > 0 ? child.name.Substring(0, dotIndex) : child.name;

				if (!_itemGroups.TryGetValue(baseName, out var list)) {
					list = new List<GameObject>();
					_itemGroups[baseName] = list;
				}
				list.Add(child.gameObject);
				child.gameObject.SetActive(false);
			}

			foreach (var list in _itemGroups.Values)
				if (list.Count > 1) list.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
		}
	}

	/// <summary>
	/// 修改玩家颜色
	/// </summary>
	public override void ApplyPlayerColor(Color32 color) {
		foreach (var renderer in _tintRenderers) {

			Color targetColor = renderer.transform.name == "Eyes"
				? GetContrastColor(color)
				: (Color)color;

			foreach (var material in renderer.materials) {
				if (material == null) continue;

				foreach (var propertyName in TintColorProperties) {
					if (!material.HasProperty(propertyName)) continue;

					var current = material.GetColor(propertyName);
					material.SetColor(
						propertyName,
						new Color(targetColor.r, targetColor.g, targetColor.b,
							(byte)Mathf.Clamp(Mathf.RoundToInt(current.a * 255f), 0, 255)));
				}
			}
		}
	}

	/// <summary>
	/// 根据亮度计算高对比色
	/// </summary>
	private static Color GetContrastColor(Color color) {
		float luminance =
			0.299f * color.r +
			0.587f * color.g +
			0.114f * color.b;

		return luminance > 0.5f
			? Color.black
			: Color.white;
	}

	/// <summary>
	/// 切换玩家下蹲状态
	/// </summary>
	public override void SetCrouching(bool isCrouching) {

	}
	
	/// <summary>
	/// 根据玩家字典配置行为
	/// </summary>
	public override void HandlePlayerData(Dictionary<string, string> playerData) {
		// 遍历所有已收集的物品组
		foreach (var (baseName, objList) in _itemGroups) {
			// 确定目标启用数量 (未命中字典或解析失败时默认为 0)
			int targetCount = 0;
			if (playerData != null && playerData.TryGetValue(baseName, out string countStr))
				int.TryParse(countStr, out targetCount);

			// 遍历该物品组的所有实例
			for (int i = 0; i < objList.Count; i++) {
				GameObject itemObj = objList[i];
				bool shouldEnable = i < targetCount; // 索引小于目标数量的均启用

				// 性能优化：只有在状态真正发生变化时才调用 SetActive
				if (itemObj.activeSelf != shouldEnable) itemObj.SetActive(shouldEnable);
			}
		}
		if (playerData != null && playerData.TryGetValue("UseMask", out var value)
			&& bool.TryParse(value, out var result) && _gasMask.activeSelf != result) _gasMask.SetActive(result);
	}
}
