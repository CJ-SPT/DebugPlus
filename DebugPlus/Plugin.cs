using System;
using BepInEx;
using BepInEx.Logging;
using DebugPlus.Config;
using DebugPlus.ConsoleCommands;
using DebugPlus.Patches;
using DrakiaXYZ.VersionChecker;
using EFT.UI;
using DebugPlus.Runtime;
using DebugPlus.UI;

#pragma warning disable

namespace DebugPlus;

[BepInPlugin("com.dirtbikercj.debugplus", "DebugPlus", BuildInfo.Version)]
public class Plugin : BaseUnityPlugin
{
    public const int TarkovVersion = 40743;

    public static Plugin Instance { get; private set; }
    public static ManualLogSource Log { get; private set; }

    internal void Awake()
    {
        if (!VersionChecker.CheckEftVersion(Logger, Info, Config))
        {
            throw new Exception("Invalid EFT Version");
        }

        Instance = this;
        DontDestroyOnLoad(this);

        Log = Logger;

        DebugPlusConfig.InitConfig(Config);
        gameObject.AddComponent<DebugPlus.Logging.ClientLogging>();
        var runtime = gameObject.AddComponent<WorkbenchRuntime>();
        if (!runtime.IsHeadless) gameObject.AddComponent<WorkbenchWindow>();

        #region LOGGIN_PATCHES

        // ClientLogging owns the consolidated Unity/native hooks and their lifetime.

        #endregion

        #region PLAYER_PATCHES

        new GodModePatch().Enable();
        new GodModeHealthPatch().Enable();
        new PauseBotUpdatePatch().Enable();
        new OnGameStartedPatch().Enable();

        #endregion

        #region OTHER

        new AfterApplicationLoadedPatch().Enable();

        #endregion

        if (!runtime.IsHeadless) RegisterCommands();
    }

    private static void RegisterCommands()
    {
        ConsoleScreen.Processor.RegisterCommand<SpawnBotsAsync>();
        ConsoleScreen.Processor.RegisterCommand<ReloadFromServerAsync>();
        ConsoleScreen.Processor.RegisterCommand<StartRaidAsync>();
    }
}
