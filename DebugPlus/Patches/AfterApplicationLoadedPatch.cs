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
        return AccessTools.Method(typeof(TarkovApplication), "method_38");
    }

    [PatchPostfix]
    public static void PatchPostfix()
    {
        if (DebugPlusConfig.LoadIntoFactoryOnStart.Value && TarkovApplication.Exist(out var app))
        {
            app.InternalStartGame("factory4_day",true, true);
        }
    }
}