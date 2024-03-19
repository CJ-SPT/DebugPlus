using Aki.Reflection.Patching;
using Aki.Reflection.Utils;
using DebugPlus.Config;
using DebugPlus.Utils;
using HarmonyLib;
using System;
using System.Reflection;
using UnityEngine;

namespace DebugPlus.Patches
{
    /// <summary>
    /// UnityEngine.Debug.LogException(Exception exception)
    /// </summary>
    internal class LogExceptionPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.GetDeclaredMethods(typeof(Debug))
                .SingleCustom(m => m.Name == nameof(Debug.LogException)
                && m.GetParameters()[0].Name == "exception"
                && m.GetParameters().Length == 1);
        }

        [PatchPostfix]
        public static void PatchPostfix(Exception exception)
        {
            if (!DebugPlusConfig.unityEngineDebugLogObj.Value) return;

            Plugin.Log.LogFatal(exception);
        }
    }

    /// <summary>
    /// UnityEngine.Debug.LogException(Exception exception, Object context)
    /// </summary>
    internal class LogExceptionContextPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.GetDeclaredMethods(typeof(Debug))
                .SingleCustom(m => m.Name == nameof(Debug.LogException)
                && m.GetParameters()[0].Name == "exception"
                && m.GetParameters().Length == 2);
        }

        [PatchPostfix]
        public static void PatchPostfix(Exception exception, UnityEngine.Object context)
        {
            if (!DebugPlusConfig.unityEngineDebugLogObj.Value) return;

            Plugin.Log.LogFatal($"GameObject: {context}\n {exception}");
        }
    }
}
