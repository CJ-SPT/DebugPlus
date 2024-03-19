using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using DebugPlus.Config;
using DebugPlus.Patches;

#pragma warning disable

namespace DebugPlus
{
    [BepInPlugin("com.dirtbikercj.debugplus", "DebugPlus", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        public static Plugin? Instance;

        public static ManualLogSource Log;

        internal void Awake()
        {
            Instance = this;
            DontDestroyOnLoad(this);

            Log = Logger;

            DebugPlusConfig.InitConfig(Config);

            new LogPatch().Enable();
            new LogObjPatch().Enable();
            
            new LogFormatPatch().Enable();
            new LogFormatObjPatch().Enable();

            new LogWarningPatch().Enable();
            new LogWarningContextPatch().Enable();
            new LogWarningFormatPatch().Enable();
            new LogWarningFormatContextPatch().Enable();

            new LogErrorPatch().Enable();
            new LogErrorObjPatch().Enable();
            new LogErrorFormatPatch().Enable();
            new LogErrorFormatObjPatch().Enable();

            new LogExceptionPatch().Enable();
            new LogExceptionContextPatch().Enable();
        }
    }
}