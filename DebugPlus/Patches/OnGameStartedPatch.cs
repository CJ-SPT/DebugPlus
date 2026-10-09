using System.Reflection;
using DebugPlus.Components;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;
using DebugPlus.Runtime;

namespace DebugPlus.Patches;

public class OnGameStartedPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(GameWorld), nameof(GameWorld.OnGameStarted));
    }

    [PatchPostfix]
    private static void PatchPrefix(GameWorld __instance)
    {
        if (!WorkbenchRuntime.IsRaidWorld(__instance)) return;
        WorkbenchRuntime.Instance?.BeginRaid(__instance);
        if (WorkbenchRuntime.Instance?.IsHeadless == true) return;
        __instance.GetOrAddComponent<PlayerComponent>();
        __instance.GetOrAddComponent<SpawnPointDebug>();
        __instance.GetOrAddComponent<DoorDebug>();
    }
}
