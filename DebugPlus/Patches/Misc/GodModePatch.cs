using System.Reflection;
using DebugPlus.Config;
using EFT.HealthSystem;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace DebugPlus.Patches.Misc;

/// <summary>
/// God mode
/// </summary>
public class GodModePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(ActiveHealthController), nameof(ActiveHealthController.ApplyDamage));
    }
    
    [PatchPrefix]
    private static bool Prefix(ActiveHealthController __instance, ref float damage)
    {
        var player = __instance.Player;
        if (!player.IsYourPlayer || !DebugPlusConfig.GodMode.Value) return true;
        
        damage = 0f;
        return false;
    }
}