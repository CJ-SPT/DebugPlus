using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace DebugPlus.Logging;

internal sealed class LoggingPatches : IDisposable
{
    private readonly Harmony _harmony = new("com.dirtbikercj.debugplus.logging");
    private static HarmonyMethod Method(string name) => new(typeof(LoggingPatches), name);
    public void Enable()
    {
        var failures = 0;
        foreach (var type in new[] { typeof(Debug), typeof(UnityEngine.Logger) })
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.Name.StartsWith("Log", StringComparison.Ordinal) && m.ReturnType == typeof(void)
                    && !m.IsGenericMethod && m.GetParameters().Length > 0))
                try { _harmony.Patch(method, prefix: Method(nameof(UnityPrefix)), finalizer: Method(nameof(UnityFinalizer))); }
                catch { failures++; }
        foreach (var method in typeof(AbstractLogger).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.Name.StartsWith("Log", StringComparison.Ordinal) || m.Name == "method_1"))
            try { _harmony.Patch(method, prefix: Method(nameof(NativePrefix)), finalizer: Method(nameof(NativeFinalizer))); }
            catch { failures++; }
        // Also cover a native forwarding handler invoked directly outside AbstractLogger.Log.
        var handler = typeof(AbstractLogger).GetNestedType("LogHandler", BindingFlags.Public | BindingFlags.NonPublic);
        if (handler != null)
            foreach (var method in handler.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.ReturnType == typeof(void) && m.Name != "Release"))
                try { _harmony.Patch(method, prefix: Method(nameof(NativePrefix)), finalizer: Method(nameof(NativeFinalizer))); }
                catch { failures++; }
        foreach (var ctor in typeof(AbstractLogger).GetConstructors())
            try { _harmony.Patch(ctor, postfix: Method(nameof(LoggerCreated))); } catch { failures++; }
        ClientLogging.Instance.HookStatus = failures == 0 ? "Unity and native capture hooks ready" : failures + " capture hooks unavailable; coverage is incomplete";
    }
    private static void LoggerCreated(AbstractLogger __instance) { try { ClientLogging.Discover(__instance.NLoggerName); } catch { } }
    private static void NativePrefix(out bool __state) { __state = true; CaptureOrigin.NativeDepth++; }
    private static Exception NativeFinalizer(Exception __exception, bool __state)
    { if (__state) CaptureOrigin.NativeDepth--; return __exception; }
    private static void UnityPrefix(MethodBase __originalMethod, object[] __args, out bool __state)
    {
        __state = true;
        var first = CaptureOrigin.UnityDepth++ == 0;
        if (!first || CaptureOrigin.NativeDepth > 0 || CaptureOrigin.InSink) return;
        try
        {
            var name = __originalMethod.Name;
            var type = name.Contains("Exception") ? LogType.Exception : name.Contains("Error") ? LogType.Error :
                name.Contains("Warning") ? LogType.Warning : name.Contains("Assertion") ? LogType.Assert : LogType.Log;
            var parameters = __originalMethod.GetParameters();
            object message = null, context = null; string tag = null; object[] arguments = null;
            for (var i = 0; i < parameters.Length; i++)
            {
                var parameter = parameters[i]; var value = __args[i];
                if (parameter.ParameterType == typeof(LogType)) type = (LogType)value;
                else if (parameter.ParameterType == typeof(UnityEngine.Object)) context = value;
                else if (parameter.ParameterType == typeof(object[])) arguments = value as object[];
                else if (parameter.ParameterType == typeof(LogOption)) { }
                else if (parameter.Name == "tag") tag = value as string;
                else message = value;
            }
            // Avoid accessing Unity object names from worker-thread logging calls.
            var detail = context == null ? "" : "Context type: " + context.GetType().FullName;
            var text = arguments == null ? CaptureOrigin.SafeString(message) : CaptureOrigin.Format(message as string, arguments);
            if (tag != null) text = tag + ": " + text;
            ClientLogging.Instance?.UnityLog(type, text, detail);
        }
        catch { }
    }
    private static Exception UnityFinalizer(Exception __exception, bool __state)
    { if (__state) CaptureOrigin.UnityDepth--; return __exception; }
    public void Dispose() => _harmony.UnpatchSelf();
}
