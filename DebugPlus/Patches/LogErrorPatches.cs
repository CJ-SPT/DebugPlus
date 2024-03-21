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
    /// UnityEngine.Debug.LogError(object message)
    /// </summary>
    internal class LogErrorPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.GetDeclaredMethods(typeof(Debug))
                .SingleDebug(m => m.Name == nameof(Debug.LogError)
                && m.GetParameters()[0].Name == "message"
                && m.GetParameters().Length == 1);
        }

        [PatchPostfix]
        public static void PatchPostfix(object message)
        {
            if (!DebugPlusConfig.UnityErrorLogging.Value) return;

            if (message.GetType() == typeof(string))
            {
                Plugin.Log.LogError(Format.FormatString((string)message));
                return;
            }

            Plugin.Log.LogError($"OBJECT: {message}");
        }
    }

    /// <summary>
    /// UnityEngine.Debug.LogError(object message, Object context)
    /// </summary>
    internal class LogErrorObjPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.GetDeclaredMethods(typeof(Debug))
                .SingleDebug(m => m.Name == nameof(Debug.LogError)
                && m.GetParameters()[0].Name == "message"
                && m.GetParameters().Length == 2);
        }

        [PatchPostfix]
        public static void PatchPostfix(object message, Object context)
        {
            if (!DebugPlusConfig.UnityErrorLogging.Value) return;

            Plugin.Log.LogError($"GameObject: {context}\nMessage: {Format.FormatString((string)message)}");
        }
    }

    /// <summary>
    /// UnityEngine.Debug.LogErrorFormat(string message, params object[] args)
    /// </summary>
    internal class LogErrorFormatPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.GetDeclaredMethods(typeof(Debug))
                .SingleDebug(m => m.Name == nameof(Debug.LogErrorFormat)
                && m.GetParameters()[0].Name == "format"
                && m.GetParameters().Length == 2);
        }

        [PatchPostfix]
        public static void PatchPostfix(string format, object[] args)
        {
            if (!DebugPlusConfig.UnityErrorLogging.Value) return;

            Plugin.Log.LogError(Format.FormatString(format, args));
        }
    }

    /// <summary>
    /// UnityEngine.Debug.LogErrorFormat(string message, params object[] args)
    /// </summary>
    internal class LogErrorFormatObjPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.GetDeclaredMethods(typeof(Debug))
                .SingleDebug(m => m.Name == nameof(Debug.LogErrorFormat)
                && m.GetParameters()[0].Name == "context"
                && m.GetParameters().Length == 3);
        }

        [PatchPostfix]
        public static void PatchPostfix(Object context, string format, object[] args)
        {
            if (!DebugPlusConfig.UnityErrorLogging.Value) return;

            Plugin.Log.LogError($"GameObject {context}\n{Format.FormatString(format, args)}");
        }
    }
}
