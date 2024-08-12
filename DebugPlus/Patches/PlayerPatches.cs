using System.Reflection;
using DebugPlus.Config;
using EFT.HealthSystem;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace DebugPlus.Patches;

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
        if (!__instance.Player.IsYourPlayer || !DebugPlusConfig.GodMode.Value) return true;
        
        damage = 0f;
        return false;
    }
}