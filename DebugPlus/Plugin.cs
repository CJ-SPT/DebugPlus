using BepInEx;
using BepInEx.Logging;
using DebugPlus.Components;
using DebugPlus.Config;
using DebugPlus.Patches;
using EFT;
using UnityEngine;

#pragma warning disable

namespace DebugPlus
{
    [BepInPlugin("com.dirtbikercj.debugplus", "DebugPlus", "1.1.0")]
    public class Plugin : BaseUnityPlugin
    {
        public static Plugin Instance { get; private set; }
        public static ManualLogSource Log { get; private set; }
        
        internal void Awake()
        {
            Instance = this;
            DontDestroyOnLoad(this);
            
            Log = Logger;

            DebugPlusConfig.InitConfig(Config);
            
            #region LOGGIN_PATCHES

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

            #endregion

            #region PLAYER_PATCHES

            new GodModePatch().Enable();
            new OnGameStartedPatch().Enable();
            
            #endregion
        }
    }
}