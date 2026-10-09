using System.Reflection;
using DebugPlus.Config;
using DebugPlus.ConsoleCommands;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace DebugPlus.Patches;

public class AfterApplicationLoadedPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(TarkovApplication), nameof(TarkovApplication.OnApplicationLoaded));
    }

    [PatchPostfix]
    public static void PatchPostfix()
    {
        DebugPlus.Runtime.WorkbenchRuntime.Instance?.RefreshAccess();
    }
}
