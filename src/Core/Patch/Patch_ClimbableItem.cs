using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;
using WKMPMod.World;

namespace WKMPMod.Patch;

/// <summary>
/// Patch: Piton命中时（放置钉子）
/// - Prefix: 记录当前已有的Handhold列表
/// - Postfix: 检测新生成的Piton并进行同步
///
/// Patch: Piton hit (placing a piton)
/// - Prefix: captures current existing handholds
/// - Postfix: detects newly spawned piton and syncs it
/// </summary>
[HarmonyPatch(typeof(HandItem_Piton), nameof(HandItem_Piton.PitonHit))]
public class Patch_HandItem_Piton_PitonHit {

    /// <summary>
    /// 在Piton生成前执行
    /// 保存当前Handhold状态，用于之后检测新增对象
    ///
    /// Runs before piton creation
    /// Stores current handhold state to detect newly created objects later
    /// </summary>
    public static void Prefix(out HashSet<int> __state) {
        __state = ClimbableItemSyncManager.CaptureExistingHandholds();
    }

    /// <summary>
    /// 在Piton生成后执行
    /// 查找新生成的可攀爬物体并注册同步
    ///
    /// Runs after piton creation
    /// Finds newly created climbable object and registers it for sync
    /// </summary>
    public static void Postfix(HandItem_Piton __instance, HashSet<int> __state) {
        ClimbableItemSyncManager.RegisterNewLocalPiton(__instance, __state);
    }
}

/// <summary>
/// Patch: 投射物碰撞命中时
/// - 用于处理由投射物生成的可攀爬对象（如射钉、钩点等）
///
/// Patch: Projectile collision hit
/// - Handles climbable objects spawned by projectiles (e.g. shootable pitons)
/// </summary>
[HarmonyPatch(typeof(Projectile), "OnCollisionHit")]
public class Patch_Projectile_OnCollisionHit_ClimbableSync {

    /// <summary>
    /// 碰撞前记录已有Handhold
    ///
    /// Captures existing handholds before collision
    /// </summary>
    public static void Prefix(out HashSet<int> __state) {
        __state = ClimbableItemSyncManager.CaptureExistingHandholds();
    }

    /// <summary>
    /// 碰撞后检测新生成的可攀爬对象并同步
    ///
    /// After collision, detects newly created climbable and syncs it
    /// </summary>
    public static void Postfix(Projectile __instance, RaycastHit hit, HashSet<int> __state) {
        ClimbableItemSyncManager.RegisterNewLocalProjectileClimbable(__instance, hit, __state);
    }
}

/// <summary>
/// Patch: Handhold被锤击（加固）时
/// - 触发一次强制同步更新
///
/// Patch: Handhold hammered (secured)
/// - Triggers a forced sync update
/// </summary>
[HarmonyPatch(typeof(CL_Handhold), nameof(CL_Handhold.HammerIn))]
public class Patch_CL_Handhold_HammerIn_PitonSync {

    /// <summary>
    /// 锤击后广播更新
    ///
    /// Broadcast update after hammering
    /// </summary>
    public static void Postfix(CL_Handhold __instance) {
        ClimbableItemSyncManager.BroadcastHammerUpdate(__instance);
    }
}

/// <summary>
/// Patch: Handhold FixedUpdate
/// - 定期同步Handhold状态（位置、旋转、secure状态等）
///
/// Patch: Handhold FixedUpdate
/// - Periodically syncs handhold state (position, rotation, secure state, etc.)
/// </summary>
[HarmonyPatch(typeof(CL_Handhold), "FixedUpdate")]
public class Patch_CL_Handhold_FixedUpdate_PitonSync {

    /// <summary>
    /// 每帧物理更新后尝试同步
    ///
    /// Attempts to sync after each physics update
    /// </summary>
    public static void Postfix(CL_Handhold __instance) {
        ClimbableItemSyncManager.BroadcastPeriodicUpdate(__instance);
    }
}

/// <summary>
/// Patch: Rope类型Handhold FixedUpdate
/// - 与普通Handhold一样进行周期同步
///
/// Patch: Rope handhold FixedUpdate
/// - Performs periodic sync similar to normal handholds
/// </summary>
[HarmonyPatch(typeof(CL_Handhold_Rope), "FixedUpdate")]
public class Patch_CL_Handhold_Rope_FixedUpdate_PitonSync {

    /// <summary>
    /// Rope Handhold同步
    ///
    /// Sync rope handhold
    /// </summary>
    public static void Postfix(CL_Handhold_Rope __instance) {
        ClimbableItemSyncManager.BroadcastPeriodicUpdate(__instance);
    }
}

/// <summary>
/// Patch: 可破坏Handhold Update
/// - 使用Update而不是FixedUpdate，因为其状态变化可能不是物理驱动
///
/// Patch: Breakable handhold Update
/// - Uses Update instead of FixedUpdate because changes may not be physics-driven
/// </summary>
[HarmonyPatch(typeof(CL_Handhold_Breakable), "Update")]
public class Patch_CL_Handhold_Breakable_Update_PitonSync {

    /// <summary>
    /// 每帧检查并同步破坏类Handhold状态
    ///
    /// Checks and syncs breakable handhold state every frame
    /// </summary>
    public static void Postfix(CL_Handhold_Breakable __instance) {
        ClimbableItemSyncManager.BroadcastPeriodicUpdate(__instance);
    }
}