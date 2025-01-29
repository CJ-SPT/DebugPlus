using System.Reflection;
using DebugPlus.Config;
using EFT;
using EFT.HealthSystem;
using HarmonyLib;
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
        var player = (Player)_playerFieldInfo.GetValue(__instance);
        
        if (!player.IsYourPlayer || !DebugPlusConfig.GodMode.Value) return true;
        
        damage = 0f;
        return false;
    }
}