using System.Reflection;
using DebugPlus.Config;
using EFT;
using EFT.HealthSystem;
using HarmonyLib;
using DebugPlus.Runtime;
using SPT.Reflection.Patching;

namespace DebugPlus.Patches;

/// <summary>
/// God mode
/// </summary>
public class GodModePatch : ModulePatch
{
    private static FieldInfo _playerFieldInfo = AccessTools.Field(typeof(ActiveHealthController), "Player");
    
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(ActiveHealthController), nameof(ActiveHealthController.ApplyDamage));
    }
    
    [PatchPrefix]
    private static bool Prefix(ActiveHealthController __instance, ref float damage)
    {
        if (DebugHealthScope.Active) return true;
        var player = _playerFieldInfo?.GetValue(__instance) as Player;
        if (WorkbenchRuntime.Instance?.GodMode(player) != true) return true;
        
        damage = 0f;
        return false;
    }
}

public class GodModeHealthPatch : ModulePatch
{
    private static readonly FieldInfo PlayerField = AccessTools.Field(typeof(ActiveHealthController), "Player");
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(ActiveHealthController), nameof(ActiveHealthController.ChangeHealth));
    [PatchPrefix]
    private static bool Prefix(ActiveHealthController __instance, ref float __1)
    {
        if (DebugHealthScope.Active || __1 >= 0) return true;
        return WorkbenchRuntime.Instance?.GodMode(PlayerField?.GetValue(__instance) as Player) != true;
    }
}
