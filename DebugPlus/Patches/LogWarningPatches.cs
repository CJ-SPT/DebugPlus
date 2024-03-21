using Aki.Reflection.Patching;
using Aki.Reflection.Utils;
using DebugPlus.Config;
using DebugPlus.Utils;
using HarmonyLib;
using System.Reflection;
using System.Runtime.Remoting.Contexts;
using UnityEngine;

namespace DebugPlus.Patches
{
    /// <summary>
    /// UnityEngine.Debug.LogWarning(object message)
    /// </summary>
    internal class LogWarningPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.GetDeclaredMethods(typeof(Debug))
                .SingleDebug(m => m.Name == nameof(Debug.LogWarning)
                && m.GetParameters()[0].Name == "message"
                && m.GetParameters().Length == 1);
        }

        [PatchPostfix]
        public static void PatchPostfix(object message)
        {
            if (!DebugPlusConfig.UnityWarningLogging.Value) return;

            if (message.GetType() == typeof(string))
            {
                Plugin.Log.LogWarning(Format.FormatString((string)message));
                return;
            }

            Plugin.Log.LogWarning(message);
        }
    }

    /// <summary>
    /// UnityEngine.Debug.LogWarning(object message, Object context)
    /// </summary>
    internal class LogWarningContextPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.GetDeclaredMethods(typeof(Debug))
                .SingleDebug(m => m.Name == nameof(Debug.LogWarning)
                && m.GetParameters()[0].Name == "message"
                && m.GetParameters().Length == 2);
        }

        [PatchPostfix]
        public static void PatchPostfix(object message, Object context)
        {
            if (!DebugPlusConfig.UnityWarningLogging.Value) return;

            if (message.GetType() == typeof(string))
            {
                Plugin.Log.LogWarning($"GameObject: {context}\n{Format.FormatString((string)message)}");
                return;
            }

            Plugin.Log.LogWarning($"GameObject: {context}\n{message}");
        }
    }

    /// <summary>
    /// UnityEngine.Debug.LogWarningFormat(string format, object[] args)
    /// </summary>
    internal class LogWarningFormatPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.GetDeclaredMethods(typeof(Debug))
                .SingleDebug(m => m.Name == nameof(Debug.LogWarningFormat)
                && m.GetParameters()[0].Name == "format"
                && m.GetParameters().Length == 2);
        }

        [PatchPostfix]
        public static void PatchPostfix(string format, object[] args)
        {
            if (!DebugPlusConfig.UnityWarningLogging.Value) return;

            Plugin.Log.LogWarning(Format.FormatString(format, args));
        }
    }

    /// <summary>
    /// UnityEngine.Debug.LogWarningFormat(string format, object[] args)
    /// </summary>
    internal class LogWarningFormatContextPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.GetDeclaredMethods(typeof(Debug))
                .SingleDebug(m => m.Name == nameof(Debug.LogWarningFormat)
                && m.GetParameters()[0].Name == "context"
                && m.GetParameters().Length == 3);
        }

        [PatchPostfix]
        public static void PatchPostfix(Object context, string format, object[] args)
        {
            if (!DebugPlusConfig.UnityWarningLogging.Value) return;

            Plugin.Log.LogWarning($"GameObject: {context}\n{Format.FormatString(format, args)}");
        }
    }
}
