using Aki.Reflection.Patching;
using Aki.Reflection.Utils;
using DebugPlus.Config;
using DebugPlus.Utils;
using HarmonyLib;
using System.Reflection;
using UnityEngine;


namespace DebugPlus.Patches
{
    /// <summary>
    /// UnityEngine.Debug.LogFormat(string message, param object[] args)
    /// </summary>
    internal class LogFormatPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.GetDeclaredMethods(typeof(Debug))
                .SingleCustom(m => m.Name == nameof(Debug.LogFormat)
                && m.GetParameters()[0].Name == "format"
                && m.GetParameters().Length == 2);
        }

        [PatchPostfix]
        public static void PatchPostfix(string format, object[] args)
        {
            if (!DebugPlusConfig.unityEngineDebugLogObj.Value) return;

            Plugin.Log.LogInfo(Format.FormatString(format, args));
        }
    }

    /// <summary>
    /// UnityEngine.Debug.LogFormat(Object context, string message, param object[] args)
    /// </summary>
    internal class LogFormatObjPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.GetDeclaredMethods(typeof(Debug))
                .SingleCustom(m => m.Name == nameof(Debug.LogFormat)
                && m.GetParameters()[0].Name == "context"
                && m.GetParameters().Length == 3);
        }

        [PatchPostfix]
        public static void PatchPostfix(Object context, string format, object[] args)
        {
            if (!DebugPlusConfig.unityEngineDebugLogObj.Value) return;

            Plugin.Log.LogInfo($"GameObject: {context} \nMessage : {Format.FormatString(format, args)}");
        }
    }
}
