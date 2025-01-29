using System.Reflection;
using DebugPlus.Components;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace DebugPlus.Patches;

public class OnGameStartedPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(GameWorld), nameof(GameWorld.OnGameStarted));
    }
    
    [PatchPrefix]
    private static void PatchPrefix(GameWorld __instance)
    {
        __instance.GetOrAddComponent<PlayerComponent>();
        __instance.GetOrAddComponent<BotZoneRenderer>();
    }
}